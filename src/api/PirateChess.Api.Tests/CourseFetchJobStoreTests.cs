using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class CourseFetchJobStoreTests
{
    private static readonly DateTime Now = new(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Prune_RemovesExpiredTerminalJobs_KeepsFresh()
    {
        var store = new CourseFetchJobStore();
        // Beide ZUERST anlegen (jedes Create pruned lazy) — erst DANACH altern, damit das Altern
        // nicht von einem zwischenzeitlichen Create-Prune verfälscht wird.
        var old = store.Create("old");
        var fresh = store.Create("fresh");
        old.Complete("PGN", "Old", 1, 1);
        fresh.Complete("PGN", "Fresh", 1, 1);

        old.CreatedAt = Now;
        old.TerminalAt = Now - CourseFetchJobStore.TerminalTtl - TimeSpan.FromMinutes(1);
        fresh.CreatedAt = Now;
        fresh.TerminalAt = Now;

        var removed = store.Prune(Now);

        Assert.Equal(1, removed);
        Assert.Null(store.Get("old"));
        Assert.NotNull(store.Get("fresh"));
    }

    [Fact]
    public void Prune_RemovesAnyJobOlderThanMaxAge_EvenRunning()
    {
        var store = new CourseFetchJobStore();
        var stuck = store.Create("stuck");   // bleibt "running"
        var fresh = store.Create("fresh");
        stuck.CreatedAt = Now - CourseFetchJobStore.MaxJobAge - TimeSpan.FromMinutes(1);
        fresh.CreatedAt = Now;

        var removed = store.Prune(Now);

        Assert.Equal(1, removed);
        Assert.Null(store.Get("stuck"));
        Assert.NotNull(store.Get("fresh"));
    }

    [Fact]
    public void Prune_EnforcesMaxJobsCap()
    {
        var store = new CourseFetchJobStore();
        // Mehr als MaxJobs Jobs anlegen → die Mengen-Obergrenze muss greifen (Create pruned bereits
        // lazy). Assertion bewusst „≤" (unabhängig von der echten Uhr / Alters-TTL).
        for (var i = 0; i < CourseFetchJobStore.MaxJobs + 50; i++)
            store.Create($"job-{i}");

        store.Prune(DateTime.UtcNow);

        Assert.True(store.Count <= CourseFetchJobStore.MaxJobs, $"Count {store.Count} > MaxJobs {CourseFetchJobStore.MaxJobs}");
    }
}
