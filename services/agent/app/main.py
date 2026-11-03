"""AI-ACE 全自动任务执行器 (agent)

给一个目标, 它自己拆步骤、调工具、看结果、失败重试, 直到做完或到上限.
和"聊天里点一次工具"不同, 这里是真正的循环: 模型 -> 工具 -> 结果 -> 模型 ...

- 模型: 栈内 llama.cpp (Qwen3-4B, 已开 --jinja 原生 function calling)
- 工具: 直接读 ace-file-tools 的 OpenAPI, 转成 OpenAI tools 下发
- 产物: documents/output/agent-results/*.md, 任务状态 documents/output/agent-tasks/*.json
- 记忆: documents/output/agent-memory/lessons.jsonl, 每个任务收尾写一条, 新任务按关键词+最近注入
- 并行: MAX_PARALLEL 个任务同时跑, 单个任务内多个工具调用并发(MAX_TOOL_WORKERS)
- 全离线: 只连 qwen3-text-service / ace-file-tools / ace-dashboard, 不出本机

无人值守模式: 把任务写进 documents/input/tasks/*.md(或 .txt), 服务自动领走,
跑完把报告写进 documents/output/agent-results/, 源文件移到 input/tasks/done/.
"""

from __future__ import annotations

import concurrent.futures
import json
import logging
import os
import re
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

NL = chr(10)

LOG = logging.getLogger("agent")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

WORK_DIR = Path(os.getenv("WORK_DIR", "/work")).resolve()
TASKS_IN = WORK_DIR / "documents" / "input" / "tasks"
RESULTS_DIR = WORK_DIR / "documents" / "output" / "agent-results"
STATE_DIR = WORK_DIR / "documents" / "output" / "agent-tasks"
MEMORY_DIR = WORK_DIR / "documents" / "output" / "agent-memory"
MEMORY_FILE = MEMORY_DIR / "lessons.jsonl"

LLM_URL = os.getenv("LLM_URL", "http://qwen3-text-service:8081").rstrip("/")
MODEL = os.getenv("MODEL", "Qwen3-4B")
TOOLS_URL = os.getenv("TOOLS_URL", "http://ace-file-tools:8010").rstrip("/")
DASHBOARD_URL = os.getenv("DASHBOARD_URL", "http://ace-dashboard:9000").rstrip("/")
DASHBOARD_CONTAINER = os.getenv("DASHBOARD_CONTAINER", "qwen3-text-service")

MAX_STEPS = int(os.getenv("MAX_STEPS", "12"))
MAX_SECONDS = int(os.getenv("MAX_SECONDS", "900"))
LLM_TIMEOUT = int(os.getenv("LLM_TIMEOUT", "180"))
TOOL_TIMEOUT = int(os.getenv("TOOL_TIMEOUT", "120"))
MAX_TOOL_CHARS = int(os.getenv("MAX_TOOL_CHARS", "3500"))
MAX_PARALLEL = int(os.getenv("MAX_PARALLEL", "2"))
MAX_TOOL_WORKERS = int(os.getenv("MAX_TOOL_WORKERS", "4"))
MEMORY_TOP = int(os.getenv("MEMORY_TOP", "4"))
MEMORY_KEEP = int(os.getenv("MEMORY_KEEP", "400"))
WATCH_ENABLED = os.getenv("WATCH_ENABLED", "true").lower() == "true"
WATCH_INTERVAL = int(os.getenv("WATCH_INTERVAL", "20"))

TASKS: dict[str, dict[str, Any]] = {}
LOCK = threading.RLock()
SLOTS = threading.Semaphore(MAX_PARALLEL)

SYSTEM_PROMPT = NL.join([
    "你是 AI-ACE 的全自动执行器, 会独立把任务做完, 不需要人来点继续.",
    "工作方式:",
    "1. 先用一句话说明打算怎么做, 然后马上开始调用工具, 不要只说不做.",
    "2. 每一步只做一件小事, 拿到结果再决定下一步.",
    "3. 读资料: list_files 看目录 -> read_document 读正文(返回 truncated 为 true 就按 next_offset 继续读) -> search_files / search_knowledge 找关键词.",
    "4. 写结果: write_file 写草稿, read_file 复查, 要正式文件就 generate_document 出 Word 或 Excel(自动存到 documents/output).",
    "5. 工具报错就改参数重试; 同一个工具连续失败 3 次就换方法, 不要死循环.",
    "6. 交付前必须自检: 写完用 read_file 读回来确认内容对不对; 用 list_files 确认文件真的存在; generate_document 出完再看返回里的 path 是否为空。核对不上就改, 不要直接结束.",
    "7. 做完后回答里写清楚: 做了什么、产出文件在哪个路径、还有什么没做完; 没验证成功的不许写成已完成.",
    "要求: 不要编造文件内容, 不确定就再读一次; 最后回答用中文, 不超过 300 字.",
])

app = FastAPI(
    title="AI-ACE 自动任务执行器",
    description=(
        "全自动执行器: 给一个目标, 自己多步调用工具(读写文件/执行代码/生成文档)直到完成. "
        "支持无人值守: 把任务写进 documents/input/tasks/ 即可."
    ),
    version="1.1.0",
)


class TaskIn(BaseModel):
    goal: str = Field(..., description="要完成的目标; 写清楚交付物, 例如: 把 documents 里的三份周报汇总成一份 Word")
    wait_seconds: int = Field(0, ge=0, le=600, description="等这么久再返回(秒); 0 表示立刻返回任务号, 之后用 agent_task_status 查")


class CancelIn(BaseModel):
    task_id: str = Field(..., description="任务号")


def _now() -> float:
    return time.time()


def _http_json(method: str, url: str, payload: Any = None, timeout: int = 60) -> tuple[int, Any]:
    data = None
    headers = {"Accept": "application/json"}
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", "replace")
            try:
                return resp.status, json.loads(raw)
            except Exception:
                return resp.status, raw
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        try:
            return exc.code, json.loads(raw)
        except Exception:
            return exc.code, raw


def _resolve_schema(node: Any, comps: dict[str, Any], depth: int = 0) -> Any:
    if depth > 6 or not isinstance(node, dict):
        return node
    ref = node.get("$ref")
    if isinstance(ref, str):
        name = ref.split("/")[-1]
        return _resolve_schema(comps.get(name) or {}, comps, depth + 1)
    out: dict[str, Any] = {}
    for key, val in node.items():
        if key == "properties" and isinstance(val, dict):
            out[key] = {k: _resolve_schema(v, comps, depth + 1) for k, v in val.items()}
        elif key == "items":
            out[key] = _resolve_schema(val, comps, depth + 1)
        elif key in ("oneOf", "anyOf", "allOf") and isinstance(val, list):
            out[key] = [_resolve_schema(x, comps, depth + 1) for x in val]
        else:
            out[key] = val
    return out


def _qs(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def _load_tools() -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    status, obj = _http_json("GET", TOOLS_URL + "/openapi.json", timeout=20)
    if status != 200 or not isinstance(obj, dict):
        raise RuntimeError("读不到工具清单 (file-tools): HTTP " + str(status))
    comps = ((obj.get("components") or {}).get("schemas") or {})
    tools: list[dict[str, Any]] = []
    index: dict[str, dict[str, Any]] = {}
    for path, item in (obj.get("paths") or {}).items():
        if not isinstance(item, dict):
            continue
        for method in ("get", "post"):
            op = item.get(method)
            if not isinstance(op, dict):
                continue
            opid = op.get("operationId")
            if not opid:
                continue
            desc = op.get("description") or op.get("summary") or opid
            props: dict[str, Any] = {}
            required: list[str] = []
            query_names: list[str] = []
            for prm in op.get("parameters") or []:
                name = prm.get("name")
                if not name or prm.get("in") != "query":
                    continue
                sch = dict(_resolve_schema(prm.get("schema") or {}, comps))
                sch["description"] = prm.get("description") or sch.get("description") or name
                props[name] = sch
                query_names.append(name)
                if prm.get("required") and name not in required:
                    required.append(name)
            body_names: list[str] | None = None
            content = ((op.get("requestBody") or {}).get("content") or {})
            if isinstance(content, dict) and "application/json" in content:
                sch = _resolve_schema((content["application/json"] or {}).get("schema") or {}, comps)
                body_props = sch.get("properties") or {}
                for key, val in body_props.items():
                    item_schema = dict(val) if isinstance(val, dict) else {}
                    item_schema.setdefault("description", key)
                    props[key] = item_schema
                body_names = list(body_props.keys())
                for name in sch.get("required") or []:
                    if name not in required:
                        required.append(name)
            tools.append({
                "type": "function",
                "function": {
                    "name": opid,
                    "description": desc,
                    "parameters": {"type": "object", "properties": props, "required": required},
                },
            })
            index[opid] = {"method": method.upper(), "path": path, "query": query_names, "body": body_names}
    return tools, index


CJK_RE = re.compile(r"[\u4e00-\u9fff]+")
WORD_RE = re.compile(r"[a-z0-9]+")


def _goal_tokens(text: str) -> set[str]:
    """目标/经验的分词: 中文按二字组, 英文数字按词; 够用且无第三方依赖."""
    low = (text or "").lower()
    out: set[str] = set()
    for chunk in CJK_RE.findall(low):
        for i in range(len(chunk) - 1):
            out.add(chunk[i:i + 2])
        if len(chunk) == 1:
            out.add(chunk)
    for word in WORD_RE.findall(low):
        if len(word) > 1:
            out.add(word)
    return out


def _load_lessons() -> list[dict[str, Any]]:
    """读经验库; 坏行直接跳过, 不让一条脏数据拖死整个服务."""
    try:
        if not MEMORY_FILE.exists():
            return []
        rows: list[dict[str, Any]] = []
        with MEMORY_FILE.open("r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except Exception:  # noqa: BLE001
                    continue
                if isinstance(obj, dict) and obj.get("goal"):
                    rows.append(obj)
        return rows
    except Exception:  # noqa: BLE001
        LOG.warning("read memory failed")
        return []


def _memory_block(goal: str) -> str:
    """取历史上最相关的几条经验, 拼成一段注入到任务目标后面."""
    if MEMORY_TOP <= 0:
        return ""
    rows = _load_lessons()
    if not rows:
        return ""
    want = _goal_tokens(goal)
    scored: list[tuple[int, float, dict[str, Any]]] = []
    for row in rows:
        have = _goal_tokens(str(row.get("goal") or "") + " " + str(row.get("answer") or ""))
        scored.append((len(want & have), float(row.get("ts") or 0), row))
    scored.sort(key=lambda item: (item[0], item[1]), reverse=True)
    picked = [row for _score, _ts, row in scored[: max(1, MEMORY_TOP)]]
    if not picked:
        return ""
    lines = ["[历史经验: 本机以前做过的任务, 只作参考, 不要照抄结论]"]
    for row in picked:
        when = time.strftime("%Y-%m-%d %H:%M", time.localtime(float(row.get("ts") or 0)))
        line = ("- " + when + " | 目标: " + str(row.get("goal") or "")[:80]
                + " | 结果: " + str(row.get("status") or "?")
                + " | 步数: " + str(row.get("steps") or 0)
                + " | 用时: " + str(row.get("seconds") or 0) + "s")
        if row.get("result_file"):
            line += " | 产出: " + str(row.get("result_file"))
        if row.get("error"):
            line += " | 错误: " + str(row.get("error"))[:80]
        lines.append(line)
    if any(str(row.get("status")) not in ("done", "cancelled") for row in picked):
        lines.append("注意: 上面有没跑成的任务, 先换做法, 别重复它的失败路径.")
    return NL.join(lines)


def _append_lesson(task: dict[str, Any]) -> None:
    """任务收尾写一条经验; 超过上限就只留最近的, 文件不会无限长."""
    row = {
        "ts": _now(),
        "id": str(task.get("id") or "")[:8],
        "goal": str(task.get("goal") or "")[:200],
        "status": str(task.get("status") or ""),
        "steps": len(task.get("steps") or []),
        "seconds": int(task.get("duration") or 0),
        "result_file": Path(str(task.get("result_file") or "")).name,
        "error": str(task.get("error") or "")[:160],
        "answer": str(task.get("answer") or "")[:300],
        "tools": sorted({str(c.get("name")) for step in (task.get("steps") or [])
                         for c in (step.get("calls") or [])})[:20],
    }
    with LOCK:
        try:
            MEMORY_DIR.mkdir(parents=True, exist_ok=True)
            with MEMORY_FILE.open("a", encoding="utf-8") as fh:
                fh.write(json.dumps(row, ensure_ascii=False) + NL)
            rows = _load_lessons()
            if len(rows) > MEMORY_KEEP:
                keep = rows[-MEMORY_KEEP:]
                MEMORY_FILE.write_text("".join(json.dumps(r, ensure_ascii=False) + NL for r in keep),
                                       encoding="utf-8")
        except Exception:  # noqa: BLE001
            LOG.warning("append lesson failed")


def _call_tool(index: dict[str, dict[str, Any]], name: str, args: dict[str, Any]) -> dict[str, Any]:
    spec = index.get(name)
    if not spec:
        return {"ok": False, "error": "没有这个工具: " + name}
    started = _now()
    try:
        if spec["method"] == "GET":
            params = {k: _qs(args[k]) for k in spec["query"] if k in args and args[k] is not None}
            url = TOOLS_URL + spec["path"]
            if params:
                url = url + "?" + urllib.parse.urlencode(params)
            status, out = _http_json("GET", url, timeout=TOOL_TIMEOUT)
        else:
            body = {k: args[k] for k in (spec["body"] or []) if k in args}
            status, out = _http_json("POST", TOOLS_URL + spec["path"], body, timeout=TOOL_TIMEOUT)
        text = out if isinstance(out, str) else json.dumps(out, ensure_ascii=False)
        return {
            "ok": 200 <= status < 300,
            "status": status,
            "text": text[:MAX_TOOL_CHARS],
            "ms": int((_now() - started) * 1000),
        }
    except Exception as exc:  # noqa: BLE001
        return {"ok": False, "error": str(exc), "ms": int((_now() - started) * 1000)}


def _chat(messages: list[dict[str, Any]], tools: list[dict[str, Any]]) -> dict[str, Any]:
    payload = {
        "model": MODEL,
        "messages": messages,
        "tools": tools,
        "tool_choice": "auto",
        "temperature": 0.2,
        "max_tokens": 1600,
    }
    last: Any = None
    for attempt in range(3):
        try:
            status, obj = _http_json("POST", LLM_URL + "/v1/chat/completions", payload, timeout=LLM_TIMEOUT)
            if status == 200 and isinstance(obj, dict):
                choices = obj.get("choices") or []
                if choices:
                    return choices[0].get("message") or {}
            last = "HTTP " + str(status) + ": " + str(obj)[:300]
        except Exception as exc:  # noqa: BLE001
            last = str(exc)
        time.sleep(2 + attempt * 3)
    raise RuntimeError("模型调用失败: " + str(last))


def _ensure_model() -> bool:
    for _ in range(2):
        try:
            status, _obj = _http_json("GET", LLM_URL + "/v1/models", timeout=4)
            if status == 200:
                return True
        except Exception:  # noqa: BLE001
            pass
        try:
            _http_json("POST", DASHBOARD_URL + "/api/models/" + DASHBOARD_CONTAINER + "/start", {}, timeout=30)
            LOG.info("asked dashboard to start %s", DASHBOARD_CONTAINER)
        except Exception:  # noqa: BLE001
            LOG.warning("dashboard start failed")
        for _ in range(40):
            time.sleep(4)
            try:
                status, _obj = _http_json("GET", LLM_URL + "/v1/models", timeout=4)
                if status == 200:
                    return True
            except Exception:  # noqa: BLE001
                continue
    return False


def _save(task: dict[str, Any]) -> None:
    try:
        STATE_DIR.mkdir(parents=True, exist_ok=True)
        text = json.dumps(task, ensure_ascii=False, indent=2)
        (STATE_DIR / (task["id"] + ".json")).write_text(text, encoding="utf-8")
    except Exception:  # noqa: BLE001
        LOG.warning("save state failed: %s", task.get("id"))


def _write_report(task: dict[str, Any]) -> str:
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    path = RESULTS_DIR / (stamp + "-" + task["id"][:8] + ".md")
    lines: list[str] = []
    lines.append("# 自动任务报告")
    lines.append("")
    lines.append("- 任务: " + task.get("goal", ""))
    lines.append("- 任务号: " + task["id"])
    lines.append("- 状态: " + task.get("status", ""))
    lines.append("- 用时: " + str(task.get("duration", 0)) + " 秒, 步数: " + str(len(task.get("steps") or [])))
    lines.append("- 模型: " + MODEL)
    if task.get("error"):
        lines.append("- 错误: " + str(task["error"]))
    lines.append("")
    lines.append("## 过程")
    lines.append("")
    for step in task.get("steps") or []:
        head = "### 第 " + str(step.get("n")) + " 步"
        lines.append(head)
        said = (step.get("content") or "").strip()
        if said:
            lines.append(said)
        for call in step.get("calls") or []:
            flag = "OK" if call.get("ok") else "FAIL"
            lines.append("- 调用 " + str(call.get("name")) + " [" + flag + ", " + str(call.get("ms")) + "ms]")
        lines.append("")
    lines.append("## 结果")
    lines.append("")
    lines.append((task.get("answer") or "(没有最终回答)").strip())
    lines.append("")
    path.write_text(NL.join(lines) + NL, encoding="utf-8")
    return str(path)


def _new_task(goal: str, source: str = "api") -> dict[str, Any]:
    task: dict[str, Any] = {
        "id": uuid.uuid4().hex,
        "goal": goal.strip(),
        "source": source,
        "status": "queued",
        "created": _now(),
        "steps": [],
        "answer": "",
        "error": "",
    }
    with LOCK:
        TASKS[task["id"]] = task
    _save(task)
    return task


def _start(task: dict[str, Any]) -> None:
    thread = threading.Thread(target=_run_with_slot, args=(task,), daemon=True)
    thread.start()


def _run_with_slot(task: dict[str, Any]) -> None:
    SLOTS.acquire()
    try:
        _run_agent(task)
    finally:
        SLOTS.release()


def _run_agent(task: dict[str, Any]) -> None:
    task["status"] = "running"
    task["started"] = _now()
    _save(task)
    try:
        if not _ensure_model():
            raise RuntimeError("模型没有就绪, 自动启动也失败: " + LLM_URL)
        tools, index = _load_tools()
        task["tools"] = len(tools)
        memory = _memory_block(task["goal"])
        task["memory_hits"] = max(0, memory.count(NL + "- ")) if memory else 0
        user_text = task["goal"] + (NL + NL + memory if memory else "")
        messages: list[dict[str, Any]] = [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user_text},
        ]
        deadline = _now() + MAX_SECONDS
        finished = False
        for step_no in range(1, MAX_STEPS + 1):
            if task.get("cancel"):
                task["status"] = "cancelled"
                finished = True
                break
            if _now() > deadline:
                task["status"] = "timeout"
                finished = True
                break
            message = _chat(messages, tools)
            content = (message.get("content") or "").strip()
            calls = message.get("tool_calls") or []
            record: dict[str, Any] = {"n": step_no, "content": content[:1500], "calls": []}
            if calls:
                messages.append({"role": "assistant", "content": content, "tool_calls": calls})
            else:
                messages.append({"role": "assistant", "content": content})
            if not calls:
                task["answer"] = content
                task["status"] = "done"
                record["final"] = True
                task["steps"].append(record)
                _save(task)
                finished = True
                break
            prepared: list[tuple[dict[str, Any], str, dict[str, Any]]] = []
            for call in calls:
                fn = (call or {}).get("function") or {}
                name = fn.get("name") or ""
                try:
                    args = json.loads(fn.get("arguments") or "{}")
                except Exception:  # noqa: BLE001
                    args = {}
                if not isinstance(args, dict):
                    args = {}
                prepared.append((call or {}, name, args))
            # 模型一次给出多个工具调用时并发执行: 慢工具(出文档/读大文件)不用一个个等
            if len(prepared) > 1:
                workers = max(1, min(MAX_TOOL_WORKERS, len(prepared)))
                with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
                    results = list(pool.map(lambda item: _call_tool(index, item[1], item[2]), prepared))
                record["parallel"] = len(prepared)
            else:
                results = [_call_tool(index, name, args) for _call, name, args in prepared]
            for (call, name, args), result in zip(prepared, results):
                messages.append({
                    "role": "tool",
                    "tool_call_id": (call or {}).get("id") or name,
                    "name": name,
                    "content": result.get("text") or json.dumps(result, ensure_ascii=False),
                })
                record["calls"].append({
                    "name": name,
                    "args": args,
                    "ok": result.get("ok"),
                    "status": result.get("status"),
                    "ms": result.get("ms"),
                })
            task["steps"].append(record)
            _save(task)
        if not finished:
            task["status"] = "max_steps"
    except Exception as exc:  # noqa: BLE001
        LOG.exception("task failed")
        task["status"] = "failed"
        task["error"] = str(exc)
    finally:
        started = task.get("started") or _now()
        task["finished"] = _now()
        task["duration"] = int(task["finished"] - started)
        try:
            task["result_file"] = _write_report(task)
        except Exception:  # noqa: BLE001
            LOG.exception("write report failed")
        _save(task)
        _append_lesson(task)


def _watch_loop() -> None:
    done_dir = TASKS_IN / "done"
    while True:
        try:
            if TASKS_IN.exists():
                for item in sorted(TASKS_IN.iterdir()):
                    if not item.is_file() or item.suffix.lower() not in (".txt", ".md"):
                        continue
                    goal = item.read_text(encoding="utf-8", errors="replace").strip()
                    if not goal:
                        continue
                    task = _new_task(goal, source="file:" + item.name)
                    LOG.info("picked task from file: %s -> %s", item.name, task["id"])
                    try:
                        done_dir.mkdir(parents=True, exist_ok=True)
                        item.replace(done_dir / item.name)
                    except Exception:  # noqa: BLE001
                        LOG.warning("move task file failed")
                    _start(task)
        except Exception:  # noqa: BLE001
            LOG.exception("watch loop failed")
        time.sleep(WATCH_INTERVAL)


@app.on_event("startup")
def _startup() -> None:
    for folder in (RESULTS_DIR, STATE_DIR, MEMORY_DIR, TASKS_IN):
        try:
            folder.mkdir(parents=True, exist_ok=True)
        except Exception:  # noqa: BLE001
            LOG.warning("mkdir failed: %s", folder)
    try:
        if STATE_DIR.exists():
            for item in STATE_DIR.glob("*.json"):
                data = json.loads(item.read_text(encoding="utf-8"))
                if isinstance(data, dict) and data.get("id"):
                    TASKS[data["id"]] = data
    except Exception:  # noqa: BLE001
        LOG.warning("load state failed")
    if WATCH_ENABLED:
        threading.Thread(target=_watch_loop, daemon=True).start()
        LOG.info("watcher on: %s", TASKS_IN)


@app.get("/health", include_in_schema=False)
def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "llm": LLM_URL,
        "model": MODEL,
        "tools": TOOLS_URL,
        "max_steps": MAX_STEPS,
        "max_parallel": MAX_PARALLEL,
        "max_tool_workers": MAX_TOOL_WORKERS,
        "memory": len(_load_lessons()),
        "watch": WATCH_ENABLED,
        "tasks": len(TASKS),
    }


@app.get("/tools", include_in_schema=False)
def list_tools() -> dict[str, Any]:
    try:
        tools, index = _load_tools()
        return {"count": len(tools), "names": sorted(index.keys())}
    except Exception as exc:  # noqa: BLE001
        return {"error": str(exc)}


@app.get("/memory", include_in_schema=False)
def get_memory(limit: int = 20) -> dict[str, Any]:
    rows = _load_lessons()
    take = max(1, min(limit, 200))
    return {"count": len(rows), "file": str(MEMORY_FILE), "items": rows[-take:][::-1]}


@app.delete("/memory", include_in_schema=False)
def clear_memory() -> dict[str, Any]:
    with LOCK:
        try:
            MEMORY_FILE.unlink(missing_ok=True)
        except Exception as exc:  # noqa: BLE001
            raise HTTPException(status_code=500, detail=str(exc))
    return {"status": "cleared"}


@app.post("/tasks")
def create_task(body: TaskIn) -> dict[str, Any]:
    goal = (body.goal or "").strip()
    if not goal:
        raise HTTPException(status_code=400, detail="goal 不能为空")
    task = _new_task(goal)
    _start(task)
    if body.wait_seconds > 0:
        limit = _now() + body.wait_seconds
        while _now() < limit:
            if task.get("status") in ("done", "failed", "max_steps", "timeout", "cancelled"):
                break
            time.sleep(2)
    return _public(task)


@app.get("/tasks")
def list_tasks(limit: int = 20) -> dict[str, Any]:
    with LOCK:
        items = sorted(TASKS.values(), key=lambda t: t.get("created", 0), reverse=True)[: max(1, limit)]
    return {"count": len(TASKS), "items": [_public(t) for t in items]}


@app.get("/tasks/{task_id}")
def get_task(task_id: str) -> dict[str, Any]:
    task = TASKS.get(task_id)
    if not task:
        raise HTTPException(status_code=404, detail="没有这个任务")
    return _public(task, full=True)


@app.post("/tasks/{task_id}/cancel")
def cancel_task(task_id: str) -> dict[str, Any]:
    task = TASKS.get(task_id)
    if not task:
        raise HTTPException(status_code=404, detail="没有这个任务")
    task["cancel"] = True
    _save(task)
    return {"status": "cancelling", "task_id": task_id}


def _public(task: dict[str, Any], full: bool = False) -> dict[str, Any]:
    out: dict[str, Any] = {
        "task_id": task["id"],
        "status": task.get("status"),
        "goal": task.get("goal"),
        "answer": task.get("answer") or "",
        "error": task.get("error") or "",
        "duration": task.get("duration"),
        "steps": len(task.get("steps") or []),
        "result_file": task.get("result_file"),
    }
    if full:
        out["trace"] = task.get("steps") or []
    return out
