# The circleguard replay analyzer for the replay-check plugin.
#   docker build -t replay-analyzer analyzer/
#   docker run -d -p 127.0.0.1:8600:8600 -e ANALYZER_KEY=change-me replay-analyzer
FROM python:3.13-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY analyzer.py .
RUN useradd --system --uid 10001 analyzer
USER analyzer
ENV PYTHONUNBUFFERED=1 ANALYZER_PORT=8600
EXPOSE 8600
HEALTHCHECK CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8600/health', timeout=3)"
CMD ["python", "analyzer.py"]
