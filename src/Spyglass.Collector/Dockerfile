# syntax=docker/dockerfile:1.7
#
# spyglass-collector (03): ingest, API and UI in one container.
#
#   docker build -f src/Spyglass.Collector/Dockerfile -t spyglass-collector .
#   docker run -d --name spyglass -p 8080:8080 -v spyglass-data:/data \
#     -e SPYGLASS_TOKEN=<ingest token> -e SPYGLASS_ADMIN_PASSWORD=<ui password> spyglass-collector
#
# Self-contained, trimmed and ReadyToRun on the chiseled runtime-deps base: no shell, no package manager, no
# shared framework, a non-root user. ~56 MB of app (11 MB of it the UI, precompressed) on a ~13 MB base.
# ReadyToRun costs 13 MB and takes the HEALTHCHECK probe (a fresh process every 30 s) from 0.2 s to 0.05 s of
# CPU, and startup JIT with it. Multi-arch: the SDK stage runs on the build platform and cross-publishes for
# TARGETARCH (docker buildx build --platform linux/amd64,linux/arm64); crossgen2 cross-compiles.
# Configuration is SPYGLASS_* environment variables (03 §9); the HEALTHCHECK reads SPYGLASS_BIND, so set the
# listener there rather than with --bind.

ARG DOTNET=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET}-noble AS build
ARG TARGETARCH
WORKDIR /src

# Restore on its own layer: it is reused until a project file changes.
COPY src/Spyglass.Collector/Spyglass.Collector.csproj src/Spyglass.Collector/
COPY src/Spyglass.Collector.Ui/Spyglass.Collector.Ui.csproj src/Spyglass.Collector.Ui/
RUN --mount=type=cache,id=spyglass-nuget,target=/root/.nuget/packages \
    dotnet restore src/Spyglass.Collector.Ui/Spyglass.Collector.Ui.csproj && \
    dotnet restore src/Spyglass.Collector/Spyglass.Collector.csproj -a $TARGETARCH -p:PublishTrimmed=true -p:SelfContained=true -p:PublishReadyToRun=true

COPY src/Spyglass.Collector/ src/Spyglass.Collector/
COPY src/Spyglass.Collector.Ui/ src/Spyglass.Collector.Ui/
# The UI is WebAssembly: built once, the same for every architecture. Its CSS is the committed
# wwwroot/css/app.css (tools/collector/publish.sh rebuilds it with Tailwind when node is around).
RUN --mount=type=cache,id=spyglass-nuget,target=/root/.nuget/packages \
    dotnet publish src/Spyglass.Collector.Ui/Spyglass.Collector.Ui.csproj -c Release --no-restore -o /out/ui && \
    dotnet publish src/Spyglass.Collector/Spyglass.Collector.csproj -c Release --no-restore -a $TARGETARCH \
        --self-contained -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:DebugType=none -o /out/app && \
    mv /out/ui/wwwroot /out/app/wwwroot && \
    mkdir -p /out/data

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET}-noble-chiseled
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/data /data
COPY --from=build /out/app /app
ENV SPYGLASS_BIND=0.0.0.0:8080 \
    SPYGLASS_DATA=/data
USER $APP_UID
EXPOSE 8080
VOLUME /data
# Liveness (/healthz): every pipeline stage turning and storage writing. The probe is the collector binary
# itself (the image has no curl); it reads SPYGLASS_BIND and exits 0 or 1.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/app/spyglass-collector", "healthcheck"]
ENTRYPOINT ["/app/spyglass-collector"]
