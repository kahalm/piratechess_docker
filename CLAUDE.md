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

- Backend: ASP.NET 8 API, MariaDB (Pomelo EF Core), SignalR
- Frontend: React 18, TypeScript, Vite, react-router-dom v6, @microsoft/signalr
- Library: piratechess_lib (namespace `piratechess_lib`, synchronous RestSharp calls)
- Docker: Gluetun VPN, MariaDB, API, Frontend (dev via docker-compose.override.yml)
- Frontend port: 8084 (dev), API port: 5000
