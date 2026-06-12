# Project Guidelines

## Testing

- Write tests for every new feature before considering it done.
- When a bug is reported, write a regression test that reproduces it before fixing.
- Tests live in `src/api/PirateChess.Api.Tests/` (xUnit + WebApplicationFactory with InMemory DB).
- Run tests with: `cd src/api/PirateChess.Api.Tests && dotnet test`

## Git

- Commit every change immediately after completing it — do not batch unrelated changes.
- Use concise German or English commit messages describing what was done.

## Stack

- Backend-only Service: ASP.NET 8 API, MariaDB (Pomelo EF Core), SignalR
- Library: piratechess_lib (namespace `piratechess_lib`, synchronous RestSharp calls)
- Docker: Gluetun VPN, MariaDB, API (dev via docker-compose.override.yml mit `dotnet watch`)
- API-Port: 5000

## Architektur

Dieses Repo enthaelt nur noch das Backend. UI/Frontend liegt jetzt komplett im
RookHub-Stack (`../rookhub`): rookhub-api leitet User-Bearer per X-Service-Key
an `/api/chessable/direct/*` (Stateless) bzw. nutzt die uebrigen JWT-Endpoints
fuer Export-Jobs. Inter-Stack-Verkabelung: externes Docker-Netz
`chessable-bridge`, gemeinsame Elasticsearch fuer Logs.
