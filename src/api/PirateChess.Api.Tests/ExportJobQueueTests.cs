using PirateChess.Api.BackgroundJobs;

namespace PirateChess.Api.Tests;

public class ExportJobQueueTests
{
    private static ExportJobRequest Job(int id) => new(UserId: 1, ExportId: id, ChessableBid: "b", CourseName: "c", TrainingMode: "m");

    [Fact]
    public async Task DequeueAllAsync_YieldsEnqueuedJobsInFifoOrder()
    {
        var q = new ExportJobQueue();
        await q.EnqueueAsync(Job(10));
        await q.EnqueueAsync(Job(20));
        await q.EnqueueAsync(Job(30));

        var got = new List<int>();
        using var cts = new CancellationTokenSource();
        await foreach (var job in q.DequeueAllAsync(cts.Token))
        {
            got.Add(job.ExportId);
            if (got.Count == 3) break;   // Stream ist unbounded → nach den erwarteten Items abbrechen
        }

        Assert.Equal(new[] { 10, 20, 30 }, got);
    }

    [Fact]
    public async Task DequeueAllAsync_HonorsCancellationWhenEmpty()
    {
        var q = new ExportJobQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in q.DequeueAllAsync(cts.Token)) { }
        });
    }
}
