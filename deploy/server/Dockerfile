# The sandbox server.
# docker build -t snail/s3l-sandbox-mcp:latest -f deploy/server/Dockerfile .
#
# It needs one thing from the node: the Docker socket, which the job mounts at /var/run/docker.sock and
# opens to this image's non-root user by adding the socket's group. Inputs and outputs travel through the
# Engine API rather than bind mounts, so no host path has to match a path inside this container.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8826 \
    Snail__WorkRoot=/tmp/snail-sandbox
EXPOSE 8826
USER $APP_UID
ENTRYPOINT ["dotnet", "Snail.MCP.Sandbox.dll"]
