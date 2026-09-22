# The image a run executes in: Python 3.12 with the analysis libraries, owned by root, used by nobody.
# docker build -t snail/sandbox-python:latest -f deploy/executor/Dockerfile .
#
# Everything is installed here because nothing can be at run time: the container has no network. The
# matplotlib font cache is not prebuilt — on this slim base it builds in 0.3 s, measured, and a prebuilt one
# would sit in a folder the read-only root does not let matplotlib use.
FROM python:3.12-slim-trixie

COPY deploy/executor/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir -r /tmp/requirements.txt \
 && rm /tmp/requirements.txt

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    MPLBACKEND=Agg

USER 65534:65534
WORKDIR /sandbox/files
CMD ["python"]
