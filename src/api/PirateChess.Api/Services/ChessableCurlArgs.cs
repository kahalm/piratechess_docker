namespace PirateChess.Api.Services;

/// <summary>
/// curl-Argumente für Chessable-Abrufe: TLS-Fingerprint (curl_chrome116) und Header-Reihenfolge exakt wie ein
/// Browser — jede Änderung verändert den Fingerprint (Golden-Test in den Tests). Aus <see cref="ChessableHttpService"/>
/// herausgelöst (Zerlegung Z46).
/// </summary>
public static class ChessableCurlArgs
{
    // curl bricht die (Proxy-)CONNECT-Phase nach so vielen Sekunden ab, statt im Default bis ~300 s
    // zu hängen. Bei soft-geblockter/hängender VPN-IP fror ein Request sonst den Import minutenlang
    // ein; jetzt schlägt er nach 30 s fehl → der Line-Retry (30 s Backoff) greift schnell.
    private const int CurlConnectTimeoutSec = 30;

    // TLS flags from curl_chrome116 wrapper — needed for Chrome TLS fingerprint
    private const string TlsFlags =
        "--ciphers TLS_AES_128_GCM_SHA256,TLS_AES_256_GCM_SHA384,TLS_CHACHA20_POLY1305_SHA256,"
        + "ECDHE-ECDSA-AES128-GCM-SHA256,ECDHE-RSA-AES128-GCM-SHA256,"
        + "ECDHE-ECDSA-AES256-GCM-SHA384,ECDHE-RSA-AES256-GCM-SHA384,"
        + "ECDHE-ECDSA-CHACHA20-POLY1305,ECDHE-RSA-CHACHA20-POLY1305,"
        + "ECDHE-RSA-AES128-SHA,ECDHE-RSA-AES256-SHA,"
        + "AES128-GCM-SHA256,AES256-GCM-SHA384,AES128-SHA,AES256-SHA"
        + " --http2 --http2-no-server-push --compressed"
        + " --tlsv1.2 --alps --tls-permute-extensions"
        + " --cert-compression brotli";

    // Dieselben TLS-Flags als einzelne argv-Tokens (kein Token enthält Leerzeichen → split by space).
    // Werden über ProcessStartInfo.ArgumentList übergeben, nicht als ein String → keine Arg-Injektion.
    private static readonly string[] TlsArgs = TlsFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void AddHeader(List<string> args, string header)
    {
        args.Add("-H");
        args.Add(header);
    }

    /// <summary>
    /// Baut die curl-Argumente als EINZELNE argv-Tokens (für <see cref="ProcessStartInfo.ArgumentList"/>).
    /// Jeder Wert — inkl. URL (mit user-naher bid) — ist ein eigenständiges Argument; .NET
    /// escaped sie pro-Token. Dadurch kann KEIN Eingabewert curl-Flags injizieren (vorher floss die URL
    /// als <c>"{url}"</c> in einen Args-String → ein <c>"</c> in der bid konnte z.B. <c>-o</c>/<c>--config</c>
    /// einschleusen und Dateien lesen/schreiben).
    /// Der Bearer steht NICHT in den Argumenten (sonst für jedes lokale Konto in <c>/proc/&lt;pid&gt;/cmdline</c>
    /// lesbar): an seiner Stelle liest <c>-H @-</c> den Authorization-Header aus stdin
    /// (<see cref="BuildGetHeaderStdin"/>). curl fügt ihn genau dort ein → Header-Reihenfolge unverändert.
    /// </summary>
    public static List<string> BuildGetArgs(string url, int maxTimeSec = 20)
    {
        var args = new List<string> { "-s", "-S", "--connect-timeout", CurlConnectTimeoutSec.ToString(),
            "--max-time", maxTimeSec.ToString() };
        args.AddRange(TlsArgs);
        AddHeader(args, "user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:138.0) Gecko/20100101 Firefox/138.0");
        AddHeader(args, "accept: application/json, text/plain, */*");
        AddHeader(args, "accept-language: en");
        AddHeader(args, "platform: Web");
        AddHeader(args, "x-os-name: Firefox");
        AddHeader(args, "x-os-version: 138");
        AddHeader(args, "x-device-model: Windows");
        AddHeader(args, "@-"); // authorization: Bearer … aus stdin (BuildGetHeaderStdin)
        AddHeader(args, "alt-used: www.chessable.com");
        AddHeader(args, "connection: keep-alive");
        AddHeader(args, "sec-fetch-dest: empty");
        AddHeader(args, "sec-fetch-mode: cors");
        AddHeader(args, "sec-fetch-site: same-origin");
        AddHeader(args, "priority: u=0");
        AddHeader(args, "te: trailers");
        AddHeader(args, "pragma: no-cache");
        AddHeader(args, "cache-control: no-cache");
        args.Add(url);
        return args;
    }

    /// <summary>stdin-Text zu <see cref="BuildGetArgs"/> (<c>-H @-</c>): genau eine Header-Zeile mit dem Bearer.
    /// CR/LF werden entfernt, weil curl jede Zeile der Header-Datei als eigenen Header liest.</summary>
    public static string BuildGetHeaderStdin(string bearer)
        => $"authorization: Bearer {bearer.Replace("\r", "").Replace("\n", "")}\n";

    /// <summary>Wie <see cref="BuildGetArgs"/>, für POST (Body via stdin, <c>-d @-</c>).</summary>
    public static List<string> BuildPostArgs(string url)
    {
        var args = new List<string> { "-s", "-S", "--connect-timeout", CurlConnectTimeoutSec.ToString() };
        args.AddRange(TlsArgs);
        args.Add("-X"); args.Add("POST");
        AddHeader(args, "user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:137.0) Gecko/20100101 Firefox/137.0");
        AddHeader(args, "accept: application/json, text/plain, */*");
        AddHeader(args, "accept-language: en");
        AddHeader(args, "referer: https://www.chessable.com/login/");
        AddHeader(args, "content-type: application/json;charset=utf-8");
        AddHeader(args, "platform: Web");
        AddHeader(args, "x-os-name: Firefox");
        AddHeader(args, "x-os-version: 137");
        AddHeader(args, "x-device-model: Windows");
        AddHeader(args, "origin: https://www.chessable.com");
        AddHeader(args, "alt-used: www.chessable.com");
        AddHeader(args, "connection: keep-alive");
        AddHeader(args, "sec-fetch-dest: empty");
        AddHeader(args, "sec-fetch-mode: cors");
        AddHeader(args, "sec-fetch-site: same-origin");
        AddHeader(args, "dnt: 1");
        AddHeader(args, "sec-gpc: 1");
        AddHeader(args, "priority: u=0");
        args.Add("-d"); args.Add("@-"); // read body from stdin
        args.Add(url);
        return args;
    }
}
