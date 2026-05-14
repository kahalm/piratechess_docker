using System.Threading.Channels;

namespace PirateChess.Api.BackgroundJobs;

public record ExportJobRequest(int UserId, int ExportId, string ChessableBid, string CourseName, string TrainingMode);

public class ExportJobQueue
{
    private readonly Channel<ExportJobRequest> _channel =
        Channel.CreateUnbounded<ExportJobRequest>();

    public ValueTask EnqueueAsync(ExportJobRequest job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<ExportJobRequest> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
