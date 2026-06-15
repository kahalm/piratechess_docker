using System.IO.Compression;
using System.Text;

namespace PirateChess.Api.Services;

/// <summary>
/// gzip + Base64 für Roh-JSON-Strings. Geteilt von den Roh-Caches und dem Audit-Log
/// (<c>ChessableRawResponses</c>), damit große, gut komprimierbare JSON-Bodies klein in der DB
/// liegen (und das alte 16-MB-max_allowed_packet-Problem gar nicht erst entsteht).
/// </summary>
public static class GzipText
{
    /// <summary>gzip-komprimiert und Base64-kodiert (schrumpft JSON typ. um Faktor ~3–10).</summary>
    public static string Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Umkehrung von <see cref="Compress"/>.</summary>
    public static string Decompress(string base64)
    {
        var data = Convert.FromBase64String(base64);
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
