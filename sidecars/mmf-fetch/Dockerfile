# Forgekeeper MMF fetch sidecar — curl_cffi (impersonate=chrome) relay, loopback only.
FROM mirror.gcr.io/library/python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 \
    FETCH_BIND=127.0.0.1 FETCH_PORT=8199
WORKDIR /app
COPY requirements.txt .
RUN pip install -r requirements.txt
COPY app.py .
USER 65534:65534
EXPOSE 8199
CMD ["python", "app.py"]
