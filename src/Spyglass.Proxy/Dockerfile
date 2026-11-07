# syntax=docker/dockerfile:1.7
#
# spyglass-proxy (01): the wire proxy. One container per upstream deployment.
#
#   docker build -f src/Spyglass.Proxy/Dockerfile -t spyglass-proxy .
#   docker run -d --name spyglass-proxy -p 27017:27017 -p 9187:9187 \
#     -e SPYGLASS_UPSTREAM__URI=mongodb://db1:27017,db2:27017/?replicaSet=rs0 \
#     -e SPYGLASS_LISTEN__ADVERTISEHOST=spyglass-proxy \
#     -e SPYGLASS_COLLECTOR__ENDPOINT=http://spyglass-collector:8080/ingest/v0 \
#     -e SPYGLASS_COLLECTOR__TOKEN=<ingest token> spyglass-proxy
#
# Every 01 §2 key is an environment variable: SPYGLASS_ prefix, `__` between sections (SPYGLASS_LISTEN__BASEPORT),
# or a mounted file given as SPYGLASS_CONFIG. The image binds the listeners and the metrics endpoint on 0.0.0.0;
# clients outside the container need `listen.advertiseHost` to be the name they reach the container by, because
# that name is written into rewritten hello replies (01 §5). A replica set needs one published port per member
# (basePort, basePort+1, ...; see listen.portRange).
#
# Self-contained, trimmed and ReadyToRun on the chiseled runtime-deps base: the proxy is BCL-only (01 §1), so the
# trimmer has nothing to guess about. No shell, no package manager, non-root. The HEALTHCHECK is the binary itself:
# it GETs /readyz on the metrics listener (bound listeners and a successful upstream hello in the last 30 s).

ARG DOTNET=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET}-noble AS build
ARG TARGETARCH
WORKDIR /src
COPY src/Spyglass.Proxy/Spyglass.Proxy.csproj src/Spyglass.Proxy/
RUN --mount=type=cache,id=spyglass-nuget,target=/root/.nuget/packages \
    dotnet restore src/Spyglass.Proxy/Spyglass.Proxy.csproj -a $TARGETARCH -p:PublishTrimmed=true -p:SelfContained=true -p:PublishReadyToRun=true
COPY src/Spyglass.Proxy/ src/Spyglass.Proxy/
RUN --mount=type=cache,id=spyglass-nuget,target=/root/.nuget/packages \
    dotnet publish src/Spyglass.Proxy/Spyglass.Proxy.csproj -c Release --no-restore -a $TARGETARCH \
        --self-contained -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:DebugType=none -o /out/app

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET}-noble-chiseled
WORKDIR /app
COPY --from=build /out/app /app
ENV SPYGLASS_LISTEN__BINDADDRESS=0.0.0.0 \
    SPYGLASS_METRICS__BIND=0.0.0.0:9187
USER $APP_UID
EXPOSE 27017 9187
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/app/spyglass-proxy", "healthcheck"]
ENTRYPOINT ["/app/spyglass-proxy"]
