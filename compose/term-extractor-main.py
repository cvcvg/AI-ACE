from fastapi import FastAPI
from pydantic import BaseModel

import os
import json
import uuid
import time
import requests
import re
import traceback

from qdrant_client import QdrantClient
from qdrant_client.models import (
    VectorParams,
    Distance,
    PointStruct
)

from sentence_transformers import SentenceTransformer


app = FastAPI(
    title="AI-ACE Term Extractor",
    version="8.0"
)


# ======================
# ENV
# ======================

QDRANT_HOST = os.getenv(
    "QDRANT_HOST",
    "ace-qdrant"
)

QDRANT_PORT = int(
    os.getenv(
        "QDRANT_PORT",
        "6333"
    )
)

LLM_HOST = os.getenv(
    "LLM_HOST",
    "http://qwen3-text-service:8081"
)

COLLECTION_NAME = "ai_ace_terms"



# ======================
# QDRANT
# ======================

def wait_qdrant():

    for _ in range(60):

        try:

            client = QdrantClient(
                host=QDRANT_HOST,
                port=QDRANT_PORT
            )

            client.get_collections()

            return client

        except Exception:

            time.sleep(2)


    raise Exception(
        "Qdrant unavailable"
    )



qdrant = wait_qdrant()



# ======================
# EMBEDDING
# ======================

embedding = SentenceTransformer(
    "/models/embedding"
)


VECTOR_SIZE = embedding.get_embedding_dimension()



collections = [
    c.name
    for c in qdrant.get_collections().collections
]


if COLLECTION_NAME not in collections:

    qdrant.create_collection(

        collection_name=COLLECTION_NAME,

        vectors_config=VectorParams(

            size=VECTOR_SIZE,

            distance=Distance.COSINE

        )

    )



# ======================
# MODEL
# ======================

class ExtractRequest(BaseModel):

    text: str

    source: str = "manual"



# ======================
# HEALTH
# ======================

@app.get("/health")
def health():

    return {

        "status": "ok",

        "service": "term-extractor",

        "collection": COLLECTION_NAME

    }



# ======================
# TEXT FIX
# ======================

def fix_mojibake(text):

    if not isinstance(text, str):

        return text


    try:

        if (
            "Ã" in text
            or "ç" in text
            or "å" in text
        ):

            return (
                text
                .encode("latin1")
                .decode("utf-8")
            )

    except Exception:

        pass


    return text



# ======================
# JSON CLEAN
# ======================

def clean_json(text):

    if not text:

        return None


    text = str(text).strip()


    text = re.sub(
        r"<think>.*?</think>",
        "",
        text,
        flags=re.S
    )


    text = text.replace(
        "```json",
        ""
    )


    text = text.replace(
        "```",
        ""
    )


    start = text.find("[")

    end = text.rfind("]")


    if start >= 0 and end > start:

        return text[start:end+1]


    return None# ======================
# EXTRACT
# ======================

@app.post("/extract")
def extract(req: ExtractRequest):

    prompt = f"""
你是企业知识库实体抽取引擎。

你的任务：

只抽取实体。

禁止总结。
禁止解释。
禁止输出思考过程。
禁止输出Markdown。

必须识别：

- 系统名称
- 软件名称
- AI模型
- 数据库
- 技术名称
- 硬件型号
- 公司名称
- 缩写


严格要求：

1. 每个实体独立一条
2. name 保留原始名称
3. type 使用中文
4. description 使用中文
5. context 使用中文
6. 只返回 JSON 数组


返回格式：

[
  {{
    "name":"",
    "type":"",
    "description":"",
    "context":""
  }}
]


文本：

{req.text}

"""


    try:

        response = requests.post(

            LLM_HOST + "/v1/chat/completions",

            headers={

                "Content-Type":
                "application/json; charset=utf-8"

            },


            json={

                "model":
                "/models/Qwen3-4B-Q4_K_M.gguf",


                "messages":[

                    {

                        "role":
                        "system",

                        "content":
                        """
只输出JSON数组。
不要输出thinking。
不要输出解释。
必须使用中文。
"""

                    },


                    {

                        "role":
                        "user",

                        "content":
                        prompt

                    }

                ],


                "temperature":
                0,


                "max_tokens":
                1200,


                "chat_template_kwargs":{

                    "enable_thinking":
                    False

                }

            },


            timeout=180

        )


        response.raise_for_status()


        response.encoding = "utf-8"


        data = response.json()


        message = (

            data
            .get(
                "choices",
                [{}]
            )[0]
            .get(
                "message",
                {}
            )

        )


        content = message.get(
            "content",
            ""
        )


        # 防止 Qwen3 thinking 空 content

        if not content:

            content = message.get(
                "reasoning_content",
                ""
            )



    except Exception as e:


        traceback.print_exc()


        return {

            "status":
            "llm_error",

            "message":
            str(e)

        }



    json_text = clean_json(
        content
    )


    if not json_text:


        return {

            "status":
            "json_error",

            "raw":
            content[:1000]

        }



    try:


        terms = json.loads(
            json_text
        )


    except Exception as e:


        return {

            "status":
            "json_error",

            "message":
            str(e),

            "raw":
            json_text[:1000]

        }



    if isinstance(
        terms,
        dict
    ):


        terms = terms.get(
            "terms",
            []
        )



    clean = []


    for item in terms:


        if not isinstance(
            item,
            dict
        ):

            continue



        name = str(

            item.get(
                "name",
                ""
            )

        ).strip()



        if not name:

            continue



        clean.append({

            "name":
            fix_mojibake(
                name
            ),


            "type":
            fix_mojibake(
                str(
                    item.get(
                        "type",
                        "未知"
                    )
                )
            ),


            "description":
            fix_mojibake(
                str(
                    item.get(
                        "description",
                        f"{name}相关技术对象"
                    )
                )
            ),


            "context":
            fix_mojibake(
                str(
                    item.get(
                        "context",
                        req.text
                    )
                )
            )

        })



    # ======================
    # SAVE JSON
    # ======================

    os.makedirs(

        "/knowledge/extraction",

        exist_ok=True

    )


    with open(

        "/knowledge/extraction/latest.json",

        "w",

        encoding="utf-8"

    ) as f:


        json.dump(

            clean,

            f,

            ensure_ascii=False,

            indent=2

        )



    # ======================
    # VECTOR SAVE
    # ======================

    points = []


    for item in clean:


        vector_text = (

            item["name"]

            + " "

            +

            item["description"]

            + " "

            +

            item["context"]

        )


        vector = embedding.encode(

            vector_text,

            normalize_embeddings=True

        ).tolist()



        points.append(

            PointStruct(

                id=str(
                    uuid.uuid4()
                ),


                vector=vector,


                payload={

                    **item,


                    "source":
                    req.source,


                    "created_at":
                    time.time()

                }

            )

        )



    try:


        if points:


            qdrant.upsert(

                collection_name=
                COLLECTION_NAME,


                points=points

            )


    except Exception as e:


        traceback.print_exc()


        return {

            "status":
            "qdrant_error",

            "message":
            str(e)

        }



    return {


        "status":
        "ok",


        "collection":
        COLLECTION_NAME,


        "count":
        len(points),


        "terms":
        clean

    }