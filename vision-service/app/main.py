from fastapi import FastAPI, UploadFile, File
from pathlib import Path
import shutil
import uuid
import os
import requests
import base64


app = FastAPI(
    title="AI-ACE Vision Service",
    version="2.0"
)


UPLOAD_DIR = Path("/documents/images")
UPLOAD_DIR.mkdir(parents=True, exist_ok=True)


VISION_HOST = os.getenv("VISION_HOST", "vision-service-model")
VISION_PORT = os.getenv("VISION_PORT", "8083")
VISION_API = f"http://{VISION_HOST}:{VISION_PORT}/v1/chat/completions"


@app.get("/health")
def health():
    return {
        "status": "ok",
        "service": "vision-service",
        "model_api": VISION_API
    }


def _extract_text(result):
    """从 llama.cpp 的 OpenAI 兼容响应里抽取文本"""
    if not isinstance(result, dict):
        return ""
    try:
        choices = result.get("choices", [])
        if choices:
            msg = choices[0].get("message", {})
            content = msg.get("content", "")
            if isinstance(content, str):
                return content.strip()
            if isinstance(content, list):
                parts = []
                for c in content:
                    if isinstance(c, dict) and c.get("type") == "text":
                        parts.append(c.get("text", ""))
                return "\n".join(parts).strip()
    except Exception:
        pass
    return ""


@app.post("/analyze")
async def analyze(file: UploadFile = File(...)):
    file_id = str(uuid.uuid4())
    save_path = UPLOAD_DIR / f"{file_id}_{file.filename}"

    with save_path.open("wb") as buffer:
        shutil.copyfileobj(file.file, buffer)

    image_bytes = save_path.read_bytes()
    image_base64 = base64.b64encode(image_bytes).decode()

    payload = {
        "messages": [
            {
                "role": "system",
                "content": (
                    "你是AI-ACE企业视觉助手。请完成: "
                    "1.图片内容分析 2.图片文字识别(OCR, 逐字提取所有可见文字) "
                    "3.专业名词提取 4.缩写解释。只输出中文结构化结果。"
                )
            },
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": "分析这张图片, 提取所有文字并说明内容"},
                    {
                        "type": "image_url",
                        "image_url": {"url": f"data:image/jpeg;base64,{image_base64}"}
                    }
                ]
            }
        ],
        "temperature": 0.2
    }

    try:
        response = requests.post(VISION_API, json=payload, timeout=300)
        result = response.json()
        text = _extract_text(result)
        if not text:
            text = str(result.get("error", "")) if isinstance(result, dict) else ""
        return {
            "status": "completed",
            "filename": file.filename,
            "path": str(save_path),
            "text": text,
            "vision_result": result
        }
    except Exception as e:
        return {
            "status": "error",
            "filename": file.filename,
            "path": str(save_path),
            "text": "",
            "error": str(e)
        }
