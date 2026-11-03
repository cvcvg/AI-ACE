FROM python:3.11-slim
RUN pip config set global.index-url https://mirrors.aliyun.com/pypi/simple

WORKDIR /app

COPY requirements.txt .

RUN pip install --no-cache-dir -r requirements.txt

COPY app .

EXPOSE 8003

HEALTHCHECK NONE

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8003"]
