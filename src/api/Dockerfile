# curl-impersonate-chrome (Chrome-TLS-Fingerprint für Chessable): EINMAL laden und gegen eine feste SHA-256
# prüfen. Ein ausgetauschtes Release-Asset bricht so den Build ab, statt unbemerkt ins Image zu wandern (das
# Binary bekommt jedes Chessable-Bearer). dev und final kopieren nur das geprüfte Ergebnis.
# Neue Version: Asset laden, sha256sum berechnen, URL und Summe hier gemeinsam ändern.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS curl-impersonate
ADD https://github.com/lwthiker/curl-impersonate/releases/download/v0.6.1/curl-impersonate-v0.6.1.x86_64-linux-gnu.tar.gz /tmp/curl-imp.tar.gz
RUN echo "fa1e1614f7ba69ccc66721a0f38be457a3647eb64c75d66974b56186e3316b12  /tmp/curl-imp.tar.gz" | sha256sum -c - \
    && mkdir /out \
    && tar -xzf /tmp/curl-imp.tar.gz -C /out --no-same-owner \
    && chmod +x /out/curl_*

# Dev target for docker-compose.override.yml
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dev
WORKDIR /app

# Install curl-impersonate-chrome (dev)
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=curl-impersonate /out/ /usr/local/bin/

COPY . .
RUN dotnet restore src/api/PirateChess.Api/PirateChess.Api.csproj
# entrypoint set in override

# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/lib/piratechess_lib/ src/lib/piratechess_lib/
COPY src/api/PirateChess.Api/ src/api/PirateChess.Api/
RUN dotnet restore src/api/PirateChess.Api/PirateChess.Api.csproj
RUN dotnet publish src/api/PirateChess.Api/PirateChess.Api.csproj \
    -c Release -o /app/publish --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install curl-impersonate-chrome
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=curl-impersonate /out/ /usr/local/bin/

COPY --from=build /app/publish .

# Build-Herkunft (vom CI als --build-arg gesetzt) → als ENV, von GET /api/chessable/direct/build-info
# ausgeliefert, damit RookHubs Admin-CI-Seite den laufenden piratechess-Build-Run markieren kann.
ARG GIT_SHA=
ARG GIT_REF=
ENV BUILD_GIT_SHA=$GIT_SHA
ENV BUILD_GIT_REF=$GIT_REF

# Install EF Core tools for migrations
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Nicht als root laufen: das aspnet-Image bringt den Nutzer app ($APP_UID) mit. Port 8080 braucht kein root,
# und die API schreibt nichts unter /app (curl-impersonate gibt nur auf stdout aus, kein Volume im Stack).
USER $APP_UID

ENTRYPOINT ["dotnet", "PirateChess.Api.dll"]
