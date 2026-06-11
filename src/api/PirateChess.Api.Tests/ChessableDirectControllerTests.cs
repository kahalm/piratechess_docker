using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PirateChess.Api.Tests;

public class ChessableDirectControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private const string ServiceKeyHeader = "X-Service-Key";
    private const string ValidServiceKey = "test-service-key";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ChessableDirectControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient ClientWithServiceKey(string? key = ValidServiceKey)
    {
        var client = _factory.CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add(ServiceKeyHeader, key);
        return client;
    }

    [Fact]
    public async Task Test_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Test_WrongServiceKey_Returns401()
    {
        var client = ClientWithServiceKey("nope");

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Test_ValidBearer_ReturnsUidAndCourseCount()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-valid-jwt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DirectTestResp>(JsonOpts);
        Assert.Equal("12345", body!.Uid);
        Assert.Equal(2, body.CourseCount);
    }

    [Fact]
    public async Task Test_InvalidBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "not-a-real-jwt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Test_EmptyBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Courses_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/courses", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Courses_ValidBearer_ReturnsCourseList()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/courses", new { Bearer = "some-valid-jwt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<CourseItem>>(JsonOpts);
        Assert.Equal(2, body!.Count);
        Assert.Contains(body, c => c.Bid == "1001" && c.Name == "Test Course 1");
        Assert.Contains(body, c => c.Bid == "1002" && c.Name == "Test Course 2");
    }

    [Fact]
    public async Task Courses_InvalidBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/courses", new { Bearer = "not-a-real-jwt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private record DirectTestResp(string Uid, int CourseCount);
    private record CourseItem(string Bid, string Name);
}
