namespace PirateChess.Api.Services;

/// <summary>
/// Erkennt Platzhalter-Geheimnisse aus <c>.env.example</c>: Werte, die mit „change_me“ oder „your_“ beginnen, sind
/// nie ein echter Schlüssel. Das Repo ist öffentlich — wer es kennt, kennt den Platzhalter und könnte damit JWTs
/// signieren, gespeicherte Zugänge entschlüsseln oder sich als rookhub ausgeben.
/// </summary>
public static class SecretPlaceholder
{
    private static readonly string[] Prefixes = ["change_me", "your_"];

    public static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Prefixes.Any(p => value.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Startprüfung für Schlüssel, ohne die der Dienst nicht sicher laufen kann: ein Platzhalter bricht den
    /// Start ab. Die Meldung nennt nur den Schlüsselnamen, nie den Wert.</summary>
    public static void ThrowIfPlaceholder(IConfiguration configuration, string key)
    {
        if (IsPlaceholder(configuration[key]))
            throw new InvalidOperationException(
                $"{key} is still a placeholder from .env.example (change_me…/your_…) — set a real secret");
    }
}
