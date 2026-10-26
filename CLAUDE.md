# Project Guidelines

## Testing

- Write tests for every new feature before considering it done.
- When a bug is reported, write a regression test that reproduces it before fixing.
- Tests live in `src/api/PirateChess.Api.Tests/` (xUnit + WebApplicationFactory with InMemory DB).
- Run tests with: `cd src/api/PirateChess.Api.Tests && dotnet test`
- CI (`.github/workflows/build-push.yml`): Job `test` laeuft vor jedem Image-Push; rote Tests = kein `:dev`/`:latest`.

## Git

- Commit every change immediately after completing it — do not batch unrelated changes.
- Use concise German or English commit messages describing what was done.

## Stack

- Backend-only Service: ASP.NET 10 API, MariaDB (EF Core, Microting-MySQL-Provider), SignalR
- Chessable-HTTP: curl-impersonate (TLS-Fingerprint) ueber `ICurlRunner`, Bearer per stdin, Proxy = gluetun-Tunnel
- Library: piratechess_lib (namespace `piratechess_lib`) fuer Parsen + PGN (`GetCourse` mit lokalen Daten);
  ihre RestSharp-Aufrufe nutzt der Server nicht
- Container-Port 8080 (Dev-Stack: Host-Port 5003; Prod: kein Host-Port; Repo-Beispiel: 127.0.0.1:5000)

## Betrieb

- piratechess-api laeuft als Dienst INNERHALB der RookHub-Stacks: `/opt/stacks/rookhub-schach` (Prod,
  `ghcr.io/kahalm/piratechess-api:latest`, zwei gluetun-Tunnel) und `/opt/stacks/rookhub-schach-dev` (Dev, `:dev`,
  ein Tunnel). Massgeblich sind die dortigen `compose.yaml`, `.env` (Schluessel `PIRATECHESS_*`) und
  `gluetun-auth.toml`: Proxy-/Rotations-Einstellungen (`Chessable__ProxyUrls`, `Gluetun__ControlUrls`,
  `Vpn__RotateOnBlock`, Delays) stehen nur dort.
- DB: eigene Datenbank in der gemeinsamen rookhub-MariaDB, angelegt vom `init-db.sh` des Stacks; Schema per
  EF-Migrationen beim Start. Logs: gemeinsames Elasticsearch (Index `piratechess-logs-*` bzw. `piratechess-dev-logs-*`).
- Deploy: Push auf master -> `:dev`, vX.Y.Z-Tag auf master -> `:latest`; Watchtower zieht nachts.
- Die Repo-Dateien `docker-compose.yml` (+ `docker-compose.override.yml` mit `dotnet watch`), `.env.example` und
  `gluetun-auth.toml` sind nur ein Beispiel fuer die lokale Entwicklung (eigener Tunnel, eigene DB, Ports an
  127.0.0.1, gluetun-Control ohne Key). Eine eigene Prod-Compose gibt es im Repo bewusst nicht mehr.

## Architektur

Dieses Repo enthaelt nur noch das Backend. UI/Frontend liegt jetzt komplett im
RookHub-Stack (`../rookhub`): rookhub-api leitet User-Bearer per X-Service-Key
an `/api/chessable/direct/*` (Stateless) - das ist der einzige Aufrufer.
Die JWT-Endpoints (`/api/auth`, `/api/chessable/{credentials,test,courses}`,
`/api/export`, `/hubs/export-progress`) stammen vom entfernten Frontend und
haben keinen Aufrufer mehr: die Registrierung ist standardmaessig aus
(`Auth:RegistrationEnabled`), `/api/vpn/rotate` verlangt nur den Service-Key.

- `ChessableDirectController`: duenne Endpunkte; Kursabruf (im Job) ueber `CourseDataProvider` (Cache -> Bid-Lock ->
  Double-Check -> Fetch -> `RawCourseCache.SetAsync`), `TrainingMode` an einer Stelle, Browser-Parse in
  `BrowserCourseParseService`, PGN-Schritt in `CoursePgn`. `course/start` legt ueber `CourseFetchJobRunner` einen
  Job in `CourseFetchJobStore` an (Poll per GET, Abbruch per DELETE oder beim Container-Stopp). Der tiefe Kursabruf laeuft NUR
  noch ueber diesen Job-Weg — den synchronen `POST /api/chessable/direct/course` gibt es seit I2-011 nicht mehr
  (rookhub nutzt ihn seit 0.837.2 nicht; ein Aufruf liefert 404).
- `ChessableHttpService`: Fassade des Chessable-Abrufs (Login, Kursliste, Schaetzung, Diagnose); tiefer Kursabruf
  in `ChessableCourseFetcher` (Retry, Pruefregel, Linien-Cache; Rueckmeldungen per `CourseFetchOptions`), je
  Request `ChessableCurlTransport` (VPN-Lease, Soft-Block, Audit ueber `RawResponseAudit`), Prozess in `CurlRunner`,
  Argumente/Fingerprint in `ChessableCurlArgs`, reine Antwort-Klassifikation in `ChessableResponseClassifier`.
- Caches: `RawCourseCache` (Kurs + Kapitel), `RawLineCache` (dauerhafter Linien-Cache, nie loeschen, nur
  als ungueltig markieren).
- `ChessableRawResponses` (Audit, gzip+Base64) ist zugleich die einzige Quelle der Kurs-/Kapitelstruktur fuer
  `RawCourseReconstructor` (BOOK_NOT_OWNED-Kurse). `RawResponseRetentionService` loescht nach
  `ChessableRawResponses:RetentionDays` (Default 14): das Fenster ist die Frist fuer die Rekonstruktion.
- VPN: `VpnRotationService`/`VpnTunnel` (Tunnel-Pool, IP an Chessable-Token gekoppelt), gluetun-Control per
  `Gluetun__ControlUrl(s)`, optional `Gluetun__ApiKey` als X-API-Key.
- Lebenszeichen: `HeartbeatService` 1x/min fuer den log-watcher.
