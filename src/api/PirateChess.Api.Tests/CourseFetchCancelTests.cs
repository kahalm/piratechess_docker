using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using piratechess_lib;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// S2-008: ein course/start-Job lässt sich abbrechen — per DELETE course/{jobId} und beim Container-Stopp
/// (ApplicationStopping). Der Abbruch muss beim Chessable-Abruf ankommen, sonst zieht piratechess den ganzen
/// Kurs weiter über die VPN-IP.
/// </summary>
public class CourseFetchCancelTests : IClassFixture<TestWebApplicationFactory>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly TestWebApplicationFactory _factory;

    public CourseFetchCancelTests(TestWebApplicationFactory factory) => _factory = factory;

    /// <summary>Kursabruf, der hängt, bis sein Token abbricht, und meldet, ob er den Abbruch gesehen hat.</summary>
    private sealed class HangingFetchService : IChessableHttpService
    {
        private readonly FakeChessableHttpService _inner = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SawCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(string? jwt, string? error)> LoginAsync(string email, string password, CancellationToken ct = default)
            => _inner.LoginAsync(email, password, ct);
        public (string uid, string? error) ExtractUidFromBearer(string jwt) => _inner.ExtractUidFromBearer(jwt);
        public Task<(Dictionary<string, string>? courses, string? error)> GetCoursesAsync(
            string bearer, string uid, CancellationToken ct = default, int? pinnedTunnel = null)
            => _inner.GetCoursesAsync(bearer, uid, ct, pinnedTunnel);
        public Task<(int? totalLines, string? error)> GetCourseLineCountAsync(string bearer, string uid, string bid, CancellationToken ct = default)
            => _inner.GetCourseLineCountAsync(bearer, uid, bid, ct);
        public Task<(bool ok, int bytes, long ms, string? error, string snippet)> DebugFetchLineAsync(
            string bearer, string uid, int oid, CancellationToken ct = default)
            => _inner.DebugFetchLineAsync(bearer, uid, oid, ct);

        public async Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
            string bearer, string uid, string bid,
            Action<string>? onChapterProgress = null, Action<string>? onLineProgress = null,
            Action<string>? onCumulativeLines = null, Action<string>? onRetry = null,
            Action<int>? onTotalLines = null, bool bypassLineCache = false, CancellationToken ct = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                SawCancellation.TrySetResult();
                throw;
            }
            return (null, "unreachable");
        }
    }

    private (WebApplicationFactory<Program> Host, HangingFetchService Fetch) HostWithHangingFetch()
    {
        var fetch = new HangingFetchService();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChessableHttpService>();
            s.AddSingleton<IChessableHttpService>(fetch);
        }));
        return (host, fetch);
    }

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");
        return client;
    }

    private static async Task<string> StartAsync(HttpClient client, string bid)
    {
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "some-valid-jwt", Bid = bid, Mode = "None" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobStartResp>(JsonOpts);
        return body!.JobId;
    }

    [Fact]
    public async Task Delete_RunningJob_CancelsFetch_AndFreesJob()
    {
        var (host, fetch) = HostWithHangingFetch();
        using var _ = host;
        var client = Client(host);
        var jobId = await StartAsync(client, "737001");
        await fetch.Started.Task.WaitAsync(Wait);

        var del = await client.DeleteAsync($"/api/chessable/direct/course/{jobId}");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var body = await del.Content.ReadFromJsonAsync<CancelResp>(JsonOpts);
        Assert.True(body!.Cancelled);
        await fetch.SawCancellation.Task.WaitAsync(Wait);   // der Abbruch kommt beim Chessable-Abruf an
        var poll = await client.GetAsync($"/api/chessable/direct/course/{jobId}");
        Assert.Equal(HttpStatusCode.NotFound, poll.StatusCode);   // Job freigegeben
        // Der Per-bid-Lock des abgebrochenen Abrufs ist wieder frei.
        var rawCache = host.Services.GetRequiredService<RawCourseCache>();
        for (var i = 0; i < 100 && rawCache.ActiveBidLockCount > 0; i++) await Task.Delay(50);
        Assert.Equal(0, rawCache.ActiveBidLockCount);
    }

    [Fact]
    public async Task Delete_UnknownJob_Returns404()
    {
        var client = Client(_factory);
        var response = await client.DeleteAsync("/api/chessable/direct/course/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MissingServiceKey_Returns401()
    {
        var response = await _factory.CreateClient().DeleteAsync("/api/chessable/direct/course/whatever");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApplicationStopping_CancelsRunningFetch()
    {
        var (host, fetch) = HostWithHangingFetch();
        using var _ = host;
        var client = Client(host);
        await StartAsync(client, "737002");
        await fetch.Started.Task.WaitAsync(Wait);

        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await fetch.SawCancellation.Task.WaitAsync(Wait);
    }

    private record JobStartResp(string JobId);
    private record CancelResp(bool Cancelled);
}
