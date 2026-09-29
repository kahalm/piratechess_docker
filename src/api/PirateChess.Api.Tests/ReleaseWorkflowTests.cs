using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PirateChess.Api.Tests;

/// <summary>
/// Wächter für den Release-Pfad (I1-004): nur vX.Y.Z-Tags lösen aus, :latest nur über den Release-Wächter, und der
/// Wächter lässt nur Tags durch, deren Commit auf master liegt. Das Wächter-Skript läuft echt (bash + git) gegen ein
/// Wegwerf-Repo mit master- und Feature-Branch.
/// </summary>
public class ReleaseWorkflowTests
{
    private static readonly string WorkflowPath =
        Path.Combine(BuildHardeningTests.RepoRoot(), ".github", "workflows", "build-push.yml");

    private static string Workflow() => File.ReadAllText(WorkflowPath);

    /// <summary>Der run-Block des Schritts mit der gegebenen id, ohne Einrückung.</summary>
    private static string StepScript(string id)
    {
        var lines = File.ReadAllLines(WorkflowPath);
        var start = Array.FindIndex(lines, l => l.Trim() == $"- id: {id}");
        Assert.True(start >= 0, $"step id {id} not found");
        var run = Array.FindIndex(lines, start, l => l.Trim() == "run: |");
        Assert.True(run > start, $"step {id} has no run block");
        var keyIndent = lines[run].Length - lines[run].TrimStart().Length;
        var body = lines.Skip(run + 1)
            .TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length > keyIndent)
            .ToList();
        var indent = body.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", body.Select(l => l.Length >= indent ? l[indent..] : l.Trim()));
    }

    /// <summary>GitHub-Filtermuster (hier nur [0-9], + und literale Punkte) als Regex.</summary>
    private static Regex FilterPattern(string pattern) =>
        new("^" + pattern.Replace(".", @"\.") + "$");

    [Fact]
    public void TagTrigger_MatchesOnlySemverTags()
    {
        var match = Regex.Match(Workflow(), @"tags:\s*\['([^']+)'\]");
        Assert.True(match.Success, "no tag trigger found");
        var filter = FilterPattern(match.Groups[1].Value);

        Assert.Matches(filter, "v1.0.42");
        Assert.Matches(filter, "v10.20.30");
        foreach (var tag in new[] { "vorher-umbau", "v-test", "v1.2", "v1.2.3-rc1", "v2.20.1-1", "1.2.3" })
            Assert.DoesNotMatch(filter, tag);
    }

    [Fact]
    public void Latest_IsOnlySetByTheReleaseGuard()
    {
        var workflow = Workflow();
        Assert.Contains("type=raw,value=latest,enable=${{ steps.release.outputs.release == 'true' }}", workflow);
        Assert.Matches(@"flavor:\s*\|\s*\n\s*latest=false", workflow);
        Assert.DoesNotContain("startsWith(github.ref, 'refs/tags/", workflow);
    }

    [Fact]
    public void ReleaseGuard_RunsAfterFullCheckout_AndBeforeLoginAndBuild()
    {
        var workflow = Workflow();
        var checkout = workflow.IndexOf("actions/checkout@", StringComparison.Ordinal);
        var guard = workflow.IndexOf("- id: release", StringComparison.Ordinal);
        var login = workflow.IndexOf("docker/login-action@", StringComparison.Ordinal);
        var build = workflow.IndexOf("docker/build-push-action@", StringComparison.Ordinal);

        Assert.True(checkout >= 0 && checkout < guard && guard < login && login < build);
        Assert.Matches(@"actions/checkout@v\d+\s*\n\s*with:\s*\n\s*fetch-depth: 0", workflow);
        Assert.Contains("if: github.ref_type == 'tag'", workflow);
    }

    [Fact]
    public void ReleaseGuard_AcceptsOnlySemverTagsOnMaster()
    {
        var script = StepScript("release");
        var root = Directory.CreateTempSubdirectory("w2b12-release-").FullName;
        try
        {
            var origin = Path.Combine(root, "origin.git");
            var work = Path.Combine(root, "work");
            Git(root, "init", "-q", "--bare", "-b", "master", origin);
            Git(root, "init", "-q", "-b", "master", work);
            Git(work, "commit", "-q", "--allow-empty", "-m", "base");
            Git(work, "remote", "add", "origin", origin);
            Git(work, "push", "-q", "origin", "master");
            Git(work, "checkout", "-q", "-b", "feature");
            Git(work, "commit", "-q", "--allow-empty", "-m", "unmerged");
            Git(work, "push", "-q", "origin", "feature");

            Git(work, "checkout", "-q", "--detach", "master");
            var (ok, output) = Guard(work, script, "v1.2.3");
            Assert.True(ok);
            Assert.Contains("release=true", output);

            Assert.False(Guard(work, script, "vorher-umbau").Ok);
            Assert.False(Guard(work, script, "v1.2.3-rc1").Ok);

            Git(work, "checkout", "-q", "--detach", "feature");
            var (onFeature, _) = Guard(work, script, "v9.9.9");
            Assert.False(onFeature);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (bool Ok, string Output) Guard(string work, string script, string tag)
    {
        var output = Path.Combine(work, "..", $"github-output-{Guid.NewGuid():N}");
        File.WriteAllText(output, "");
        var (exit, _) = Run(work, "bash", ["-c", script], new() { ["TAG"] = tag, ["GITHUB_OUTPUT"] = output });
        return (exit == 0, File.ReadAllText(output));
    }

    private static void Git(string cwd, params string[] args)
    {
        var (exit, log) = Run(cwd, "git", args, new());
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed: {log}");
    }

    private static (int Exit, string Log) Run(string cwd, string file, string[] args, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        // Eigene, leere Git-Konfiguration: Nutzer-/System-Einstellungen (Hooks, Signieren, Default-Branch) stören nicht.
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";
        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
