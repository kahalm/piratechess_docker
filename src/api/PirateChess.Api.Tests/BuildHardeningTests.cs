using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PirateChess.Api.Tests;

/// <summary>
/// Wächter für das Laufzeit-Image und die Paketstände (S2-007): Final-Stage nicht als root, fremde Downloads nur mit
/// fester SHA-256, keine gleitenden NuGet-Versionen. Liest die Dateien aus dem Repo (CI baut nur das Image).
/// </summary>
public class BuildHardeningTests
{
    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "api", "Dockerfile")))
                return dir.FullName;
        throw new InvalidOperationException("Repo root (src/api/Dockerfile) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Dockerfile als logische Anweisungen: Fortsetzungszeilen zusammengefügt, Kommentare/Leerzeilen weg.</summary>
    private static List<string> DockerInstructions()
    {
        var result = new List<string>();
        var current = "";
        foreach (var raw in File.ReadAllLines(Path.Combine(RepoRoot(), "src", "api", "Dockerfile")))
        {
            var line = raw.Trim();
            if (current.Length == 0 && (line.Length == 0 || line.StartsWith('#')))
                continue;
            if (line.EndsWith('\\'))
            {
                current += line[..^1] + " ";
                continue;
            }
            result.Add((current + line).Trim());
            current = "";
        }
        return result;
    }

    private static bool Is(string instruction, string keyword) =>
        instruction.StartsWith(keyword + " ", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Dockerfile_FinalStage_RunsAsNonRootUser()
    {
        var instructions = DockerInstructions();
        var lastFrom = instructions.FindLastIndex(i => Is(i, "FROM"));
        var finalStage = instructions.Skip(lastFrom + 1).ToList();
        var entrypoint = finalStage.FindIndex(i => Is(i, "ENTRYPOINT"));
        var user = finalStage.FindLastIndex(i => Is(i, "USER"));

        Assert.True(entrypoint >= 0, "final stage has no ENTRYPOINT");
        Assert.True(user >= 0 && user < entrypoint, "final stage must switch to a non-root USER before ENTRYPOINT");
        var name = finalStage[user][5..].Trim();
        Assert.DoesNotContain(name.Split(':')[0], new[] { "root", "0" });
    }

    [Fact]
    public void Dockerfile_RemoteDownloads_AreVerifiedAgainstAFixedSha256()
    {
        var instructions = DockerInstructions();
        var downloads = instructions
            .Select((text, index) => (text, index))
            .Where(x => Is(x.text, "ADD") && x.text.Contains("://"))
            .ToList();

        Assert.NotEmpty(downloads);
        foreach (var (text, index) in downloads)
        {
            if (Regex.IsMatch(text, @"--checksum=sha256:[0-9a-f]{64}\b"))
                continue;
            var destination = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
            var next = index + 1 < instructions.Count ? instructions[index + 1] : "";
            Assert.True(
                Is(next, "RUN") && Regex.IsMatch(next, @"\b[0-9a-f]{64}\s+" + Regex.Escape(destination) + @"\b")
                    && next.Contains("sha256sum -c"),
                $"download without fixed SHA-256 check: {text}");
        }

        // Gemeinsame Download-Stage: curl-impersonate wird genau einmal geladen, nicht je Stage.
        Assert.Single(downloads, d => d.text.Contains("curl-impersonate"));
    }

    [Fact]
    public void Csproj_PackageVersions_ArePinnedExactly()
    {
        var floating = Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(file => XDocument.Load(file).Descendants("PackageReference")
                .Select(p => (file: Path.GetFileName(file), id: (string?)p.Attribute("Include"), version: (string?)p.Attribute("Version"))))
            .Where(p => p.version is null || p.version.Contains('*') || p.version.StartsWith('[') || p.version.StartsWith('('))
            .Select(p => $"{p.file}: {p.id} {p.version}")
            .ToList();

        Assert.Empty(floating);
    }
}
