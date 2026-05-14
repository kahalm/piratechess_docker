using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PirateChess.Api.Tests;

public class ExportControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ExportControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task StartExport_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "123",
            CourseName = "Test",
            TrainingMode = "FirstKeyMove"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartExport_InvalidTrainingMode_ReturnsBadRequest()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_invalid_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "123",
            CourseName = "Test",
            TrainingMode = "InvalidMode"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartExport_ValidRequest_ReturnsRunningExport()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_start_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "42",
            CourseName = "Italian Game",
            TrainingMode = "AllKeyMoves"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResp>(JsonOpts);
        Assert.Equal("Running", body!.Status);
        Assert.Equal("42", body.ChessableBid);
        Assert.Equal("Italian Game", body.CourseName);
        Assert.Equal("AllKeyMoves", body.TrainingMode);
        Assert.True(body.Id > 0);
    }

    [Fact]
    public async Task GetExports_Empty_ReturnsEmptyList()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_empty_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.GetAsync("/api/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResp[]>(JsonOpts);
        Assert.Empty(body!);
    }

    [Fact]
    public async Task GetExports_AfterStart_ReturnsExportInList()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_list_" + Guid.NewGuid().ToString("N")[..6]);

        await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "99",
            CourseName = "Sicilian",
            TrainingMode = "None"
        });

        var response = await client.GetAsync("/api/export");
        var body = await response.Content.ReadFromJsonAsync<ExportResp[]>(JsonOpts);
        Assert.Single(body!);
        Assert.Equal("99", body[0].ChessableBid);
    }

    [Fact]
    public async Task GetExportById_ExistingExport_ReturnsExport()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_byid_" + Guid.NewGuid().ToString("N")[..6]);

        var startResp = await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "77",
            CourseName = "French Defense",
            TrainingMode = "FirstKeyMove"
        });
        var started = await startResp.Content.ReadFromJsonAsync<ExportResp>(JsonOpts);

        var response = await client.GetAsync($"/api/export/{started!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExportResp>(JsonOpts);
        Assert.Equal(started.Id, body!.Id);
        Assert.Equal("French Defense", body.CourseName);
    }

    [Fact]
    public async Task GetExportById_NonExisting_ReturnsNotFound()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_notfound_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.GetAsync("/api/export/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPgn_NotCompleted_ReturnsBadRequest()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_pgn_nc_" + Guid.NewGuid().ToString("N")[..6]);

        var startResp = await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "55",
            CourseName = "KID",
            TrainingMode = "AllKeyMoves"
        });
        var started = await startResp.Content.ReadFromJsonAsync<ExportResp>(JsonOpts);

        var response = await client.GetAsync($"/api/export/{started!.Id}/pgn");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DownloadPgn_NonExisting_ReturnsNotFound()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_pgn_nf_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.GetAsync("/api/export/99999/pgn");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetExports_DifferentUser_CantSeeOthersExports()
    {
        var (client1, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_iso1_" + Guid.NewGuid().ToString("N")[..6]);
        var (client2, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "exp_iso2_" + Guid.NewGuid().ToString("N")[..6]);

        await client1.PostAsJsonAsync("/api/export", new
        {
            Bid = "111",
            CourseName = "Secret Course",
            TrainingMode = "None"
        });

        var response = await client2.GetAsync("/api/export");
        var body = await response.Content.ReadFromJsonAsync<ExportResp[]>(JsonOpts);
        Assert.Empty(body!);
    }

    [Theory]
    [InlineData("AllKeyMoves")]
    [InlineData("FirstKeyMove")]
    [InlineData("None")]
    public async Task StartExport_AllValidModes_Accepted(string mode)
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            $"exp_mode_{mode[..3]}_{Guid.NewGuid().ToString("N")[..4]}");

        var response = await client.PostAsJsonAsync("/api/export", new
        {
            Bid = "10",
            CourseName = "Test",
            TrainingMode = mode
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private record ExportResp(
        int Id, string Status, string ChessableBid, string CourseName,
        string TrainingMode, int ChapterCount, int LineCount,
        string StartedAt, string? CompletedAt);
}
