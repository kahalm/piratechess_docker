using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Services;

/// <summary>
/// Persistenter, zeilen-(oid-)basierter Resume-Cache der rohen getGame-Antwort einer Kurs-Linie.
/// Chessable-Linien-IDs (oid) sind global eindeutig und der Inhalt ist user-/kursunabhängig →
/// eine einmal erfolgreich geholte Linie muss bei einem (Neu-)Start NICHT erneut bei Chessable
/// abgefragt werden. Bricht ein Kursabruf in der Mitte ab, holt der Neustart nur die fehlenden
/// Linien. Überlebt Neustarts (DB). Cache-Fehler sind nie fatal (dann wird eben neu geholt).
///
/// Es werden NUR erfolgreiche Antworten gecacht (nie leer / "{}") — analog zum
/// <see cref="RawCourseCache"/>-Härtungsprinzip: ein leerer Roh-Content würde sonst jeden Replay
/// vergiften. Per-Linie ist das unkritisch, weil wir hier ausschließlich Erfolge ablegen.
///
/// Roh-Content kann groß sein (einzelne Linien &gt;500 KB) → gzip+Base64, wie der Kurs-Cache.
/// </summary>
public class RawLineCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RawLineCache> _logger;

    public RawLineCache(IServiceScopeFactory scopeFactory, ILogger<RawLineCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Eine Linie ist cache-würdig, wenn ihr Roh-Content nicht leer und nicht <c>{}</c> ist.</summary>
    public static bool IsComplete(string? content)
        => !string.IsNullOrWhiteSpace(content) && content != "{}";

    /// <summary>Liefert den gecachten Roh-Content der Linie (oid) oder null, wenn nicht vorhanden.</summary>
    public async Task<string?> GetAsync(int oid, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CachedRawLines.AsNoTracking().FirstOrDefaultAsync(c => c.Oid == oid, ct);
            if (row is null || string.IsNullOrEmpty(row.LineJsonContent)) return null;
            return Decompress(row.LineJsonContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawLineCache.Get fehlgeschlagen für oid {Oid}", oid);
            return null;
        }
    }

    /// <summary>Legt eine erfolgreiche Linie ab (Upsert). Leere/<c>{}</c>-Antworten werden ignoriert.</summary>
    public async Task SetAsync(int oid, string content, CancellationToken ct = default)
    {
        if (!IsComplete(content))
            return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var compressed = Compress(content);
            var row = await db.CachedRawLines.FirstOrDefaultAsync(c => c.Oid == oid, ct);
            if (row is null)
                db.CachedRawLines.Add(new CachedRawLine { Oid = oid, LineJsonContent = compressed, CachedAt = DateTime.UtcNow });
            else
            {
                row.LineJsonContent = compressed;
                row.CachedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RawLineCache.Set fehlgeschlagen für oid {Oid}", oid);
        }
    }

    /// <summary>gzip + Base64 — schrumpft das Linien-JSON deutlich.</summary>
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
