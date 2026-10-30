# The sandbox server as a container image, built by .github/workflows/images.yml through the shared mcp-image.yml and
# pushed from a version tag as ghcr.io/<owner>/s3l-sandbox-mcp.
#
#   docker build -t snail/s3l-sandbox-mcp:latest -f deploy/server/Dockerfile .
#
# It needs one thing from the node: the Docker socket, which the job mounts at /var/run/docker.sock and opens to this
# image's non-root user by adding the socket's group. Inputs and outputs travel through the Engine API rather than bind
# mounts, so no host path has to match a path inside this container.
#
# VERSION is 0.0.0-local unless the build passes one — the shared workflow passes the release's version from its tag —
# so the server reports in initialize what it is, and the image carries it as org.opencontainers.image.version.
#
# The SDK is pinned to the band global.json asks for: on the floating sdk:10.0 a newer feature band would one day
# refuse the build. The server is native: compiled ahead of time, which needs clang and zlib in the build stage, for
# the platform the image is built for (TARGETARCH, which buildx sets: amd64 is linux-x64, arm64 is linux-arm64), since
# ahead-of-time compilation does not cross architectures in the SDK image; and run on runtime-deps, with no .NET
# runtime in the image. The restore reads Snail.Sdk from the sources nuget.config names, so a build on a machine
# whose Snail.Sdk is a local package needs that feed reachable from the build.
ARG VERSION=0.0.0-local

FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ARG VERSION
ARG TARGETARCH
RUN apt-get update \
    && apt-get install --yes --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN case "$TARGETARCH" in amd64|"") rid=linux-x64 ;; *) rid="linux-$TARGETARCH" ;; esac \
    && dotnet publish src/Snail.MCP.Sandbox.csproj -c Release -r "$rid" -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
ARG VERSION
LABEL org.opencontainers.image.version=$VERSION
WORKDIR /app
COPY --from=build /app .
ENV SNAIL_MCP_SANDBOX_URL=http://0.0.0.0:8826/ \
    SNAIL_MCP_SANDBOX_WORK_ROOT=/tmp/snail-sandbox
EXPOSE 8826
USER app
ENTRYPOINT ["/app/Snail.MCP.Sandbox"]
