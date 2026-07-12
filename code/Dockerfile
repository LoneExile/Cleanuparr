# Build .NET backend (must be before frontend-build so BuildKit can serialize)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.1
ARG PACKAGES_USERNAME
ARG PACKAGES_PAT
WORKDIR /app
EXPOSE 11011

# Copy source code
COPY backend/ ./backend/

# Add private GitHub Packages NuGet feed (FLM.QBittorrent/FLM.Transmission).
# Skip if PACKAGES_USERNAME/PACKAGES_PAT aren't set (e.g. fork builds without GitHub PAT).
RUN if [ -n "$PACKAGES_USERNAME" ] && [ -n "$PACKAGES_PAT" ]; then \
      dotnet nuget add source --username "$PACKAGES_USERNAME" --password "$PACKAGES_PAT" --store-password-in-clear-text --name Cleanuparr https://nuget.pkg.github.com/Cleanuparr/index.json; \
    else echo "Skipping private NuGet feed (no GitHub Packages credentials)"; fi

# Restore and publish with cache mount
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore ./backend/Cleanuparr.Api/Cleanuparr.Api.csproj -a $TARGETARCH && \
    dotnet publish ./backend/Cleanuparr.Api/Cleanuparr.Api.csproj \
    -a $TARGETARCH \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:Version=${VERSION} \
    /p:PublishSingleFile=true \
    /p:DebugSymbols=false \
    /p:PublishReadyToRun=false

# Angular frontend — pre-built locally (npm run build -- --configuration production)
# and committed as frontend/dist/. The Angular 22 build is too memory-hungry
# (>8Gi) for the in-cluster GitLab runner, so we build on a host with enough RAM
# and just copy the output here.

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Install required packages for user management, timezone support, and Python for Apprise CLI
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    tzdata \
    gosu \
    python3 \
    python3-venv \
    && rm -rf /var/lib/apt/lists/*

# Create virtual environment and install Apprise CLI
ENV VIRTUAL_ENV=/opt/apprise-venv
RUN python3 -m venv $VIRTUAL_ENV
ENV PATH="$VIRTUAL_ENV/bin:$PATH"
RUN pip install --no-cache-dir apprise==1.9.6

ENV PUID=1000 \
    PGID=1000 \
    UMASK=022 \
    TZ=Etc/UTC \
    HTTP_PORTS=11011

# Fix FileSystemWatcher in Docker: https://github.com/dotnet/dotnet-docker/issues/3546
ENV DOTNET_USE_POLLING_FILE_WATCHER=true

# GC tuning: prefer lower memory footprint over throughput
ENV DOTNET_GCDynamicAdaptationMode=1 \
    DOTNET_GCConserveMemory=5 \
    DOTNET_GCRetainVM=0

WORKDIR /app

# Copy backend
COPY --from=build /app/publish .
# Copy frontend to wwwroot (pre-built, see note above)
COPY frontend/dist/ui/browser ./wwwroot
# Copy entrypoint script
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["./Cleanuparr"]