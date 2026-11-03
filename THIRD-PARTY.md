# 第三方组件与许可

本项目自身代码未附开源许可证。下列组件以 Docker 镜像或模型权重形式随离线包分发，版权归各自作者，使用时请遵守其原始许可。

| 组件 | 用途 | 上游 | 许可 |
|---|---|---|---|
| llama.cpp（CUDA 版） | 本地推理引擎 | https://github.com/ggml-org/llama.cpp | MIT |
| Qwen3-4B（GGUF Q4 量化） | 文本模型 | https://github.com/QwenLM/Qwen3 | Apache-2.0 |
| Open WebUI | 网页版前端（桌面端另有原生界面） | https://github.com/open-webui/open-webui | 见上游 LICENSE |
| Qdrant | 向量库 | https://github.com/qdrant/qdrant | Apache-2.0 |
| ChromaDB | 知识库索引 | https://github.com/chroma-core/chroma | Apache-2.0 |
| FastAPI / Uvicorn | 各服务 HTTP 框架 | https://github.com/fastapi/fastapi | MIT |
| python-docx | 文档生成 | https://github.com/python-openxml/python-docx | MIT |
| Vision 模型（Qwen2.5-VL 系列） | 图像理解 | https://github.com/QwenLM/Qwen2.5-VL | Apache-2.0 |

若你是上述项目的作者并认为分发方式不妥，请提 issue，会立即调整。
