from fastapi import FastAPI
from pydantic import BaseModel
from qdrant_client import QdrantClient
from sentence_transformers import SentenceTransformer
import os
import requests


app = FastAPI(
    title="AI-ACE RAG Service",
    version="2.2"
)


QDRANT_HOST = os.getenv("QDRANT_HOST", "ace-qdrant")
QDRANT_PORT = int(os.getenv("QDRANT_PORT", "6333"))
LLM_HOST = os.getenv("LLM_HOST", "http://qwen3-text-service:8081")
LLM_MODEL_NAME = os.getenv("LLM_MODEL_NAME", "Qwen3-4B")

COLLECTION_NAME = "ai_ace_knowledge"
TERMS_COLLECTION = "ai_ace_terms"


client = QdrantClient(host=QDRANT_HOST, port=QDRANT_PORT)
model = SentenceTransformer("/models/embedding")


def _search(vector, limit, collection=COLLECTION_NAME):
    result = client.query_points(
        collection_name=collection,
        query=vector,
        limit=limit
    )
    return result.points


@app.get("/health")
def health():
    return {"status": "ok", "service": "rag-service"}


@app.get("/collections")
def collections():
    return client.get_collections()


class SearchRequest(BaseModel):
    text: str
    limit: int = 5


@app.post("/search")
def search(req: SearchRequest):
    vector = model.encode(req.text).tolist()
    points = _search(vector, req.limit)
    return {
        "results": [
            {"id": p.id, "score": p.score, "payload": p.payload}
            for p in points
        ]
    }


class QueryRequest(BaseModel):
    question: str


@app.post("/query")
def query(req: QueryRequest):
    vector = model.encode(req.question).tolist()
    points = _search(vector, 5)
    context = [p.payload for p in points]
    term_points = _search(vector, 5, TERMS_COLLECTION)
    terms = [p.payload for p in term_points if p.payload]
    return {"question": req.question, "context": context, "terms": terms}


class AskRequest(BaseModel):
    question: str
    limit: int = 5


@app.post("/ask")
def ask(req: AskRequest):
    vector = model.encode(req.question).tolist()

    # 1) 检索知识库 chunk
    points = _search(vector, req.limit)
    context = [p.payload.get("chunk", "") for p in points if p.payload]
    context_text = "\n\n".join(context)

    # 2) 检索术语表 (企业知识库实体: 专有名词/缩写/产品名/技术关键词)
    term_points = _search(vector, req.limit, TERMS_COLLECTION)
    term_lines = []
    for p in term_points:
        pl = p.payload
        if not pl:
            continue
        name = pl.get("name", "")
        if not name:
            continue
        ttype = pl.get("type", "")
        desc = pl.get("description", "")
        if ttype and desc:
            term_lines.append(f"- {name}（{ttype}）：{desc}")
        elif desc:
            term_lines.append(f"- {name}：{desc}")
        else:
            term_lines.append(f"- {name}")
    term_text = "\n".join(term_lines)

    prompt = (
        "你是AI-ACE企业知识库助手。请仅根据以下【术语表】和【知识库内容】回答用户问题。"
        "术语表是企业知识库抽取出的专有名词/缩写/产品名/技术关键词，用于辅助理解。"
        "如果知识库中没有答案，明确说明'知识库中未找到相关信息'。\n\n"
        f"【术语表】\n{term_text if term_text else '（无）'}\n\n"
        f"【知识库内容】\n{context_text}\n\n"
        f"【用户问题】\n{req.question}\n\n"
        "【回答】"
    )

    try:
        resp = requests.post(
            LLM_HOST + "/v1/chat/completions",
            json={
                "model": LLM_MODEL_NAME,
                "messages": [{"role": "user", "content": prompt}],
                "temperature": 0.2
            },
            timeout=300
        )
        data = resp.json()
        answer = ""
        choices = data.get("choices", [])
        if choices:
            answer = choices[0].get("message", {}).get("content", "")
    except Exception as e:
        answer = f"模型未启动或调用失败: {e}"

    return {
        "question": req.question,
        "answer": answer,
        "context": context,
        "terms": [p.payload for p in term_points if p.payload]
    }
