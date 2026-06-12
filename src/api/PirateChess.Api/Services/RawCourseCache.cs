using System.IO.Compression;
using System.Text;
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
///
/// Die Rohdaten können sehr groß sein (Kurse mit 36+ MB JSON) → werden gzip-komprimiert (Base64)
/// gespeichert, damit sie unter MariaDBs max_allowed_packet bleiben.
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
            var json = Decompress(row.RestResponseJson);
            return JsonSerializer.Deserialize<RestResponseCourse>(json, JsonOpts);
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
            var compressed = Compress(JsonSerializer.Serialize(course));
            var row = await db.CachedRawCourses.FirstOrDefaultAsync(c => c.Bid == bid, ct);
            if (row is null)
                db.CachedRawCourses.Add(new CachedRawCourse { Bid = bid, RestResponseJson = compressed, CachedAt = DateTime.UtcNow });
            else
            {
                row.RestResponseJson = compressed;
                row.CachedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawCourseCache.Set fehlgeschlagen für bid {Bid}", bid);
        }
    }

    /// <summary>gzip + Base64 — schrumpft das große Kurs-JSON deutlich (gut komprimierbar).</summary>
    private static string Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    private static string Decompress(string base64)
    {
        var data = Convert.FromBase64String(base64);
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
