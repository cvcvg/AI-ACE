from fastapi import FastAPI, UploadFile, File
from pathlib import Path

from qdrant_client import QdrantClient
from qdrant_client.models import (
    PointStruct,
    VectorParams,
    Distance,
    PointIdsList
)

from sentence_transformers import SentenceTransformer

from docx import Document
import fitz
from openpyxl import load_workbook

import uuid
import os
import requests
import time


app = FastAPI(
    title="AI-ACE Document Parser",
    version="3.0"
)


# ==========================
# 路径
# ==========================
DOCUMENT_DIR = Path("/documents/input")
DOCUMENT_DIR.mkdir(parents=True, exist_ok=True)


# ==========================
# 环境
# ==========================
QDRANT_HOST = os.getenv("QDRANT_HOST", "ace-qdrant")
QDRANT_PORT = int(os.getenv("QDRANT_PORT", "6333"))
TERM_EXTRACTOR_URL = os.getenv(
    "TERM_EXTRACTOR_URL",
    "http://ace-term-extractor:8004/extract"
)
VISION_SERVICE_URL = os.getenv(
    "VISION_SERVICE_URL",
    "http://ace-vision-service:8001/analyze"
)

KNOWLEDGE_COLLECTION = "ai_ace_knowledge"

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif"}


# ==========================
# Qdrant 等待
# ==========================
def wait_qdrant():
    for _ in range(60):
        try:
            client = QdrantClient(host=QDRANT_HOST, port=QDRANT_PORT)
            client.get_collections()
            return client
        except Exception:
            time.sleep(2)
    raise Exception("Qdrant unavailable")


client = wait_qdrant()


# ==========================
# Embedding
# ==========================
embedding_model = SentenceTransformer("/models/embedding")
VECTOR_SIZE = embedding_model.get_sentence_embedding_dimension()


# ==========================
# Health
# ==========================
@app.get("/health")
def health():
    return {
        "status": "ok",
        "service": "document-parser",
        "collection": KNOWLEDGE_COLLECTION
    }


# ==========================
# 文件读取
# ==========================
def read_txt(path):
    return path.read_text(encoding="utf-8", errors="ignore")


def read_docx(path):
    doc = Document(path)
    content = []
    for p in doc.paragraphs:
        if p.text.strip():
            content.append(p.text)
    for table in doc.tables:
        for row in table.rows:
            content.append(" | ".join(cell.text for cell in row.cells))
    return "\n".join(content)


def read_pdf(path):
    text = ""
    pdf = fitz.open(path)
    for page in pdf:
        text += page.get_text()
    return text


def read_xlsx(path):
    wb = load_workbook(path, read_only=True, data_only=True)
    content = []
    for ws in wb.worksheets:
        content.append(f"[工作表: {ws.title}]")
        for row in ws.iter_rows(values_only=True):
            cells = [str(c) if c is not None else "" for c in row]
            if any(cells):
                content.append(" | ".join(cells))
    wb.close()
    return "\n".join(content)


def parse_file(path):
    suffix = path.suffix.lower()
    if suffix == ".txt":
        return read_txt(path)
    if suffix == ".docx":
        return read_docx(path)
    if suffix == ".pdf":
        return read_pdf(path)
    if suffix in (".xlsx", ".xlsm"):
        return read_xlsx(path)
    return ""


# ==========================
# 图片 -> 视觉服务 -> 文本
# ==========================
def analyze_image(path):
    try:
        with path.open("rb") as f:
            resp = requests.post(
                VISION_SERVICE_URL,
                files={"file": (path.name, f, "image/jpeg")},
                timeout=300
            )
        data = resp.json()
        text = data.get("text", "") or data.get("description", "")
        if text.strip():
            return text
        # 兜底: 从 vision_result 里尽量提取文本
        vr = data.get("vision_result", {})
        if isinstance(vr, dict):
            choices = vr.get("choices", [])
            if choices:
                text = choices[0].get("message", {}).get("content", "")
        return text if text else ""
    except Exception as e:
        return ""


# ==========================
# 分块
# ==========================
def split_text(text, size=500):
    chunks = []
    for i in range(0, len(text), size):
        chunk = text[i:i + size]
        if chunk.strip():
            chunks.append(chunk)
    return chunks


# ==========================
# Collection
# ==========================
def ensure_collection():
    collections = [c.name for c in client.get_collections().collections]
    if KNOWLEDGE_COLLECTION not in collections:
        client.create_collection(
            collection_name=KNOWLEDGE_COLLECTION,
            vectors_config=VectorParams(size=VECTOR_SIZE, distance=Distance.COSINE)
        )


def clear_collection(name):
    try:
        pts, _ = client.scroll(
            collection_name=name, limit=100000,
            with_payload=False, with_vectors=False
        )
        if pts:
            client.delete(
                collection_name=name,
                points_selector=PointIdsList(points=[p.id for p in pts])
            )
        return len(pts)
    except Exception:
        return -1


def save_chunks(filename, chunks):
    vectors = embedding_model.encode(chunks, normalize_embeddings=True)
    points = []
    for i, vector in enumerate(vectors):
        points.append(
            PointStruct(
                id=str(uuid.uuid4()),
                vector=vector.tolist(),
                payload={
                    "file": filename,
                    "chunk": chunks[i],
                    "created_at": time.time()
                }
            )
        )
    if points:
        client.upsert(collection_name=KNOWLEDGE_COLLECTION, points=points)
    return len(points)


# ==========================
# 专有名词提取
# ==========================
def extract_terms(text, filename):
    try:
        content = text[:12000]
        response = requests.post(
            TERM_EXTRACTOR_URL,
            json={"text": content, "source": filename},
            timeout=180
        )
        return response.json()
    except Exception as e:
        return {"status": "error", "message": str(e)}


# ==========================
# 处理核心逻辑
# ==========================
def process_file(path, filename):
    suffix = path.suffix.lower()

    if suffix in IMAGE_EXTS:
        text = analyze_image(path)
        if not text.strip():
            return {
                "status": "error",
                "message": "图片分析失败(视觉模型可能未启动)"
            }
    else:
        text = parse_file(path)

    if not text.strip():
        return {"status": "error", "message": "empty document"}

    chunks = split_text(text)
    ensure_collection()
    chunk_count = save_chunks(filename, chunks)
    term_result = extract_terms(text, filename)

    return {
        "status": "success",
        "filename": filename,
        "chunks": chunk_count,
        "terms_saved": term_result.get("count", 0),
        "term_status": term_result.get("status", "unknown"),
        "term_error": term_result.get("error", term_result.get("message", ""))
    }


# ==========================
# 上传入口
# ==========================
@app.post("/upload")
async def upload(file: UploadFile = File(...)):
    file_id = str(uuid.uuid4())
    save_path = DOCUMENT_DIR / f"{file_id}_{file.filename}"

    with save_path.open("wb") as f:
        f.write(await file.read())

    result = process_file(save_path, file.filename)
    return result


# ==========================
# 重建索引
# ==========================
@app.post("/reindex")
def reindex():
    files = [p for p in DOCUMENT_DIR.iterdir() if p.is_file()]
    if not files:
        return {"status": "ok", "message": "无待索引文件", "files": 0}

    # 重建索引前清空旧数据, 避免重复累积
    cleared_knowledge = clear_collection(KNOWLEDGE_COLLECTION)
    cleared_terms = clear_collection("ai_ace_terms")

    results = []
    for path in files:
        results.append(process_file(path, path.name))

    ok = sum(1 for r in results if r.get("status") == "success")
    return {
        "status": "ok",
        "total": len(files),
        "success": ok,
        "failed": len(files) - ok,
        "cleared_knowledge": cleared_knowledge,
        "cleared_terms": cleared_terms,
        "details": results
    }
