# Dev target for docker-compose.override.yml
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dev
WORKDIR /app

# Install curl-impersonate-chrome (dev)
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates && rm -rf /var/lib/apt/lists/*
ADD https://github.com/lwthiker/curl-impersonate/releases/download/v0.6.1/curl-impersonate-v0.6.1.x86_64-linux-gnu.tar.gz /tmp/curl-imp.tar.gz
RUN tar -xzf /tmp/curl-imp.tar.gz -C /usr/local/bin/ \
    && rm /tmp/curl-imp.tar.gz \
    && chmod +x /usr/local/bin/curl_*

COPY . .
RUN dotnet restore src/api/PirateChess.Api/PirateChess.Api.csproj
# entrypoint set in override

# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/lib/piratechess_lib/ src/lib/piratechess_lib/
COPY src/api/PirateChess.Api/ src/api/PirateChess.Api/
RUN dotnet restore src/api/PirateChess.Api/PirateChess.Api.csproj
RUN dotnet publish src/api/PirateChess.Api/PirateChess.Api.csproj \
    -c Release -o /app/publish --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Install curl-impersonate-chrome
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates && rm -rf /var/lib/apt/lists/*
ADD https://github.com/lwthiker/curl-impersonate/releases/download/v0.6.1/curl-impersonate-v0.6.1.x86_64-linux-gnu.tar.gz /tmp/curl-imp.tar.gz
RUN tar -xzf /tmp/curl-imp.tar.gz -C /usr/local/bin/ \
    && rm /tmp/curl-imp.tar.gz \
    && chmod +x /usr/local/bin/curl_*

COPY --from=build /app/publish .

# Install EF Core tools for migrations
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PirateChess.Api.dll"]
