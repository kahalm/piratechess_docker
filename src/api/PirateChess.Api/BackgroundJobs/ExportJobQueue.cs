using System.Threading.Channels;

namespace PirateChess.Api.BackgroundJobs;

public record ExportJobRequest(int UserId, int ExportId, string ChessableBid, string CourseName, string TrainingMode);

public class ExportJobQueue
{
    // Bounded statt unbounded: bei einem Job-Sturm (z. B. Admin re-importiert alle Kurse) staut sich
    // sonst beliebig viel Arbeit im Heap, weil genau EIN Consumer (ExportBackgroundService) seriell
    // abarbeitet und einzelne Jobs lange dauern können (Linien-Retries). FullMode.Wait drosselt den
    // Enqueue-Aufrufer sauber, statt unbegrenzt zu puffern.
    private readonly Channel<ExportJobRequest> _channel =
        Channel.CreateBounded<ExportJobRequest>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public ValueTask EnqueueAsync(ExportJobRequest job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<ExportJobRequest> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
