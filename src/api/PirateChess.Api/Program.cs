using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
        });
    }
});

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
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
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!)),
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

// Un-proxied HttpClient für den gluetun-Control-Server (:8000). Diese Calls
// dürfen NICHT durch den :8888-Proxy laufen → UseProxy=false. Registriert
// nebenbei IHttpClientFactory.
builder.Services.AddHttpClient(VpnRotationService.ClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false });

// VPN-IP-Rotation (gluetun) — teilt sich denselben gluetun wie der Crawler.
builder.Services.AddSingleton<IVpnRotationService, VpnRotationService>();

// Chessable HTTP service (curl-impersonate for TLS fingerprint bypass)
builder.Services.AddSingleton<IChessableHttpService, ChessableHttpService>();

// Background export worker
builder.Services.AddHostedService<ExportBackgroundService>();

// SignalR
builder.Services.AddSignalR();

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Auto-migrate on startup (skip for InMemory DB in tests)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        db.Database.Migrate();
}

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

app.UseAuthorization();

app.MapControllers();
app.MapHub<ExportProgressHub>("/hubs/export-progress");

app.Run();

// Marker class for WebApplicationFactory<Program> in tests
public partial class Program { }
