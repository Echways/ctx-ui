FROM ghcr.io/astral-sh/uv:0.13.6 AS uv

FROM python:3.13-slim
COPY --from=uv /uv /uvx /usr/local/bin/
COPY prepare.py /opt/builder/prepare.py
COPY main.py /opt/builder/main.py
RUN mkdir -p /code /env /data /uv-cache \
    && chown 10001:10001 /code /env /data /uv-cache
ENV UV_CACHE_DIR=/uv-cache \
    UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=never \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
USER 10001
WORKDIR /code
