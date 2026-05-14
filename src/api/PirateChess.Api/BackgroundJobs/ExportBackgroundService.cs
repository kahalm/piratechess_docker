using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Data;
using PirateChess.Api.Hubs;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.BackgroundJobs;

public class ExportBackgroundService : BackgroundService
{
    private readonly ExportJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ExportProgressHub> _hub;
    private readonly ILogger<ExportBackgroundService> _logger;

    public ExportBackgroundService(
        ExportJobQueue queue,
        IServiceScopeFactory scopeFactory,
        IHubContext<ExportProgressHub> hub,
        ILogger<ExportBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExportBackgroundService started");

        await foreach (var job in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await ProcessExportAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export job failed for ExportId {ExportId}", job.ExportId);
                await FailExportAsync(job, ex.Message);
            }
        }
    }

    private async Task ProcessExportAsync(ExportJobRequest job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var cred = await db.ChessableCredentials
            .FirstOrDefaultAsync(c => c.UserId == job.UserId, ct);

        if (cred is null)
        {
            await FailExportAsync(job, "No credentials found");
            return;
        }

        var lib = new piratechess_lib.PirateChessLib();
        string loginResult;

        if (cred.UseBearer && cred.EncryptedBearer is not null)
        {
            var bearer = encryption.Decrypt(cred.EncryptedBearer);
            loginResult = lib.LoginWithBearer(bearer);
        }
        else if (!cred.UseBearer && cred.EncryptedEmail is not null && cred.EncryptedPassword is not null)
        {
            var email = encryption.Decrypt(cred.EncryptedEmail);
            var password = encryption.Decrypt(cred.EncryptedPassword);
            loginResult = lib.Login(email, password);
        }
        else
        {
            await FailExportAsync(job, "Incomplete credentials");
            return;
        }

        if (!string.IsNullOrEmpty(loginResult))
        {
            await FailExportAsync(job, $"Login failed: {loginResult}");
            return;
        }

        // Configure training mode
        switch (job.TrainingMode)
        {
            case "AllKeyMoves":
                lib.AllKeyMovesTraining = true;
                lib.NoTrainingMove = false;
                break;
            case "FirstKeyMove":
                lib.AllKeyMovesTraining = false;
                lib.NoTrainingMove = false;
                break;
            case "None":
                lib.AllKeyMovesTraining = false;
                lib.NoTrainingMove = true;
                break;
        }

        var userGroup = $"user-{job.UserId}";
        int chaptersDone = 0;
        int chaptersTotal = 0;
        int linesDone = 0;

        // Register progress events
        lib.SetChapterCounterEvent(counter =>
        {
            var parts = counter.Split('/');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0].Trim(), out chaptersDone);
                int.TryParse(parts[1].Trim(), out chaptersTotal);
            }

            var msg = new ExportProgressMessage(
                job.ExportId, "Chapter", $"Chapter {counter}",
                chaptersDone, chaptersTotal, linesDone);
            _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
        });

        lib.SetCumulativeLinesEvent(total =>
        {
            int.TryParse(total.Trim(), out linesDone);

            var msg = new ExportProgressMessage(
                job.ExportId, "Line", $"Lines: {total}",
                chaptersDone, chaptersTotal, linesDone);
            _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
        });

        lib.SetRetryEvent(retryMsg =>
        {
            var msg = new ExportProgressMessage(
                job.ExportId, "Retry", retryMsg,
                chaptersDone, chaptersTotal, linesDone);
            _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
        });

        // Run the export (synchronous lib call, runs on thread pool)
        var (pgn, courseName) = await Task.Run(() => lib.GetCourse(job.ChessableBid), ct);

        if (string.IsNullOrEmpty(pgn))
        {
            await FailExportAsync(job, "Export returned empty PGN");
            return;
        }

        // Save cached course
        var cachedCourse = await db.CachedCourses
            .FirstOrDefaultAsync(c => c.UserId == job.UserId && c.ChessableBid == job.ChessableBid, ct);

        if (cachedCourse is null)
        {
            cachedCourse = new CachedCourse
            {
                UserId = job.UserId,
                ChessableBid = job.ChessableBid,
                CourseName = courseName,
            };
            db.CachedCourses.Add(cachedCourse);
        }

        cachedCourse.CourseName = courseName;
        cachedCourse.RestResponseJson = lib.restResponseCourse is not null
            ? JsonSerializer.Serialize(lib.restResponseCourse)
            : "{}";
        cachedCourse.CachedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Save generated PGN (upsert)
        var existingPgn = await db.GeneratedPgns
            .FirstOrDefaultAsync(p => p.CachedCourseId == cachedCourse.Id
                && p.UserId == job.UserId
                && p.TrainingMode == job.TrainingMode, ct);

        if (existingPgn is null)
        {
            existingPgn = new GeneratedPgn
            {
                CachedCourseId = cachedCourse.Id,
                UserId = job.UserId,
                TrainingMode = job.TrainingMode,
            };
            db.GeneratedPgns.Add(existingPgn);
        }

        existingPgn.PgnContent = pgn;
        existingPgn.GeneratedAt = DateTime.UtcNow;

        // Update export history
        var export = await db.ExportHistories.FindAsync(new object[] { job.ExportId }, ct);
        if (export is not null)
        {
            export.Status = "Completed";
            export.CourseName = courseName;
            export.ChapterCount = chaptersTotal;
            export.LineCount = linesDone;
            export.CompletedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // Notify completion
        await _hub.Clients.Group(userGroup).SendAsync("ExportCompleted", new
        {
            ExportId = job.ExportId,
            CourseName = courseName,
            ChapterCount = chaptersTotal,
            LineCount = linesDone,
            PgnSize = pgn.Length
        }, ct);

        _logger.LogInformation("Export {ExportId} completed: {CourseName}, {Lines} lines",
            job.ExportId, courseName, linesDone);
    }

    private async Task FailExportAsync(ExportJobRequest job, string error)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var export = await db.ExportHistories.FindAsync(job.ExportId);
            if (export is not null)
            {
                export.Status = "Failed";
                export.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            await _hub.Clients.Group($"user-{job.UserId}")
                .SendAsync("ExportFailed", new { ExportId = job.ExportId, Error = error });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update export status for {ExportId}", job.ExportId);
        }
    }
}
