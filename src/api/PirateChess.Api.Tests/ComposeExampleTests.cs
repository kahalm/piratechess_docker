namespace PirateChess.Api.Tests;

/// <summary>
/// Wächter für die Repo-Beispiele (S2-013). Betrieben wird piratechess in /opt/stacks/rookhub-schach(-dev); die
/// Repo-Dateien sind nur ein Beispiel für die lokale Entwicklung. Wer ihnen folgt, darf weder DB noch API ins Netz
/// stellen, und gluetun-auth.toml und .env.example müssen zusammenpassen. Vorher verlangte die Vorlage
/// apikey="CHANGE_ME", während GLUETUN_APIKEY leer blieb: jede Rotation endete mit 401, oder man übernahm den
/// öffentlich bekannten Platzhalter als Schlüssel. Gelesen wird zeilenweise, die Dateien sind flach.
/// </summary>
public class ComposeExampleTests
{
    private static string RepoFile(string name) => Path.Combine(BuildHardeningTests.RepoRoot(), name);

    /// <summary>Aktive (nicht auskommentierte) Zeilen, getrimmt.</summary>
    private static List<string> ActiveLines(string name) =>
        File.ReadAllLines(RepoFile(name)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    [Fact]
    public void NoSeparateProdCompose_InTheRepo()
        => Assert.False(File.Exists(RepoFile("docker-compose.prod.yml")),
            "Prod läuft in /opt/stacks/rookhub-schach; eine zweite Prod-Compose im Repo driftet (API auf 0.0.0.0:5000, ein Tunnel)");

    [Fact]
    public void DevCompose_PublishesPortsOnlyOnLoopback_AndPointsToTheStacks()
    {
        var lines = File.ReadAllLines(RepoFile("docker-compose.yml"));
        var ports = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "ports:") continue;
            ports.AddRange(lines.Skip(i + 1)
                .TakeWhile(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                .Select(l => l.Trim()[2..].Trim().Trim('"', '\'')));
        }

        Assert.Equal(2, ports.Count);   // db + api
        Assert.All(ports, p => Assert.StartsWith("127.0.0.1:", p));
        Assert.Contains("/opt/stacks/rookhub-schach", File.ReadAllText(RepoFile("docker-compose.yml")));
    }

    [Fact]
    public void GluetunAuthTemplate_AndEnvExample_ShareTheSameStartingState()
    {
        var toml = ActiveLines("gluetun-auth.toml");
        var gluetunKey = ActiveLines(".env.example").Single(l => l.StartsWith("GLUETUN_APIKEY=", StringComparison.Ordinal));

        // Ausgangszustand wie Prod/Dev: kein Key auf beiden Seiten → Rotation funktioniert ohne Nacharbeit.
        Assert.Contains("auth = \"none\"", toml);
        Assert.DoesNotContain(toml, l => l.StartsWith("apikey", StringComparison.Ordinal));
        Assert.Equal("GLUETUN_APIKEY=", gluetunKey);
    }
}
