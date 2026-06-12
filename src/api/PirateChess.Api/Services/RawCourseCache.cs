using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

/// <summary>
/// Persistenter, kurs-(bid-)basierter Cache der rohen Kursstruktur (<see cref="RestResponseCourse"/>).
/// Der Kursinhalt ist für alle Besitzer identisch → ein zweiter User kann denselben Kurs importieren,
/// OHNE dass Chessable erneut abgefragt wird. Überlebt Neustarts (DB). Cache-Fehler sind nie fatal
/// (dann wird eben neu geholt).
/// </summary>
public class RawCourseCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RawCourseCache> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public RawCourseCache(IServiceScopeFactory scopeFactory, ILogger<RawCourseCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<RestResponseCourse?> GetAsync(string bid, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CachedRawCourses.AsNoTracking().FirstOrDefaultAsync(c => c.Bid == bid, ct);
            if (row is null || string.IsNullOrEmpty(row.RestResponseJson)) return null;
            return JsonSerializer.Deserialize<RestResponseCourse>(row.RestResponseJson, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawCourseCache.Get fehlgeschlagen für bid {Bid}", bid);
            return null;
        }
    }

    public async Task SetAsync(string bid, RestResponseCourse course, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var json = JsonSerializer.Serialize(course);
            var row = await db.CachedRawCourses.FirstOrDefaultAsync(c => c.Bid == bid, ct);
            if (row is null)
                db.CachedRawCourses.Add(new CachedRawCourse { Bid = bid, RestResponseJson = json, CachedAt = DateTime.UtcNow });
            else
            {
                row.RestResponseJson = json;
                row.CachedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawCourseCache.Set fehlgeschlagen für bid {Bid}", bid);
        }
    }
}
