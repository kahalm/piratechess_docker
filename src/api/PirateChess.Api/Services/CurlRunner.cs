using System.Diagnostics;

namespace PirateChess.Api.Services;

/// <summary>Ausgang eines curl-Laufs: Exit-Code, stdout (Antwort-Body), stderr (Fehlertext).</summary>
public sealed record CurlResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Startet curl-impersonate als Prozess. Naht zwischen der Abruf-Logik (<see cref="ChessableHttpService"/>:
/// Retry, Klassifikation, Audit) und dem Prozess — Tests setzen einen Fake-Runner ein und prüfen die Orchestrierung
/// ohne echtes curl.
/// </summary>
public interface ICurlRunner
{
    /// <summary>Führt curl mit <paramref name="args"/> aus (bei <paramref name="proxyUrl"/> davor <c>--proxy</c>),
    /// schreibt <paramref name="stdin"/> (falls nicht null) auf stdin und liefert Exit-Code, stdout und stderr.
    /// Wirft, wenn der Prozess nicht startet oder <paramref name="ct"/> abbricht (curl wird dann beendet).</summary>
    Task<CurlResult> RunAsync(IReadOnlyList<string> args, string? stdin, string? proxyUrl, CancellationToken ct);
}

public sealed class CurlRunner(ILogger<CurlRunner> logger) : ICurlRunner
{
    /// <summary>Pfad der curl-Binary: curl-impersonate-chrome direkt (NICHT die Wrapper-Skripte, die eigene
    /// Browser-Header ergänzen und so Duplikate erzeugen). Nur Tests setzen ihn (Fake-curl).</summary>
    internal string CurlPath { get; init; } = "/usr/local/bin/curl-impersonate-chrome";

    public async Task<CurlResult> RunAsync(IReadOnlyList<string> args, string? stdin, string? proxyUrl, CancellationToken ct)
    {
        logger.LogDebug("curl: {Path} (proxy: {Proxy})", CurlPath, proxyUrl ?? "none");

        var psi = new ProcessStartInfo
        {
            FileName = CurlPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList → jedes Token wird einzeln/escaped übergeben (keine Shell, keine Arg-Injektion).
        // curl-impersonate honoriert in unserem Setup keine HTTP(S)_PROXY-Env automatisch
        // → Proxy explizit als --proxy mitgeben, damit die Calls über gluetun/VPN laufen.
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(proxyUrl);
        }
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {CurlPath}");

        // Bei Cancellation (z. B. Shutdown) den curl-Prozess aktiv beenden — Process.Dispose
        // killt ihn NICHT, sonst bliebe er als Waise hängen und WaitForExitAsync würde erst
        // mit seinem Ende zurückkehren.
        using var killReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* Prozess bereits beendet / Race — egal */ }
        });

        if (stdin is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // curl hat stdin nicht gelesen, weil es schon beendet ist (Broken pipe). Den Ausgang
                // sagen Exit-Code und stderr unten; eine IOException hier würde ihn verdecken (z. B.
                // den Proxy-503, auf den der Aufrufer gezielt einen Retry macht).
                try { process.StandardInput.Dispose(); } catch (IOException) { /* Pipe trotzdem schließen */ }
            }
        }

        // BEIDE Pipes gleichzeitig leeren: stdout ist hier riesig (Linien Ø ~210 KB, Kapitel ~500 KB).
        // Würde stdout erst vollständig gelesen, bevor stderr drankommt, blockiert curl beim Schreiben
        // auf eine volle stderr-Pipe (OS-Puffer ~64 KB), während wir auf stdout warten → Deadlock,
        // den nur --max-time auflöst. Daher parallel lesen, dann auf Exit warten.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        await process.WaitForExitAsync(ct);
        return new CurlResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
