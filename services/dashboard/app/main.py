from fastapi import FastAPI, BackgroundTasks, Body
from fastapi.responses import FileResponse, Response, StreamingResponse, JSONResponse
from pathlib import Path
import os
import json
import time
import urllib.request

import requests
import docker


app = FastAPI(title="AI-ACE Dashboard", version="1.0")


# ==========================
# 配置
# ==========================
MODELS_ENV = os.getenv(
    "MODELS",
    "qwen3-text-service:8081,vision-service-model:8083"
)
PARSER_URL = os.getenv("PARSER_URL", "http://ace-document-parser:8000")
RAG_URL = os.getenv("RAG_URL", "http://ace-rag-service:8002")
DOCX_URL = os.getenv("DOCX_URL", "http://ace-docx-generator:8003")

FRIENDLY = {
    "qwen3-text-service": {"name": "Qwen3-4B", "type": "文本", "desc": "通用中文文本模型"},
    "vision-service-model": {"name": "Qwen2.5-VL-3B", "type": "视觉", "desc": "图片理解 / OCR"},
}

STATIC_DIR = Path(__file__).parent / "static"


def get_docker():
    try:
        return docker.from_env()
    except Exception:
        return None


def parse_models():
    models = []
    for item in MODELS_ENV.split(","):
        item = item.strip()
        if not item:
            continue
        parts = item.split(":")
        name = parts[0]
        port = parts[1] if len(parts) > 1 else "8081"
        info = FRIENDLY.get(name, {"name": name, "type": "模型", "desc": ""})
        models.append({
            "container": name,
            "port": port,
            "display": info["name"],
            "type": info["type"],
            "desc": info["desc"]
        })
    return models


def read_gpu_json():
    try:
        p = Path("/system/gpu.json")
        if p.exists():
            return json.loads(p.read_text(encoding="utf-8"))
    except Exception:
        pass
    return {"name": "NVIDIA GPU", "memory_total_mb": 8192}


def probe_model(container, port):
    url = f"http://{container}:{port}/v1/models"
    try:
        urllib.request.urlopen(url, timeout=2)
        return True
    except Exception:
        return False


def get_vram(container):
    c = get_docker()
    if not c:
        return None
    try:
        ct = c.containers.get(container)
        res = ct.exec_run(
            "nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader,nounits"
        )
        if res.exit_code == 0:
            out = res.output.decode("utf-8", errors="ignore").strip()
            parts = out.split(",")
            if len(parts) >= 2:
                return {"used_mb": int(parts[0]), "total_mb": int(parts[1])}
    except Exception:
        pass
    return None


# ==========================
# 前端页面
# ==========================
@app.get("/", response_class=FileResponse)
def index():
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/health")
def health():
    return {"status": "ok", "service": "dashboard"}


# ==========================
# 系统状态
# ==========================
@app.get("/api/system")
def system():
    c = get_docker()
    gpu = read_gpu_json()
    if not c:
        return {
            "ok": False,
            "error": "无法连接 Docker",
            "gpu": gpu,
            "docker_version": "不可用",
            "ncpu": 0,
            "mem_total": 0,
            "containers_total": 0,
            "containers_running": 0
        }
    try:
        info = c.info()
        version = c.version()
        containers = c.containers.list(all=True)
        running = [x for x in containers if x.status == "running"]
        return {
            "ok": True,
            "docker_version": version.get("Version", "?"),
            "ncpu": info.get("NCPU", 0),
            "mem_total": info.get("MemTotal", 0),
            "containers_total": len(containers),
            "containers_running": len(running),
            "gpu": gpu
        }
    except Exception as e:
        return {"ok": False, "error": str(e), "gpu": gpu}


# ==========================
# 模型管理
# ==========================
@app.get("/api/models")
def models():
    c = get_docker()
    result = []
    for m in parse_models():
        status = "未创建"
        state = None
        vram = None
        if c:
            try:
                ct = c.containers.get(m["container"])
                state = ct.status
                if state == "running":
                    if probe_model(m["container"], m["port"]):
                        status = "运行"
                    else:
                        status = "加载中"
                elif state in ("exited", "created", "stopped", "paused"):
                    status = "停止"
                else:
                    status = state
                if state == "running":
                    vram = get_vram(m["container"])
            except Exception:
                status = "未创建"
        result.append({**m, "status": status, "state": state, "vram": vram})
    return result


@app.post("/api/models/{name}/start")
def start_model(name: str):
    c = get_docker()
    if not c:
        return {"status": "error", "message": "无法连接 Docker"}
    try:
        c.containers.get(name).start()
        return {"status": "ok", "action": "start", "model": name}
    except Exception as e:
        return {"status": "error", "message": str(e)}


@app.post("/api/models/{name}/stop")
def stop_model(name: str):
    c = get_docker()
    if not c:
        return {"status": "error", "message": "无法连接 Docker"}
    try:
        c.containers.get(name).stop()
        return {"status": "ok", "action": "stop", "model": name}
    except Exception as e:
        return {"status": "error", "message": str(e)}


# ==========================
# OpenAI 兼容网关 (模型按需启停 + 路由)
# 供 Open WebUI 单端点接入, 用户选模型即自动启动/切换
# ==========================
def _model_by_id(model_id):
    for m in parse_models():
        if m["display"] == model_id or m["container"] == model_id:
            return m
    return None


def _stop_other_models(target_container):
    """停止除目标外的所有模型容器, 释放 GPU 显存"""
    c = get_docker()
    if not c:
        return
    for m in parse_models():
        if m["container"] == target_container:
            continue
        try:
            ct = c.containers.get(m["container"])
            if ct.status == "running":
                ct.stop()
        except Exception:
            pass


def _ensure_model_running(target):
    """停止其他模型 → 启动目标 → 等待健康。返回是否就绪。"""
    c = get_docker()
    if not c:
        return False
    target_container = target["container"]
    target_port = target["port"]

    # 1. 停止其他模型 (释放显存)
    _stop_other_models(target_container)

    # 2. 启动目标模型
    try:
        ct = c.containers.get(target_container)
        if ct.status != "running":
            ct.start()
    except Exception:
        return False

    # 3. 等待健康 (最多约 5 分钟)
    for _ in range(150):
        if probe_model(target_container, target_port):
            return True
        time.sleep(2)
    return False


@app.get("/v1/models")
def v1_models():
    c = get_docker()
    data = []
    for m in parse_models():
        running = False
        if c:
            try:
                ct = c.containers.get(m["container"])
                running = ct.status == "running" and probe_model(m["container"], m["port"])
            except Exception:
                pass
        data.append({
            "id": m["display"],
            "object": "model",
            "created": 0,
            "owned_by": "ai-ace",
            "status": "running" if running else "stopped",
            "display_status": "运行中" if running else "未启动",
            "type": m["type"],
            "description": m["desc"],
        })
    return {"object": "list", "data": data}


@app.post("/v1/chat/completions")
def v1_chat_completions(payload: dict = Body(...)):
    model_id = payload.get("model", "")
    target = _model_by_id(model_id)
    if not target:
        return JSONResponse(
            status_code=404,
            content={"error": {"message": f"未知模型: {model_id}", "type": "invalid_request_error"}}
        )

    # 按需启动目标模型 (自动停止其他模型)
    if not _ensure_model_running(target):
        return JSONResponse(
            status_code=503,
            content={"error": {"message": f"模型 {model_id} 启动失败或超时", "type": "server_error"}}
        )

    # 转发请求到对应 llama.cpp 端点 (支持流式)
    upstream = f"http://{target['container']}:{target['port']}/v1/chat/completions"
    try:
        resp = requests.post(upstream, json=payload, stream=True, timeout=600)
    except Exception as e:
        return JSONResponse(
            status_code=502,
            content={"error": {"message": f"转发失败: {e}", "type": "server_error"}}
        )

    def gen():
        for chunk in resp.iter_content(chunk_size=1024):
            if chunk:
                yield chunk

    media = resp.headers.get("content-type", "text/event-stream")
    return StreamingResponse(gen(), media_type=media, status_code=resp.status_code)


# ==========================
# 容器列表
# ==========================
@app.get("/api/containers")
def containers():
    c = get_docker()
    if not c:
        return []
    result = []
    for ct in c.containers.list(all=True):
        if ct.name.startswith("ace-") or ct.name.startswith("open-webui") or \
           ct.name in ("qwen3-text-service", "vision-service-model"):
            result.append({
                "name": ct.name,
                "status": ct.status,
                "image": (ct.image.tags[0] if ct.image.tags else ct.image.id[:12])
            })
    return result


@app.get("/api/logs/{name}")
def logs(name: str, tail: int = 120):
    c = get_docker()
    if not c:
        return {"logs": "无法连接 Docker"}
    try:
        ct = c.containers.get(name)
        out = ct.logs(tail=tail).decode("utf-8", errors="ignore")
        return {"logs": out}
    except Exception as e:
        return {"logs": f"读取日志失败: {e}"}


# ==========================
# 知识库操作
# ==========================
@app.post("/api/knowledge/reindex")
def reindex():
    try:
        resp = requests.post(PARSER_URL + "/reindex", timeout=600)
        return resp.json()
    except Exception as e:
        return {"status": "error", "message": str(e)}


@app.post("/api/knowledge/load")
def load_knowledge():
    return reindex()


# ==========================
# 文档生成
# ==========================
@app.post("/api/document/generate")
def generate_document(payload: dict = Body(...)):
    try:
        resp = requests.post(DOCX_URL + "/generate", json=payload, timeout=60)
        return resp.json()
    except Exception as e:
        return {"status": "error", "message": str(e)}


@app.get("/api/document/download/{filename}")
def download_document(filename: str):
    try:
        resp = requests.get(DOCX_URL + "/download/" + filename, timeout=60)
        if resp.status_code != 200:
            return {"status": "error", "message": "文件不存在"}
        ct = resp.headers.get("content-type", "application/octet-stream")
        return Response(
            content=resp.content,
            media_type=ct,
            headers={"Content-Disposition": f'attachment; filename="{filename}"'}
        )
    except Exception as e:
        return {"status": "error", "message": str(e)}


# ==========================
# 关闭系统
# ==========================
@app.post("/api/shutdown")
def shutdown(background_tasks: BackgroundTasks):
    c = get_docker()
    if not c:
        return {"status": "error", "message": "无法连接 Docker"}

    stopped = []
    # 先停模型
    for m in parse_models():
        try:
            c.containers.get(m["container"]).stop()
            stopped.append(m["container"])
        except Exception:
            pass
    # 再停核心服务 (保留自身)
    for ct in c.containers.list(all=True):
        if ct.name == "ace-dashboard":
            continue
        if ct.name.startswith("ace-") or ct.name == "open-webui":
            try:
                ct.stop()
                stopped.append(ct.name)
            except Exception:
                pass

    def stop_self():
        time.sleep(2)
        try:
            c.containers.get("ace-dashboard").stop()
        except Exception:
            pass

    background_tasks.add_task(stop_self)
    return {"status": "ok", "stopped": stopped, "message": "系统正在关闭"}
