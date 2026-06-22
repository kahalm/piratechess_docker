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
    private readonly IChessableHttpService _chessableHttp;

    // Oberhalb dieser Größe wird das (ohnehin leserlose) Audit-Bundle CachedCourse.RestResponseJson
    // nicht abgelegt — unkomprimiert würde es sonst MariaDBs max_allowed_packet (Prod/Dev 256 MB)
    // sprengen und diesen ungeschützten SaveChanges (und damit den Export) abbrechen.
    private const int MaxAuditJsonChars = 16 * 1024 * 1024;

    public ExportBackgroundService(
        ExportJobQueue queue,
        IServiceScopeFactory scopeFactory,
        IHubContext<ExportProgressHub> hub,
        ILogger<ExportBackgroundService> logger,
        IChessableHttpService chessableHttp)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
        _chessableHttp = chessableHttp;
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

        // --- Login phase via ChessableHttpService ---
        string bearer;
        string uid;

        if (cred.UseBearer && cred.EncryptedBearer is not null)
        {
            var decrypted = encryption.TryDecrypt(cred.EncryptedBearer);
            if (decrypted is null)
            {
                await FailExportAsync(job, "Stored bearer could not be decrypted — please re-enter credentials");
                return;
            }
            bearer = decrypted;
            var (extractedUid, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
            if (uidError is not null)
            {
                await FailExportAsync(job, $"Invalid bearer token: {uidError}");
                return;
            }
            uid = extractedUid;
        }
        else if (!cred.UseBearer && cred.EncryptedEmail is not null && cred.EncryptedPassword is not null)
        {
            var email = encryption.TryDecrypt(cred.EncryptedEmail);
            var password = encryption.TryDecrypt(cred.EncryptedPassword);
            if (email is null || password is null)
            {
                await FailExportAsync(job, "Stored credentials could not be decrypted — please re-enter them");
                return;
            }
            var (jwt, loginError) = await _chessableHttp.LoginAsync(email, password, ct);
            if (loginError is not null)
            {
                await FailExportAsync(job, $"Login failed: {loginError}");
                return;
            }
            bearer = jwt!;
            var (extractedUid, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
            if (uidError is not null)
            {
                await FailExportAsync(job, $"Invalid token after login: {uidError}");
                return;
            }
            uid = extractedUid;
        }
        else
        {
            await FailExportAsync(job, "Incomplete credentials");
            return;
        }

        var userGroup = $"user-{job.UserId}";
        int chaptersDone = 0;
        int chaptersTotal = 0;
        int linesDone = 0;

        // --- Data fetch phase via ChessableHttpService ---
        var (fetchedData, fetchError) = await _chessableHttp.FetchCourseDataAsync(
            bearer, uid, job.ChessableBid,
            onChapterProgress: counter =>
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
            },
            onCumulativeLines: total =>
            {
                int.TryParse(total.Trim(), out linesDone);

                var msg = new ExportProgressMessage(
                    job.ExportId, "Line", $"Lines: {total}",
                    chaptersDone, chaptersTotal, linesDone);
                _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
            },
            onRetry: retryMsg =>
            {
                var msg = new ExportProgressMessage(
                    job.ExportId, "Retry", retryMsg,
                    chaptersDone, chaptersTotal, linesDone);
                _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
            },
            ct: ct);

        if (fetchError is not null)
        {
            await FailExportAsync(job, fetchError);
            return;
        }

        // --- PGN generation phase via piratechess_lib (useLocalData: true) ---
        var lib = new piratechess_lib.PirateChessLib();
        lib.restResponseCourse = fetchedData;

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

        // Progress events still fire during useLocalData PGN generation
        lib.SetChapterCounterEvent(counter =>
        {
            var parts = counter.Split('/');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0].Trim(), out chaptersDone);
                int.TryParse(parts[1].Trim(), out chaptersTotal);
            }

            var msg = new ExportProgressMessage(
                job.ExportId, "PGN", $"Generating PGN: Chapter {counter}",
                chaptersDone, chaptersTotal, linesDone);
            _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
        });

        lib.SetCumulativeLinesEvent(total =>
        {
            int.TryParse(total.Trim(), out linesDone);

            var msg = new ExportProgressMessage(
                job.ExportId, "PGN", $"Generating PGN: Lines {total}",
                chaptersDone, chaptersTotal, linesDone);
            _hub.Clients.Group(userGroup).SendAsync("ExportProgress", msg).Wait();
        });

        var (pgn, courseName) = await Task.Run(() => lib.GetCourse(job.ChessableBid, useLocalData: true), ct);

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
        // CachedCourse.RestResponseJson ist ein reines Audit/Debug-Bundle (kein Leser; die Einzel-Calls
        // liegen granular in ChessableRawResponse, die cache-relevante Kopie komprimiert in
        // CachedRawCourse). Unkomprimiert sprengt es bei Riesen-Kursen MariaDBs max_allowed_packet und
        // würde — anders als der Roh-Cache — diesen ungeschützten SaveChanges und damit den Export
        // abbrechen. Darum nur ablegen, solange es klein genug ist, sonst leeren Marker speichern.
        var auditJson = fetchedData is not null ? JsonSerializer.Serialize(fetchedData) : "{}";
        if (auditJson.Length > MaxAuditJsonChars)
        {
            _logger.LogInformation(
                "Audit-RestResponseJson für bid {Bid} ({Size} Zeichen) überschreitet {Limit} — wird nicht im CachedCourse abgelegt (Roh-Cache/ChessableRawResponse bleiben unberührt)",
                job.ChessableBid, auditJson.Length, MaxAuditJsonChars);
            auditJson = "{}";
        }
        cachedCourse.RestResponseJson = auditJson;
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
