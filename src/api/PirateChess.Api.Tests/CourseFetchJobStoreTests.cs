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
    public void Prune_RunningJobOlderThanMaxAge_IsCancelledBeforeRemoval()
    {
        // S1-006: der Fetch-Task hält eine eigene Referenz — ohne Cancel liefe der Abruf unkündbar weiter.
        var store = new CourseFetchJobStore();
        var stuck = store.Create("stuck");
        stuck.CreatedAt = Now - TimeSpan.FromHours(7);

        store.Prune(Now);

        Assert.Null(store.Get("stuck"));
        Assert.True(stuck.CancellationToken.IsCancellationRequested);
        Assert.Equal("cancelled", stuck.Snapshot().Status);
    }

    [Fact]
    public void Prune_Overflow_CancelsRemovedRunningJobs_LeavesTerminalStatus()
    {
        // Wall-Clock-Basis: Create pruned lazy mit DateTime.UtcNow — die Alter bleiben weit unter MaxJobAge.
        var baseUtc = DateTime.UtcNow;
        var store = new CourseFetchJobStore();
        var jobs = new List<CourseFetchJob>();
        for (var i = 0; i < CourseFetchJobStore.MaxJobs; i++) jobs.Add(store.Create("j" + i));
        for (var i = 0; i < jobs.Count; i++) jobs[i].CreatedAt = baseUtc - TimeSpan.FromSeconds(1000 - i);   // j0 ältester
        jobs[5].Complete("PGN", "Done", 1, 1);   // terminal → fliegt zuerst raus, bleibt "completed"

        store.Create("extra1");
        store.Create("extra2");   // Lazy-Prune: 501 > MaxJobs → der terminale j5 geht
        store.Prune(baseUtc);     // 501 > MaxJobs → der älteste laufende j0 geht

        Assert.Equal(CourseFetchJobStore.MaxJobs, store.Count);
        Assert.Null(store.Get("j5"));
        Assert.Equal("completed", jobs[5].Snapshot().Status);
        Assert.False(jobs[5].CancellationToken.IsCancellationRequested);
        Assert.Null(store.Get("j0"));
        Assert.True(jobs[0].CancellationToken.IsCancellationRequested);   // S1-006: abgebrochen, nicht nur vergessen
        Assert.NotNull(store.Get("j1"));
        Assert.False(jobs[1].CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_RunningJob_SetsCancelled_AndSignalsToken()
    {
        var store = new CourseFetchJobStore();
        var job = store.Create("run");

        Assert.True(job.Cancel());

        Assert.Equal("cancelled", job.Snapshot().Status);
        Assert.True(job.CancellationToken.IsCancellationRequested);
        Assert.NotNull(job.TerminalAt);
    }

    [Fact]
    public void Cancel_ThenCompleteOrFail_StaysCancelled()
    {
        // Der Fetch-Worker kann nach dem Abbruch noch fertig werden oder scheitern: der Abbruch bleibt stehen.
        var job = new CourseFetchJobStore().Create("run");
        job.Cancel();

        job.Complete("PGN", "Name", 1, 1);
        job.Fail("boom");

        var s = job.Snapshot();
        Assert.Equal("cancelled", s.Status);
        Assert.Null(s.Pgn);
    }

    [Fact]
    public void Cancel_TerminalJob_ReturnsFalse_KeepsStatus()
    {
        var job = new CourseFetchJobStore().Create("done");
        job.Complete("PGN", "Name", 1, 1);

        Assert.False(job.Cancel());
        Assert.Equal("completed", job.Snapshot().Status);
    }

    [Fact]
    public void Prune_RemovesExpiredCancelledJobs()
    {
        var store = new CourseFetchJobStore();
        var job = store.Create("cancelled");
        job.Cancel();
        job.CreatedAt = Now;
        job.TerminalAt = Now - CourseFetchJobStore.TerminalTtl - TimeSpan.FromMinutes(1);

        Assert.Equal(1, store.Prune(Now));
        Assert.Null(store.Get("cancelled"));
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
