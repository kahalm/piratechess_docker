using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PirateChess.Api.BackgroundJobs;
using PirateChess.Api.Data;
using PirateChess.Api.Hubs;
using PirateChess.Api.Services;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Elastic.Serilog.Sinks;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        // Pro HttpClient-Request loggt Microsoft.Extensions.Http sonst 4 INF-Zeilen
        // (Start/Sending/Received/End) — bei IP-Rotation + publicip-Polling + Proxy-Probe
        // ein Vielfaches pro Kursabruf. Nur noch Warnungen behalten.
        .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("Application", "PirateChess")
        .WriteTo.Console();

    var esUrl = context.Configuration["Elasticsearch:Url"];
    if (!string.IsNullOrEmpty(esUrl))
    {
        // ECS-Schema (Elastic.Serilog.Sinks) in einen Data-Stream. Eigener Index
        // `piratechess-logs-*`; rookhub/log-watcher koennen das Pattern mitlesen.
        var indexFormat = context.Configuration["Elasticsearch:IndexFormat"] ?? "piratechess-logs-{0:yyyy.MM}";
        var streamName = indexFormat.Split('{')[0].TrimEnd('-', '.', ' ');
        configuration.WriteTo.Elasticsearch([new Uri(esUrl)], opts =>
        {
            opts.DataStream = new Elastic.Ingest.Elasticsearch.DataStreams.DataStreamName(streamName);
            opts.BootstrapMethod = Elastic.Ingest.Elasticsearch.BootstrapMethod.Silent;
            // Bei Chessable-Requests user.name auf den echten Chessable-Username setzen
            // (aus dem Bearer, via LogContext-Property "ChessableUser") statt des OS-Users
            // "root", den die ECS-Sink sonst aus der Container-Umgebung einträgt.
            opts.TextFormatting.MapCustom = (doc, logEvent) =>
            {
                if (logEvent.Properties.TryGetValue("ChessableUser", out var p)
                    && p is Serilog.Events.ScalarValue { Value: string uname }
                    && !string.IsNullOrEmpty(uname))
                {
                    doc.User ??= new Elastic.CommonSchema.User();
                    doc.User.Name = uname;
                    doc.User.Domain = null;
                }
                return doc;
            };
        });
    }
});

// Pflicht-Konfiguration früh validieren → Fail-FAST beim Start statt erst beim ersten Request/Login.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection not configured");
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret not configured");
if (Encoding.UTF8.GetBytes(jwtSecret).Length < 32)
    throw new InvalidOperationException("Jwt:Secret must be at least 32 bytes for HMAC-SHA256");
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer not configured");
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience not configured");

// Database
var serverVersion = new MariaDbServerVersion(new Version(11, 0, 0));
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, serverVersion));

// Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            NameClaimType = ClaimTypes.NameIdentifier
        };

        // Allow SignalR to receive token from query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Services
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<ExportJobQueue>();
builder.Services.AddSingleton<CourseFetchJobStore>();
builder.Services.AddSingleton<RawCourseCache>();
builder.Services.AddSingleton<RawCourseReconstructor>();
builder.Services.AddSingleton<RawLineCache>();

// Un-proxied HttpClient für den gluetun-Control-Server (:8000). Diese Calls
// dürfen NICHT durch den :8888-Proxy laufen → UseProxy=false. Registriert
// nebenbei IHttpClientFactory.
//
// PooledConnectionIdleTimeout bewusst kurz (< Vpn:RestartPauseMs): Eine Rotation
// macht stop-PUT → Pause (~3s, gluetun setzt sein Netz neu auf) → start-PUT. In
// dieser Pause schließt gluetun die serverseitige Keep-Alive-Verbindung. Mit dem
// .NET-Default (1 min) griffe der start-PUT die tote gepoolte Verbindung wieder
// auf → "Connection reset by peer". Mit kurzem Idle-Timeout verwirft .NET die
// inaktive Verbindung und öffnet für den start-PUT eine frische.
builder.Services.AddHttpClient(VpnRotationService.ClientName, client =>
    {
        // gluetun-Control-Server härten: ist ein API-Key gesetzt (Gluetun:ApiKey, aus der .env),
        // wird er als X-API-Key mitgeschickt → gluetun-auth.toml kann von auth="none" auf
        // auth="apikey" umgestellt werden. Ohne Key (leer) bleibt das Verhalten wie bisher
        // (rückwärtskompatibel) → Code kann deployt werden, bevor der Key auf beiden Seiten aktiv ist.
        var apiKey = builder.Configuration["Gluetun:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(1),
    });

// Proxied HttpClient ("Chessable") für den gluetun-Proxy (:8888). Wird von
// VpnRotationService.WaitForProxyReadyAsync (Readiness-Probe nach Rotation) UND
// VpnController (IP-Status-Fallback) genutzt. OHNE diese Registrierung liefert
// CreateClient("Chessable") einen Default-Client OHNE Proxy → Probe/Status liefen
// mit der Host-IP am Tunnel vorbei (Probe wirkungslos, Status meldet Host-IP).
builder.Services.AddChessableHttpClient(builder.Configuration);

// VPN-IP-Rotation (gluetun) — teilt sich denselben gluetun wie der Crawler.
builder.Services.AddSingleton<VpnIpHealth>();   // Per-IP-Request-/Block-Buchführung
builder.Services.AddSingleton<IVpnRotationService, VpnRotationService>();

// Chessable HTTP service (curl-impersonate for TLS fingerprint bypass)
builder.Services.AddSingleton<IChessableHttpService, ChessableHttpService>();

// Background export worker
builder.Services.AddHostedService<ExportBackgroundService>();

// Hält die Audit-Tabelle ChessableRawResponses klein (Retention, Default 14 Tage)
builder.Services.AddHostedService<RawResponseRetentionService>();

// SignalR
builder.Services.AddSignalR();

// Rate-Limiter für /api/chessable/direct/* (benannte Policy "direct", per [EnableRateLimiting] am
// Controller). EIN gemeinsames Fixed-Window für alle direct-Aufrufer: schützt vor Amok-Schleifen/
// Missbrauch, ohne legitimen Betrieb zu bremsen — rookhub pollt Fortschritt alle 2,5 s (~24 Requests/min
// je Import-Job), der Default (300/min) lässt also reichlich Luft. Die langlaufenden Fetch-Jobs selbst
// laufen im Hintergrund weiter; der Limiter gate't nur die Request-ANNAHME, bricht also nichts ab.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests; // statt Default 503
    options.AddFixedWindowLimiter("direct", o =>
    {
        // Lambda läuft erst beim Aufbau der Middleware → sieht auch Test-Config (WebApplicationFactory).
        o.PermitLimit = builder.Configuration.GetValue("RateLimit:Direct:PermitLimit", 300);
        o.Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimit:Direct:WindowSeconds", 60));
        o.QueueLimit = 0; // kein Anstellen: über dem Limit sofort 429 (Aufrufer sollen backoffen)
    });
});

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Auto-migrate on startup (skip for InMemory DB in tests)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        db.Database.Migrate();
}

// Globaler Exception-Handler als ERSTES Request-Middleware-Glied: ungefangene Exceptions werden zu
// einer ProblemDetails-500-Antwort OHNE Stacktrace/Internas (bewusst in ALLEN Umgebungen — interner
// Backend-Service, keine Developer-Exception-Page, kein Leak selbst bei versehentlichem Development-Env).
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();

// Reichert jedes Log-Event im Request mit UserId/UserName/IpAddress an (analog rookhub).
app.Use(async (ctx, next) =>
{
    var scopes = new List<IDisposable>(3);
    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    if (!string.IsNullOrEmpty(ip))
        scopes.Add(LogContext.PushProperty("IpAddress", ip));
    if (ctx.User?.Identity?.IsAuthenticated == true)
    {
        var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userId))
            scopes.Add(LogContext.PushProperty("UserId", userId));
        if (!string.IsNullOrEmpty(ctx.User.Identity.Name))
            scopes.Add(LogContext.PushProperty("UserName", ctx.User.Identity.Name));
    }
    try { await next(); }
    catch (UnauthorizedAccessException) when (!ctx.Response.HasStarted)
    {
        // Fehlender/ungültiger User-Id-Claim (s. ClaimsPrincipalExtensions.GetRequiredUserId) → 401 statt 500.
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
    finally { for (var i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose(); }
});

app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        var path = httpContext.Request.Path.Value ?? "";
        if (path.StartsWith("/health") || path.StartsWith("/swagger"))
            return LogEventLevel.Debug;
        if (ex != null || httpContext.Response.StatusCode >= 500)
            return LogEventLevel.Error;
        return LogEventLevel.Information;
    };
});

// Vor UseAuthorization: geratelimitete Requests werden abgewiesen, BEVOR weitere Pipeline-Arbeit anfällt.
// Greift nur auf Endpoints mit [EnableRateLimiting]-Policy (direct/*) — /health & Co. bleiben unlimitiert.
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();
app.MapHub<ExportProgressHub>("/hubs/export-progress");

app.Run();

// Marker class for WebApplicationFactory<Program> in tests
public partial class Program { }
