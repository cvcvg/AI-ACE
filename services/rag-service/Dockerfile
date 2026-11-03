FROM python:3.11-slim
RUN pip config set global.index-url https://mirrors.aliyun.com/pypi/simple

RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .

# 强制安装 CPU 版 torch (精确锁定 +cpu 版本, 避免拉到 CUDA 版)
RUN pip install --no-cache-dir --default-timeout=600 "https://mirrors.aliyun.com/pytorch-wheels/cpu/torch-2.13.0%2Bcpu-cp311-cp311-manylinux_2_28_x86_64.whl"

RUN pip install --no-cache-dir -r requirements.txt

COPY app .

EXPOSE 8002

HEALTHCHECK NONE

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8002"]
