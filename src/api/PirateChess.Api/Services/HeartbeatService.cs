using PirateChess.Api.Data;

namespace PirateChess.Api.Services;

/// <summary>
/// Schreibt periodisch (Standard 60 s, via <c>Heartbeat:IntervalSeconds</c>) ein strukturiertes
/// „Heartbeat"-Log nach Elasticsearch (Index piratechess-logs-*) — damit der log-watcher einen
/// toten/hängenden Dienst an AUSBLEIBENDEN Heartbeats erkennt. piratechess loggt sonst nur, wenn
/// rookhub etwas abruft; Stille heißt hier also nichts. Enthält einen kurzen Selbst-Check
/// (DB erreichbar) → Status healthy/degraded.
///
/// Datei-Kopie des HeartbeatService aus rookhub und Crawler (gleiches Template, gleiches Feld). Der Satz
/// „Heartbeat: {HeartbeatService} …" und das Feld <c>HeartbeatService</c> (ECS: labels.HeartbeatService)
/// sind der Vertrag mit dem log-watcher; HeartbeatServiceTests pinnt beides wörtlich.
/// </summary>
public class HeartbeatService : BackgroundService
{
    public const string ServiceName = "piratechess-api";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly TimeSpan _interval;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public HeartbeatService(IServiceScopeFactory scopeFactory, ILogger<HeartbeatService> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var seconds = config.GetValue<int?>("Heartbeat:IntervalSeconds") ?? 60;
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EmitAsync();   // erstes Lebenszeichen sofort beim Start
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await EmitAsync();
        }
        catch (OperationCanceledException) { /* Shutdown */ }
    }

    /// <summary>Einen Heartbeat (mit DB-Selbst-Check) loggen. public als Test-Seam.</summary>
    public async Task EmitAsync()
    {
        bool dbOk;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbOk = await db.Database.CanConnectAsync();
        }
        catch
        {
            dbOk = false;
        }

        var uptimeSeconds = (int)(DateTime.UtcNow - _startedAt).TotalSeconds;
        _logger.LogInformation(
            "Heartbeat: {HeartbeatService} {HeartbeatStatus} db={HeartbeatDbOk} uptime={HeartbeatUptimeSeconds}s",
            ServiceName, dbOk ? "healthy" : "degraded", dbOk, uptimeSeconds);
    }
}
