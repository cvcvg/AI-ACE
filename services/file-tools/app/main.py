"""AI-ACE 本地文件工具服务 (file-tools)

把 documents 目录交给 AI 使用:
- 读: 列目录 / 读文本全文 / 读 PDF·Word·Excel·PPT 正文 / 看图片 / 全文检索 / 知识库检索
- 写: 在整个工作目录(AI-ACE 根)写文件、改文件、执行 Python/shell、生成 Word·Excel 成品(数据目录仍受保护)
- 自动: 往 input 放文件会自动重建知识库索引。

全离线: 只连栈内服务 (document-parser, rag-service, vision-service), 不访问公网。
运行在 open-webui 镜像内 (已自带 pypdf / docx2txt / python-pptx / openpyxl / pillow),
源码只读挂载, 不需要单独构建镜像。
"""

from __future__ import annotations

import base64
import html
import json
import logging
import os
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path, PurePosixPath
from typing import Any

from fastapi import FastAPI, HTTPException, Query
from pydantic import BaseModel, Field

LOG = logging.getLogger("file-tools")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
WATCH_DIR = Path(os.getenv("WATCH_DIR", str(DATA_DIR / "input"))).resolve()
PARSER_URL = os.getenv("PARSER_URL", "http://ace-document-parser:8000").rstrip("/")
RAG_URL = os.getenv("RAG_URL", "http://ace-rag-service:8002").rstrip("/")
VISION_URL = os.getenv("VISION_URL", "http://vision-service-model:8083").rstrip("/")
VISION_MODEL = os.getenv("VISION_MODEL", "Qwen2.5-VL-3B")

WATCH_ENABLED = os.getenv("WATCH_ENABLED", "true").lower() == "true"
WATCH_INTERVAL = int(os.getenv("WATCH_INTERVAL", "15"))
STABLE_SECONDS = int(os.getenv("STABLE_SECONDS", "6"))
REINDEX_MIN_GAP = int(os.getenv("REINDEX_MIN_GAP", "90"))
INDEX_ON_START = os.getenv("INDEX_ON_START", "false").lower() == "true"

# 单文件处理上限: 50MB (读取阶段还会按需截断, 不会把整本书塞进模型)
MAX_FILE_BYTES = int(os.getenv("MAX_FILE_BYTES", str(50 * 1024 * 1024)))
# 单次提取的文本总长上限, 防止超大 Excel/PDF 撑爆内存
MAX_EXTRACT_CHARS = int(os.getenv("MAX_EXTRACT_CHARS", str(3_000_000)))

TEXT_SUFFIXES = {
    ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".yaml", ".yml",
    ".toml", ".ini", ".cfg", ".conf", ".log", ".py", ".js", ".ts", ".java", ".go",
    ".c", ".h", ".cpp", ".cs", ".rs", ".sh", ".bat", ".ps1", ".sql", ".xml", ".html",
    ".htm", ".css", ".tex", ".rst", ".env", ".properties", ".srt", ".vtt",
}
DOC_SUFFIXES = {".pdf", ".docx", ".pptx", ".xlsx", ".xlsm", ".html", ".htm"}
IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"}

EXTRACT_CACHE: dict[str, tuple[float, int, str, str]] = {}
STATE: dict[str, Any] = {
    "last_reindex": None,
    "last_reindex_ok": None,
    "last_reindex_files": 0,
    "last_reindex_msg": "",
    "pending": False,
    "started": time.strftime("%Y-%m-%dT%H:%M:%S"),
}
LOCK = threading.Lock()

app = FastAPI(
    title="AI-ACE 本地文件工具",
    description=(
        "Claude 式本地助手: 读 documents(文本/PDF/Word/Excel/PPT/图片)、检索知识库, "
        "并能读写整个 AI-ACE 工作目录、执行代码、生成 Word/Excel 成品。"
    ),
    version="3.4.0",
)


# ---------------------------------------------------------------- 基础工具


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def _rel(path: Path) -> str:
    for base in (DATA_DIR, OUTPUT_DIR):
        try:
            return path.relative_to(base).as_posix()
        except ValueError:
            continue
    return path.name


def _resolve(rel: str) -> Path:
    """把相对路径解析到 documents 目录; documents 里没有时, 再到工作目录(AI-ACE 根)下找。"""
    raw = (rel or "").strip().replace("\\", "/").lstrip("/")
    if not raw:
        raise HTTPException(status_code=400, detail="path 不能为空")
    outside = 0
    for base in (DATA_DIR, OUTPUT_DIR):
        target = (base / PurePosixPath(raw)).resolve()
        try:
            target.relative_to(base)
        except ValueError:
            outside += 1
            continue
        if target.exists():
            if not target.is_file():
                raise HTTPException(status_code=400, detail=f"这是目录, 不是文件: {raw}")
            return target
    if outside == 2:
        raise HTTPException(status_code=400, detail="路径越界, 只允许 documents 目录或 AI-ACE 工作目录内的文件")
    raise HTTPException(status_code=404, detail=f"文件不存在: {raw}")


def _size_ok(path: Path) -> None:
    size = path.stat().st_size
    if size > MAX_FILE_BYTES:
        mb = MAX_FILE_BYTES / 1024 / 1024
        raise HTTPException(
            status_code=413,
            detail=f"文件 {size / 1024 / 1024:.1f}MB 超过单文件上限 {mb:.0f}MB, 请先在本地拆分",
        )


def _decode(raw: bytes) -> str:
    for enc in ("utf-8-sig", "utf-8", "gb18030", "big5", "latin-1"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def _read_text(path: Path, limit: int | None = None) -> str:
    _size_ok(path)
    raw = path.read_bytes()[: limit or MAX_FILE_BYTES]
    return _decode(raw)


def _numbered(text: str, start_line: int, max_lines: int, max_chars: int) -> dict:
    lines = text.splitlines()
    total = len(lines)
    begin = max(start_line - 1, 0)
    chunk = lines[begin: begin + max_lines]
    out: list[str] = []
    used = 0
    last = begin
    for idx, line in enumerate(chunk, start=begin + 1):
        piece = f"{idx:6d}| {line}"
        if used + len(piece) + 1 > max_chars and out:
            break
        out.append(piece)
        used += len(piece) + 1
        last = idx
    return {
        "text": "\n".join(out),
        "start_line": begin + 1,
        "end_line": last,
        "total_lines": total,
        "truncated": last < total,
    }


# ---------------------------------------------------------------- 文档解析


def _extract_pdf(path: Path) -> str:
    from pypdf import PdfReader

    reader = PdfReader(str(path))
    parts: list[str] = []
    used = 0
    for i, page in enumerate(reader.pages, 1):
        try:
            body = (page.extract_text() or "").strip()
        except Exception:  # 个别损坏页跳过, 不影响其它页
            body = ""
        block = f"\n\n[第 {i} 页]\n{body}"
        parts.append(block)
        used += len(block)
        if used > MAX_EXTRACT_CHARS:
            parts.append("\n\n[已达提取上限, 后面的页没有读取]")
            break
    return "".join(parts).strip()


def _extract_docx(path: Path) -> str:
    import docx2txt

    text = docx2txt.process(str(path)) or ""
    return re.sub(r"\n{3,}", "\n\n", text).strip()


def _extract_pptx(path: Path) -> str:
    from pptx import Presentation

    prs = Presentation(str(path))
    parts: list[str] = []
    for i, slide in enumerate(prs.slides, 1):
        buf: list[str] = []
        for shape in slide.shapes:
            if not getattr(shape, "has_text_frame", False):
                continue
            txt = (shape.text_frame.text or "").strip()
            if txt:
                buf.append(txt)
        body = "\n".join(buf).strip()
        parts.append(f"\n\n[第 {i} 页幻灯片]\n{body}")
    return "".join(parts).strip()


def _extract_xlsx(path: Path) -> str:
    import openpyxl

    wb = openpyxl.load_workbook(str(path), read_only=True, data_only=True)
    parts: list[str] = []
    used = 0
    for ws in wb.worksheets:
        parts.append(f"\n\n[工作表 {ws.title}]")
        used += len(parts[-1])
        for row in ws.iter_rows(values_only=True):
            cells = [("" if c is None else str(c)).strip() for c in row]
            if not any(cells):
                continue
            line = "|".join(cells).rstrip("|")
            parts.append(line)
            used += len(line) + 1
            if used > MAX_EXTRACT_CHARS:
                parts.append("[已达提取上限, 后面的行没有读取]")
                wb.close()
                return "\n".join(parts).strip()
    wb.close()
    return "\n".join(parts).strip()


def _extract_html(path: Path) -> str:
    raw = _read_text(path)
    raw = re.sub(r"(?is)<(script|style)[^>]*>.*?</\1>", " ", raw)
    raw = re.sub(r"(?s)<br\s*/?>|</p>|</div>|</tr>|</h[1-6]>", "\n", raw)
    raw = re.sub(r"(?s)<[^>]+>", " ", raw)
    return re.sub(r"[ \t]{2,}", " ", html.unescape(raw)).strip()


def _suffix(path: Path) -> str:
    return path.suffix.lower()


def _kind(path: Path) -> str:
    s = _suffix(path)
    if s in IMAGE_SUFFIXES:
        return "image"
    if s == ".pdf":
        return "pdf"
    if s == ".docx":
        return "docx"
    if s == ".pptx":
        return "pptx"
    if s in {".xlsx", ".xlsm"}:
        return "xlsx"
    if s in {".html", ".htm"}:
        return "html"
    if s in TEXT_SUFFIXES:
        return "text"
    return "unknown"


def _extract(path: Path) -> tuple[str, str]:
    """返回 (kind, 正文文本)。结果按 (mtime, size) 缓存, 避免反复解析大文件。"""
    key = str(path)
    st = path.stat()
    cached = EXTRACT_CACHE.get(key)
    if cached and cached[0] == st.st_mtime and cached[1] == st.st_size:
        return cached[2], cached[3]

    _size_ok(path)
    kind = _kind(path)
    if kind == "pdf":
        text = _extract_pdf(path)
    elif kind == "docx":
        text = _extract_docx(path)
    elif kind == "pptx":
        text = _extract_pptx(path)
    elif kind == "xlsx":
        text = _extract_xlsx(path)
    elif kind == "html":
        text = _extract_html(path)
    elif kind == "text":
        text = _read_text(path)
    else:
        raise HTTPException(
            status_code=400,
            detail=f"看不懂这种文件: {path.name}. 支持 PDF / Word(.docx) / Excel / PPT / 图片 / 文本类",
        )
    if len(EXTRACT_CACHE) > 200:
        EXTRACT_CACHE.clear()
    EXTRACT_CACHE[key] = (st.st_mtime, st.st_size, kind, text)
    return kind, text


def _page_hint(kind: str, text: str) -> str:
    if kind == "pdf":
        n = len(re.findall(r"\[第 \d+ 页\]", text))
        return f"PDF, 共 {n} 页"
    if kind == "pptx":
        n = len(re.findall(r"\[第 \d+ 页幻灯片\]", text))
        return f"PPT, 共 {n} 页幻灯片"
    if kind == "xlsx":
        n = len(re.findall(r"\[工作表 ", text))
        return f"Excel, 共 {n} 个工作表"
    return kind


# ---------------------------------------------------------------- 内部 HTTP


def _post_json(url: str, payload: dict | None, timeout: int = 60) -> dict:
    data = json.dumps(payload or {}).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8", errors="replace"))


def _get_json(url: str, timeout: int = 10) -> dict:
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8", errors="replace"))


# ---------------------------------------------------------------- 工具接口


@app.get("/health", summary="健康检查", include_in_schema=False)
def health() -> dict:
    parser = "unknown"
    try:
        _get_json(f"{PARSER_URL}/health", timeout=3)
        parser = "ok"
    except Exception:
        parser = "unreachable"
    vision = "unknown"
    try:
        _get_json(f"{VISION_URL}/health", timeout=3)
        vision = "ok"
    except Exception:
        vision = "unreachable"
    return {
        "status": "ok",
        "data_dir": str(DATA_DIR),
        "watch_dir": str(WATCH_DIR),
        "watch_enabled": WATCH_ENABLED,
        "parser": parser,
        "vision": vision,
        "max_file_mb": MAX_FILE_BYTES // (1024 * 1024),
        "last_reindex": STATE["last_reindex"],
    }


@app.get(
    "/files/list",
    operation_id="list_files",
    summary="列出 documents 目录里的文件",
)
def list_files(
    subdir: str = Query("", description="子目录, 例如 input / 报告。留空表示 documents 根目录"),
    recursive: bool = Query(True, description="是否包含子目录"),
    limit: int = Query(300, ge=1, le=2000),
) -> dict:
    base = DATA_DIR
    if subdir.strip():
        base = _resolve_dir(subdir)
    items: list[dict] = []
    iterator = base.rglob("*") if recursive else base.glob("*")
    for path in sorted(iterator):
        if len(items) >= limit:
            break
        if not path.is_file():
            continue
        try:
            rel = _rel(path)
        except ValueError:
            continue
        st = path.stat()
        items.append({
            "path": rel,
            "name": path.name,
            "size": st.st_size,
            "size_human": _human(st.st_size),
            "modified": time.strftime("%Y-%m-%d %H:%M", time.localtime(st.st_mtime)),
            "kind": _kind(path),
            "text": _suffix(path) in TEXT_SUFFIXES,
        })
    return {"count": len(items), "limit": limit, "files": items}


def _resolve_dir(rel: str) -> Path:
    raw = (rel or "").strip().replace("\\", "/").lstrip("/")
    target = (DATA_DIR / PurePosixPath(raw)).resolve()
    try:
        target.relative_to(DATA_DIR)
    except ValueError:
        raise HTTPException(status_code=400, detail="路径越界, 只允许 documents 目录内的路径")
    if not target.is_dir():
        raise HTTPException(status_code=404, detail=f"目录不存在: {rel}")
    return target


def _human(n: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.0f}{unit}" if unit == "B" else f"{n:.1f}{unit}"
        n /= 1024.0
    return f"{n:.1f}GB"


@app.get(
    "/files/read",
    operation_id="read_file",
    summary="读取纯文本文件(带行号), 可指定起始行",
)
def read_file(
    path: str = Query(..., description="文件路径, 相对 documents 目录, 例如 input/笔记.txt"),
    start_line: int = Query(1, ge=1, description="从第几行开始, 默认 1"),
    max_lines: int = Query(200, ge=1, le=2000),
    max_chars: int = Query(4000, ge=200, le=20000),
) -> dict:
    target = _resolve(path)
    if _suffix(target) not in TEXT_SUFFIXES:
        raise HTTPException(
            status_code=400,
            detail="这不是纯文本文件, 请用 read_document 读 PDF / Word / Excel / PPT 正文",
        )
    text = _read_text(target)
    data = _numbered(text, start_line, max_lines, max_chars)
    data.update({"path": _rel(target), "kind": "text", "size": target.stat().st_size})
    return data


@app.get(
    "/documents/read",
    operation_id="read_document",
    summary="读取 PDF / Word / Excel / PPT / 文本 的正文(按字符分页). 长文档先读第一页拿 next_offset, 再一段段继续, 直到 truncated=false",
)
def read_document(
    path: str = Query(..., description="文件路径, 相对 documents 目录"),
    offset: int = Query(0, ge=0, description="从第几个字符开始, 上次返回的 next_offset 直接填这里"),
    max_chars: int = Query(4000, ge=500, le=20000, description="本次最多返回多少字"),
) -> dict:
    target = _resolve(path)
    kind, text = _extract(target)
    total = len(text)
    if total == 0:
        return {
            "path": _rel(target), "kind": kind, "total_chars": 0,
            "offset": 0, "next_offset": None, "truncated": False,
            "text": "(没有提取到文字。如果是扫描件或图片型 PDF, 请用 describe_image 看图)",
        }
    begin = min(offset, total)
    end = min(begin + max_chars, total)
    return {
        "path": _rel(target),
        "kind": kind,
        "hint": _page_hint(kind, text),
        "total_chars": total,
        "offset": begin,
        "next_offset": end if end < total else None,
        "truncated": end < total,
        "text": text[begin:end],
    }


@app.get(
    "/documents/outline",
    operation_id="outline_document",
    include_in_schema=False,
    summary="先看文档目录/结构(PDF 每页首行、Excel 工作表、PPT 每页标题、文本前几行)",
)
def outline_document(
    path: str = Query(..., description="文件路径, 相对 documents 目录"),
    limit: int = Query(60, ge=5, le=300, description="最多列出多少条结构信息"),
) -> dict:
    target = _resolve(path)
    kind, text = _extract(target)
    items: list[dict] = []
    if kind == "pdf":
        for m in re.finditer(r"\[第 (\d+) 页\]\s*\n?(.{0,80})", text):
            page = int(m.group(1))
            head = m.group(2).strip().splitlines()[0] if m.group(2).strip() else "(本页无文字)"
            items.append({"locator": f"第 {page} 页", "head": head})
            if len(items) >= limit:
                break
    elif kind == "pptx":
        for m in re.finditer(r"\[第 (\d+) 页幻灯片\]\s*\n?(.{0,80})", text):
            page = int(m.group(1))
            head = m.group(2).strip().splitlines()[0] if m.group(2).strip() else "(本页无文字)"
            items.append({"locator": f"第 {page} 页幻灯片", "head": head})
            if len(items) >= limit:
                break
    elif kind == "xlsx":
        for m in re.finditer(r"\[工作表 ([^\]]+)\]", text):
            items.append({"locator": f"工作表 {m.group(1)}", "head": ""})
            if len(items) >= limit:
                break
    else:
        lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
        for i, line in enumerate(lines[:limit], 1):
            items.append({"locator": f"第 {i} 行", "head": line[:80]})
    return {
        "path": _rel(target),
        "kind": kind,
        "hint": _page_hint(kind, text),
        "total_chars": len(text),
        "items": items,
        "truncated_items": len(items) >= limit,
    }


@app.get(
    "/documents/search",
    operation_id="search_in_file",
    include_in_schema=False,
    summary="在单个文件(含 PDF/Word/Excel/PPT)里搜关键词, 返回命中上下文与字符位置",
)
def search_in_file(
    path: str = Query(..., description="文件路径, 相对 documents 目录"),
    query: str = Query(..., min_length=1, description="要搜的词"),
    ignore_case: bool = Query(True),
    limit: int = Query(10, ge=1, le=50),
    context_chars: int = Query(120, ge=20, le=400),
) -> dict:
    target = _resolve(path)
    kind, text = _extract(target)
    needle = query if not ignore_case else query.lower()
    hay = text if not ignore_case else text.lower()
    hits: list[dict] = []
    start = 0
    while len(hits) < limit:
        idx = hay.find(needle, start)
        if idx < 0:
            break
        lo = max(idx - context_chars, 0)
        hi = min(idx + len(query) + context_chars, len(text))
        hits.append({
            "offset": idx,
            "snippet": text[lo:hi].replace("\n", " ⏎ ").strip(),
        })
        start = idx + max(len(query), 1)
    return {
        "path": _rel(target),
        "kind": kind,
        "query": query,
        "count": len(hits),
        "hits": hits,
        "hint": "拿到 offset 后用 read_document(path, offset=...) 读命中处的完整上下文",
    }


@app.get(
    "/files/search",
    operation_id="search_files",
    summary="在 documents 目录里按文件名和纯文本正文搜索",
)
def search_files(
    query: str = Query(..., min_length=1, description="要找的关键词"),
    subdir: str = Query("", description="限定子目录, 留空=全部"),
    ignore_case: bool = Query(True),
    limit: int = Query(20, ge=1, le=100),
) -> dict:
    base = _resolve_dir(subdir) if subdir.strip() else DATA_DIR
    needle = query.lower() if ignore_case else query
    hits: list[dict] = []
    for path in sorted(base.rglob("*")):
        if len(hits) >= limit:
            break
        if not path.is_file():
            continue
        name_cmp = path.name.lower() if ignore_case else path.name
        if needle in name_cmp:
            hits.append({"path": _rel(path), "where": "文件名", "line": 0, "snippet": path.name})
            continue
        if _suffix(path) not in TEXT_SUFFIXES:
            continue
        try:
            text = _read_text(path)
        except Exception:
            continue
        body = text.lower() if ignore_case else text
        idx = body.find(needle)
        if idx < 0:
            continue
        line_no = body.count("\n", 0, idx) + 1
        lo = max(idx - 80, 0)
        hi = min(idx + len(query) + 80, len(text))
        hits.append({
            "path": _rel(path),
            "where": "内容",
            "line": line_no,
            "snippet": text[lo:hi].replace("\n", " ⏎ ").strip(),
        })
    return {"query": query, "count": len(hits), "hits": hits}


@app.get(
    "/images/describe",
    operation_id="describe_image",
    summary="让本地视觉模型看图片(照片/截图/扫描件), 返回描述与读出的文字",
)
def describe_image(
    path: str = Query(..., description="图片路径, 相对 documents 目录"),
    question: str = Query("请用中文描述这张图片的内容; 如果图中有文字, 请逐字读出来。"),
    max_tokens: int = Query(512, ge=64, le=2048),
) -> dict:
    target = _resolve(path)
    if _suffix(target) not in IMAGE_SUFFIXES:
        raise HTTPException(status_code=400, detail="这不是图片文件")
    _size_ok(target)
    mime = {
        ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
        ".webp": "image/webp", ".bmp": "image/bmp", ".gif": "image/gif",
        ".tif": "image/tiff", ".tiff": "image/tiff",
    }[_suffix(target)]
    b64 = base64.b64encode(target.read_bytes()).decode("ascii")
    payload = {
        "model": VISION_MODEL,
        "messages": [{
            "role": "user",
            "content": [
                {"type": "text", "text": question},
                {"type": "image_url", "image_url": {"url": f"data:{mime};base64,{b64}"}},
            ],
        }],
        "max_tokens": max_tokens,
        "temperature": 0.2,
    }
    try:
        resp = _post_json(f"{VISION_URL}/v1/chat/completions", payload, timeout=180)
    except urllib.error.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"视觉服务返回错误 {exc.code}: {exc.reason}")
    except Exception as exc:
        raise HTTPException(
            status_code=503,
            detail=f"连不上视觉服务 {VISION_URL} ({exc}). 请用 --profile vision 启动 ace-vision-service-model",
        )
    try:
        answer = resp["choices"][0]["message"]["content"].strip()
    except Exception:
        answer = json.dumps(resp)[:2000]
    return {"path": _rel(target), "model": VISION_MODEL, "answer": answer}


@app.get(
    "/knowledge/search",
    operation_id="search_knowledge",
    summary="在已入库的知识库里做语义检索(适合问'哪份文件提到了X')",
)
def search_knowledge(
    query: str = Query(..., min_length=1),
    limit: int = Query(4, ge=1, le=10),
) -> dict:
    try:
        resp = _post_json(f"{RAG_URL}/search", {"text": query, "limit": limit}, timeout=60)
    except urllib.error.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"知识库服务返回错误 {exc.code}")
    except Exception as exc:
        raise HTTPException(status_code=503, detail=f"连不上知识库服务 {RAG_URL} ({exc})")
    results = resp.get("results") or resp.get("documents") or []
    cleaned = []
    for item in results[:limit]:
        if isinstance(item, dict):
            cleaned.append({
                "score": round(float(item.get("score", 0) or 0), 4),
                "source": item.get("source") or item.get("path") or item.get("file") or "",
                "chunk": str(item.get("chunk") or item.get("text") or item.get("content") or "")[:600],
            })
        else:
            cleaned.append({"score": 0, "source": "", "chunk": str(item)[:600]})
    return {"query": query, "count": len(cleaned), "results": cleaned}


@app.get(
    "/knowledge/status",
    operation_id="knowledge_status",
    include_in_schema=False,
    summary="看知识库是否已更新(有多少文件待入库、上次重建时间)",
)
def knowledge_status() -> dict:
    parser = "unknown"
    detail: Any = None
    try:
        detail = _get_json(f"{PARSER_URL}/health")
        parser = "ok"
    except Exception as exc:
        parser = f"unreachable ({exc})"
    return {
        "parser": parser,
        "parser_detail": detail,
        "last_reindex": STATE["last_reindex"],
        "last_reindex_ok": STATE["last_reindex_ok"],
        "last_reindex_files": STATE["last_reindex_files"],
        "last_reindex_msg": STATE["last_reindex_msg"],
        "pending": STATE["pending"],
        "watch_dir": str(WATCH_DIR),
    }


@app.post(
    "/knowledge/reindex",
    operation_id="reindex_knowledge",
    include_in_schema=False,
    summary="手动重建知识库索引(一般不需要, 放文件进目录会自动重建)",
)
def reindex_knowledge() -> dict:
    ok, msg = _do_reindex()
    if not ok:
        raise HTTPException(status_code=502, detail=msg)
    return {"ok": True, "message": msg}


# ---------------------------------------------------------------- 写文件 / 执行 / 产出文档
#
# 这一段是"Claude 式"能力: 不只读, 还能动手。
# 工作目录: OUTPUT_DIR 默认 = /work, 由 compose 挂载宿主整个 AI-ACE 目录(读写)。
# 少数数据/缓存目录(数据库、上传件、向量库、模型、备份)列入 PROTECTED, 只挡住写入, 仍可读。

OUTPUT_DIR = Path(os.getenv("OUTPUT_DIR", str(DATA_DIR / "output"))).resolve()

# 只读保护: 这些前缀下的文件不允许写入/删除/改名(防止 AI 弄坏数据库、上传件、模型)
PROTECTED_PREFIXES = tuple(
    x.strip() for x in os.getenv(
        "PROTECTED_PREFIXES",
        "open-webui/webui.db,open-webui/uploads,open-webui/vector_db,open-webui/cache,models,backup,database,.git",
    ).split(",")
    if x.strip()
)
DOCX_URL = os.getenv("DOCX_URL", "http://ace-docx-generator:8003").rstrip("/")
RUN_TIMEOUT = int(os.getenv("RUN_TIMEOUT", "60"))
MAX_RUN_OUTPUT = int(os.getenv("MAX_RUN_OUTPUT", "6000"))
MAX_WRITE_CHARS = int(os.getenv("MAX_WRITE_CHARS", str(400_000)))


def _out_rel(path: Path) -> str:
    try:
        return path.relative_to(OUTPUT_DIR).as_posix()
    except ValueError:
        return path.name


def _resolve_out(rel: str, *, must_exist: bool = False, want_dir: bool = False) -> Path:
    """把相对路径解析到 OUTPUT_DIR 内, 越界直接拒绝。"""
    raw = (rel or "").strip().replace("\\", "/").lstrip("/")
    if not raw:
        if want_dir:
            return OUTPUT_DIR
        raise HTTPException(status_code=400, detail="path 不能为空")
    target = (OUTPUT_DIR / PurePosixPath(raw)).resolve()
    try:
        target.relative_to(OUTPUT_DIR)
    except ValueError:
        raise HTTPException(
            status_code=400,
            detail="路径越界, 只能读写 AI-ACE 工作目录",
        )
    if must_exist and not target.exists():
        raise HTTPException(status_code=404, detail=f"文件不存在: {raw}")
    return target


def _check_writable(path: Path) -> None:
    try:
        rel = path.relative_to(OUTPUT_DIR).as_posix()
    except ValueError:
        rel = ""
    for pref in PROTECTED_PREFIXES:
        if rel == pref or rel.startswith(pref + "/"):
            raise HTTPException(
                status_code=403,
                detail=f"{rel} 属于受保护的数据/缓存目录, 只读; 换一个位置写",
            )
    if path.suffix.lower() in IMAGE_SUFFIXES | DOC_SUFFIXES:
        raise HTTPException(
            status_code=400,
            detail=f"不能直接写入 {path.suffix} 这类二进制文件; 文本请用 .md/.txt/.csv, "
                   f"Word/Excel 请用 generate_document 生成",
        )


def _file_info(path: Path) -> dict:
    st = path.stat()
    return {
        "name": path.name,
        "path": _out_rel(path),
        "size": st.st_size,
        "size_human": _human(st.st_size),
        "modified": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(st.st_mtime)),
        "kind": _kind(path),
    }


class WriteRequest(BaseModel):
    path: str = Field(..., description="相对工作目录(AI-ACE 根)的路径, 例如 documents/output/报告.md 或 services/x.py")
    content: str = Field(..., description="要写入的完整文本内容 (UTF-8)")
    overwrite: bool = Field(False, description="是否覆盖已存在的文件; 默认 false, 避免覆盖已有成果")


class AppendRequest(BaseModel):
    path: str = Field(..., description="目标文件路径 (不存在会自动创建)")
    content: str = Field(..., description="追加到文件末尾的文本")


class ReplaceRequest(BaseModel):
    path: str = Field(..., description="要修改的文件路径")
    old_text: str = Field(..., description="被替换的原文本, 必须能在文件里精确找到")
    new_text: str = Field("", description="替换成的新文本, 留空表示删除这段")
    replace_all: bool = Field(False, description="是否替换全部匹配; 默认只替换第一处")


class MoveRequest(BaseModel):
    src: str = Field(..., description="原文件路径")
    dst: str = Field(..., description="新路径 (可改名, 也可移到子目录)")
    overwrite: bool = Field(False, description="目标已存在时是否覆盖")


class DeleteRequest(BaseModel):
    path: str = Field(..., description="要删除的文件路径 (受保护的数据目录除外)")
    confirm: bool = Field(False, description="必须显式传 true, 表示你确认要删除这个文件")


class RunPythonRequest(BaseModel):
    code: str = Field(..., description="要执行的 Python 代码; 工作目录是 AI-ACE 根, 可以直接 open() 读写相对路径文件")
    timeout_s: int = Field(30, ge=1, le=300, description="最长执行秒数, 默认 30")


class RunShellRequest(BaseModel):
    command: str = Field(..., description="要执行的 shell 命令; 工作目录是 AI-ACE 根, 示例: ls documents/output")
    timeout_s: int = Field(30, ge=1, le=300, description="最长执行秒数, 默认 30")


class SectionIn(BaseModel):
    heading: str = Field("", description="小节标题")
    body: str = Field("", description="小节正文 (可多段, 用 \\n 分隔)")


class TableIn(BaseModel):
    header: list[str] = Field(default_factory=list, description="表头单元格")
    rows: list[list[str]] = Field(default_factory=list, description="表格数据行")


class GenerateRequest(BaseModel):
    title: str = Field(..., description="文档标题")
    content: str = Field("", description="标题下方的概述段落")
    sections: list[SectionIn] = Field(default_factory=list, description="正文小节列表")
    tables: list[TableIn] = Field(default_factory=list, description="要插入的表格")
    format: str = Field("docx", description="docx 生成 Word, xlsx 生成 Excel")
    font_name: str = Field("宋体", description="字体, 默认宋体")
    font_size: int = Field(12, ge=8, le=24, description="字号, 默认 12")


@app.get(
    "/output/list",
    operation_id="list_outputs",
    include_in_schema=False,
    summary="列出工作目录里已有的文件(不传 subdir 就是整个 AI-ACE 根)",
)
def list_outputs(
    subdir: str = Query("", description="子目录, 留空表示整个工作目录"),
    limit: int = Query(200, ge=1, le=1000, description="最多返回多少条"),
) -> dict:
    root = _resolve_out(subdir, want_dir=True)
    if not root.exists():
        return {"ok": True, "root": "output", "count": 0, "items": [], "message": "工作目录还不存在"}
    if not root.is_dir():
        raise HTTPException(status_code=400, detail=f"这是文件不是目录: {subdir}")
    items: list[dict] = []
    for path in sorted(root.rglob("*")):
        if path.is_file():
            try:
                items.append(_file_info(path))
            except OSError:
                continue
        if len(items) >= limit:
            break
    return {
        "ok": True,
        "root": _out_rel(root) if root != OUTPUT_DIR else ".",
        "count": len(items),
        "items": items,
        "hint": "路径相对 AI-ACE 根; 生成的 Word/Excel 在 documents\\output 下",
    }


@app.post(
    "/output/write",
    operation_id="write_file",
    summary="写一个文本文件(新建). 做多步任务时: 先 list_files 定位, 用 read_document 读资料, 用本工具写草稿, 写完用 read_file 复查, 最后 generate_document 出 Word",
)
def write_file(req: WriteRequest) -> dict:
    if len(req.content) > MAX_WRITE_CHARS:
        raise HTTPException(
            status_code=413,
            detail=f"内容 {len(req.content)} 字超过单次上限 {MAX_WRITE_CHARS} 字, 请分段写 (先 write 再 append)",
        )
    target = _resolve_out(req.path)
    _check_writable(target)
    if target.exists() and target.is_dir():
        raise HTTPException(status_code=400, detail=f"这是目录: {req.path}")
    if target.exists() and not req.overwrite:
        raise HTTPException(
            status_code=409,
            detail=f"文件已存在: {req.path}; 先用 read_file 看一下, 要整体重写请传 overwrite=true, 要补充请用 append_file",
        )
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(req.content, encoding="utf-8")
    size = target.stat().st_size
    return {
        "ok": True,
        "path": _out_rel(target),
        "size": size,
        "size_human": _human(size),
        "message": f"已写入 {_out_rel(target)} ({_human(size)})",
    }


@app.post(
    "/output/append",
    operation_id="append_file",
    include_in_schema=False,
    summary="在已有文件末尾追加内容(不存在会创建), 适合分段写长文",
)
def append_file(req: AppendRequest) -> dict:
    if len(req.content) > MAX_WRITE_CHARS:
        raise HTTPException(status_code=413, detail=f"单次追加超过 {MAX_WRITE_CHARS} 字, 请再分一段")
    target = _resolve_out(req.path)
    _check_writable(target)
    if target.exists() and target.is_dir():
        raise HTTPException(status_code=400, detail=f"这是目录: {req.path}")
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("a", encoding="utf-8") as fh:
        fh.write(req.content)
    size = target.stat().st_size
    return {"ok": True, "path": _out_rel(target), "size": size, "size_human": _human(size), "message": "已追加"}


@app.post(
    "/output/replace",
    operation_id="replace_in_file",
    include_in_schema=False,
    summary="替换文件里的一段文字(改稿用); 找不到原文字会报错, 先 read_file 确认原文",
)
def replace_in_file(req: ReplaceRequest) -> dict:
    if not req.old_text:
        raise HTTPException(status_code=400, detail="old_text 不能为空")
    target = _resolve_out(req.path, must_exist=True)
    _check_writable(target)
    if not target.is_file():
        raise HTTPException(status_code=400, detail=f"这是目录: {req.path}")
    _size_ok(target)
    text = _decode(target.read_bytes())
    hits = text.count(req.old_text)
    if hits == 0:
        raise HTTPException(
            status_code=400,
            detail="文件里找不到 old_text, 请先用 read_file 或 search_in_file 确认要改的原文(注意空格与换行)",
        )
    new_text = text.replace(req.old_text, req.new_text, 1 if not req.replace_all else -1)
    target.write_text(new_text, encoding="utf-8")
    done = hits if req.replace_all else 1
    return {"ok": True, "path": _out_rel(target), "replaced": done, "matches": hits, "size": target.stat().st_size}


@app.post(
    "/output/move",
    operation_id="move_output",
    include_in_schema=False,
    summary="给工作目录里的文件改名或移动到子目录(例如把草稿归到 归档/)",
)
def move_output(req: MoveRequest) -> dict:
    src = _resolve_out(req.src, must_exist=True)
    _check_writable(src)
    if not src.is_file():
        raise HTTPException(status_code=400, detail=f"这是目录: {req.src}")
    dst = _resolve_out(req.dst)
    _check_writable(dst)
    if dst.exists() and not req.overwrite:
        raise HTTPException(status_code=409, detail=f"目标已存在: {req.dst}, 要覆盖请传 overwrite=true")
    dst.parent.mkdir(parents=True, exist_ok=True)
    src.replace(dst)
    return {"ok": True, "from": _out_rel(src), "to": _out_rel(dst), "message": "已移动"}


@app.post(
    "/output/delete",
    operation_id="delete_output",
    summary="删除一个文件(需要 confirm=true; 只能删文件不能删目录; 受保护目录禁止)",
)
def delete_output(req: DeleteRequest) -> dict:
    if not req.confirm:
        raise HTTPException(status_code=400, detail="删除是不可恢复操作, 必须传 confirm=true 才执行")
    target = _resolve_out(req.path, must_exist=True)
    _check_writable(target)
    if target.is_dir():
        raise HTTPException(status_code=400, detail="只能删文件, 不能删目录")
    target.unlink()
    return {"ok": True, "path": req.path, "message": "已删除"}


NET_BLOCK_ENV = ("http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "all_proxy")


def _child_env() -> dict:
    """子进程环境: 默认断掉外网出口(代理指向 127.0.0.1:9), 内部服务名放行。"""
    env = os.environ.copy()
    flag = os.getenv("BLOCK_INTERNET", "1").strip().lower()
    if flag not in ("0", "false", "no", "off"):
        for k in NET_BLOCK_ENV:
            env[k] = "http://127.0.0.1:9"
        allow = (
            "localhost,127.0.0.1,ace-file-tools,ace-agent,qwen3-text-service,"
            "ace-dashboard,ace-rag-service,ace-document-parser,ace-docx-generator,vision-service-model"
        )
        env["NO_PROXY"] = allow
        env["no_proxy"] = allow
    return env


def _run(cmd: list[str], timeout_s: int) -> dict:
    started = time.time()
    try:
        proc = subprocess.run(
            cmd,
            cwd=str(OUTPUT_DIR) if OUTPUT_DIR.exists() else None,
            env=_child_env(),
            capture_output=True,
            text=True,
            timeout=timeout_s,
            errors="replace",
        )
    except subprocess.TimeoutExpired:
        raise HTTPException(status_code=504, detail=f"执行超过 {timeout_s} 秒被终止, 请简化或分段执行")
    except FileNotFoundError as exc:
        raise HTTPException(status_code=500, detail=f"找不到可执行程序: {exc}")
    out = (proc.stdout or "")[-MAX_RUN_OUTPUT:]
    err = (proc.stderr or "")[-MAX_RUN_OUTPUT:]
    return {
        "exit_code": proc.returncode,
        "stdout": out,
        "stderr": err,
        "truncated": len(proc.stdout or "") > MAX_RUN_OUTPUT or len(proc.stderr or "") > MAX_RUN_OUTPUT,
        "seconds": round(time.time() - started, 2),
        "cwd": str(OUTPUT_DIR),
        "ok": proc.returncode == 0,
    }


@app.post(
    "/output/run-python",
    operation_id="run_python",
    summary="执行 Python 代码(算数/处理文件/生成图表都行), 工作目录是 AI-ACE 根, 返回 exit_code 与输出. 可以连续调用: 每次看 stderr, 报错就改再跑, 不要失败一次就停",
)
def run_python(req: RunPythonRequest) -> dict:
    if not req.code.strip():
        raise HTTPException(status_code=400, detail="code 不能为空")
    timeout_s = min(req.timeout_s, RUN_TIMEOUT * 5)
    return _run([sys.executable, "-c", req.code], timeout_s)


@app.post(
    "/output/run-shell",
    operation_id="run_shell",
    include_in_schema=False,
    summary="执行 shell 命令(ls/cat/grep/wc/python 等), 工作目录是 AI-ACE 根, 返回 exit_code 与输出",
)
def run_shell(req: RunShellRequest) -> dict:
    if not req.command.strip():
        raise HTTPException(status_code=400, detail="command 不能为空")
    timeout_s = min(req.timeout_s, RUN_TIMEOUT * 5)
    return _run(["/bin/sh", "-c", req.command], timeout_s)


@app.post(
    "/documents/generate",
    operation_id="generate_document",
    summary="把内容生成正式的 Word(docx) 或 Excel(xlsx) 文件, 存到 documents/output 供下载. 输入是结构化内容(sections/tables), 先把正文准备好再调用",
)
def generate_document(req: GenerateRequest) -> dict:
    fmt = (req.format or "docx").lower().strip()
    if fmt not in ("docx", "xlsx"):
        raise HTTPException(status_code=400, detail="format 只能是 docx 或 xlsx")
    payload = {
        "title": req.title,
        "content": req.content,
        "sections": [{"heading": s.heading, "body": s.body} for s in req.sections],
        "tables": [{"header": t.header, "rows": t.rows} for t in req.tables],
        "format": fmt,
        "font_name": req.font_name,
        "font_size": req.font_size,
    }
    try:
        resp = _post_json(f"{DOCX_URL}/generate", payload, timeout=180)
    except Exception as exc:
        raise HTTPException(
            status_code=502,
            detail=f"文档生成服务不可用 ({DOCX_URL}): {exc}; 请确认 ace-docx-generator 已启动",
        )
    if resp.get("status") != "success" or not resp.get("file"):
        raise HTTPException(status_code=502, detail=f"生成失败: {json.dumps(resp, ensure_ascii=False)[:300]}")
    filename = resp["file"]
    return {
        "ok": True,
        "file": filename,
        "format": resp.get("format", fmt),
        "path": f"output/{filename}",
        "host_path": f"documents\\output\\{filename}",
        "download_url": f"http://127.0.0.1:8003/download/{urllib.parse.quote(filename)}",
        "message": f"已生成 {filename}, 在 AI-ACE\\documents\\output 下, 双击即可打开",
    }


# ---------------------------------------------------------------- 自动执行 (agent)


AGENT_URL = os.getenv("AGENT_URL", "http://ace-agent:8011").rstrip("/")


class AgentTaskRequest(BaseModel):
    goal: str = Field(
        ...,
        description="要完成的目标, 写清楚要什么交付物, 例如: 把 documents 里的三份周报汇总成一份 Word 放到 documents/output",
    )
    wait_seconds: int = Field(
        60,
        ge=0,
        le=600,
        description="等多少秒再返回; 建议 60, 大任务先拿任务号再用 agent_task_status 查",
    )


@app.post(
    "/agent/run",
    operation_id="run_agent_task",
    summary=(
        "全自动执行一个多步任务: 交目标后 agent 自己规划、读写文件、执行代码、生成 Word/Excel, 一直做到完成. "
        "适合整理资料成文档/批量处理文件/写脚本跑一下; 一句话能答的不要用它"
    ),
)
def run_agent_task(req: AgentTaskRequest) -> dict:
    payload = {"goal": req.goal, "wait_seconds": req.wait_seconds}
    try:
        resp = _post_json(f"{AGENT_URL}/tasks", payload, timeout=req.wait_seconds + 90)
    except Exception as exc:
        raise HTTPException(
            status_code=502,
            detail=f"自动执行器不可用 ({AGENT_URL}): {exc}; 请确认 ace-agent 已启动",
        )
    status = str(resp.get("status") or "queued")
    done = status == "done"
    return {
        "ok": True,
        "task_id": resp.get("task_id"),
        "status": status,
        "answer": resp.get("answer") or "",
        "steps": resp.get("steps"),
        "result_file": resp.get("result_file"),
        "message": (
            "任务已完成, 回答见 answer, 过程报告见 result_file"
            if done
            else "任务已提交(还在跑), 用 agent_task_status 传这个 task_id 查进度"
        ),
    }


@app.get(
    "/agent/task",
    operation_id="agent_task_status",
    summary="查自动任务进度: 传任务号, 返回状态/步数/最终回答/报告路径",
)
def agent_task_status(
    task_id: str = Query(..., description="run_agent_task 返回的任务号"),
) -> dict:
    try:
        resp = _get_json(f"{AGENT_URL}/tasks/{urllib.parse.quote(task_id)}", timeout=30)
    except Exception as exc:
        raise HTTPException(status_code=502, detail=f"自动执行器不可用 ({AGENT_URL}): {exc}")
    return {
        "ok": True,
        "task_id": resp.get("task_id") or task_id,
        "status": resp.get("status"),
        "answer": resp.get("answer") or "",
        "steps": resp.get("steps"),
        "duration": resp.get("duration"),
        "trace": resp.get("trace") or [],
        "result_file": resp.get("result_file"),
    }


# ---------------------------------------------------------------- 目录监听


def _snapshot(root: Path) -> dict[str, tuple[float, int]]:
    snap: dict[str, tuple[float, int]] = {}
    if not root.exists():
        return snap
    for path in root.rglob("*"):
        if path.is_file():
            try:
                st = path.stat()
            except OSError:
                continue
            snap[str(path)] = (st.st_mtime, st.st_size)
    return snap


def _do_reindex() -> tuple[bool, str]:
    with LOCK:
        STATE["pending"] = True
        try:
            resp = _post_json(f"{PARSER_URL}/reindex", {}, timeout=1800)
            files = 0
            for key in ("indexed", "files", "count", "total", "documents"):
                value = resp.get(key)
                if isinstance(value, int):
                    files = max(files, value)
            STATE.update({
                "last_reindex": _now(),
                "last_reindex_ok": True,
                "last_reindex_files": files,
                "last_reindex_msg": json.dumps(resp, ensure_ascii=False)[:400],
                "pending": False,
            })
            LOG.info("reindex ok, files=%s", files)
            return True, f"已重建索引, 文件 {files} 个"
        except Exception as exc:
            STATE.update({
                "last_reindex": _now(),
                "last_reindex_ok": False,
                "last_reindex_msg": str(exc)[:400],
                "pending": False,
            })
            LOG.warning("reindex failed: %s", exc)
            return False, f"重建索引失败: {exc}"


def _watch_loop() -> None:
    last_change = 0.0
    base = _snapshot(WATCH_DIR)
    LOG.info("watch start: %s interval=%ss", WATCH_DIR, WATCH_INTERVAL)
    while True:
        time.sleep(WATCH_INTERVAL)
        try:
            cur = _snapshot(WATCH_DIR)
            if cur != base:
                time.sleep(STABLE_SECONDS)
                again = _snapshot(WATCH_DIR)
                if again != cur:
                    base = again
                    continue
                added = len(set(again) - set(base))
                changed = len(set(again) ^ set(base))
                LOG.info("detected change: new=%s diff=%s", added, changed)
                base = again
                if time.time() - last_change < REINDEX_MIN_GAP:
                    continue
                last_change = time.time()
                _do_reindex()
            else:
                base = cur
        except Exception as exc:  # 监听线程不能死
            LOG.warning("watch loop error: %s", exc)


@app.on_event("startup")
def _startup() -> None:
    LOG.info("file-tools up: data=%s watch=%s enabled=%s", DATA_DIR, WATCH_DIR, WATCH_ENABLED)
    if WATCH_ENABLED:
        threading.Thread(target=_watch_loop, name="watch", daemon=True).start()
    if INDEX_ON_START:
        threading.Thread(target=_do_reindex, name="reindex-on-start", daemon=True).start()
