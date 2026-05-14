# Dev target for docker-compose.override.yml
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dev
WORKDIR /app
COPY . .
RUN dotnet restore src/api/PirateChess.Api/PirateChess.Api.csproj
# entrypoint set in override

# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY piratechess/piratechess_lib/ piratechess/piratechess_lib/
COPY src/api/PirateChess.Api/ src/api/PirateChess.Api/
RUN dotnet restore src/api/PirateChess.Api/PirateChess.Api.csproj
RUN dotnet publish src/api/PirateChess.Api/PirateChess.Api.csproj \
    -c Release -o /app/publish --no-restore

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Install EF Core tools for migrations
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PirateChess.Api.dll"]
