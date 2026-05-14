using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PirateChess.Api.Tests;

public class ChessableControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ChessableControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetCredentials_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/chessable/credentials");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCredentials_NoCredentialsSaved_ReturnsEmpty()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_empty_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.GetAsync("/api/chessable/credentials");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.False(body!.HasCredentials);
        Assert.Equal(0, body.Id);
    }

    [Fact]
    public async Task SaveCredentials_Bearer_SavesAndReturns()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_save_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = true,
            Bearer = "some-jwt-token"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.True(body!.HasCredentials);
        Assert.True(body.UseBearer);
        Assert.True(body.Id > 0);
    }

    [Fact]
    public async Task SaveCredentials_EmailPassword_SavesAndReturns()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_email_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = false,
            Email = "user@chessable.com",
            Password = "chess123"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.True(body!.HasCredentials);
        Assert.False(body.UseBearer);
    }

    [Fact]
    public async Task SaveCredentials_UpdateExisting_Overwrites()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_update_" + Guid.NewGuid().ToString("N")[..6]);

        await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = true,
            Bearer = "token-1"
        });

        var response = await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = false,
            Email = "new@chessable.com",
            Password = "newpass"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.False(body!.UseBearer);
    }

    [Fact]
    public async Task DeleteCredentials_ExistingCredentials_ReturnsNoContent()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_del_" + Guid.NewGuid().ToString("N")[..6]);

        await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = true,
            Bearer = "to-delete"
        });

        var deleteResponse = await client.DeleteAsync("/api/chessable/credentials");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync("/api/chessable/credentials");
        var body = await getResponse.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.False(body!.HasCredentials);
    }

    [Fact]
    public async Task DeleteCredentials_NoneExist_ReturnsNoContent()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_delnone_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.DeleteAsync("/api/chessable/credentials");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TestCredentials_NoCredentials_ReturnsBadRequest()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_testnone_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.PostAsJsonAsync("/api/chessable/test", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestCredentials_LoginReturnsEmptyJson_ShowsInvalidCredentials()
    {
        // Regression: Chessable API returns "{}" for failed login,
        // should show "Invalid credentials" not "Login failed: {}"
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_emptyjson_" + Guid.NewGuid().ToString("N")[..6]);

        // Save fake bearer that will cause LoginWithBearer to throw/fail
        await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = true,
            Bearer = "not-a-real-jwt"
        });

        var response = await client.PostAsJsonAsync("/api/chessable/test", new { });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("{}", body);
    }

    [Fact]
    public async Task SaveCredentials_Bearer_ReturnsMaskedBearer()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_mask_b_" + Guid.NewGuid().ToString("N")[..6]);

        await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = true,
            Bearer = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test"
        });

        var response = await client.GetAsync("/api/chessable/credentials");
        var body = await response.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.NotNull(body!.MaskedBearer);
        Assert.Contains("*", body.MaskedBearer);
        Assert.StartsWith("eyJh", body.MaskedBearer);
    }

    [Fact]
    public async Task SaveCredentials_EmailPassword_ReturnsMaskedValues()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_mask_e_" + Guid.NewGuid().ToString("N")[..6]);

        await client.PostAsJsonAsync("/api/chessable/credentials", new
        {
            UseBearer = false,
            Email = "player@chessable.com",
            Password = "mysecret123"
        });

        var response = await client.GetAsync("/api/chessable/credentials");
        var body = await response.Content.ReadFromJsonAsync<CredentialResp>(JsonOpts);
        Assert.NotNull(body!.MaskedEmail);
        Assert.Contains("*", body.MaskedEmail);
        Assert.Contains("@chessable.com", body.MaskedEmail);
        Assert.Equal("********", body.MaskedPassword);
    }

    [Fact]
    public async Task GetCourses_NoCredentials_ReturnsBadRequest()
    {
        var (client, _) = await TestHelper.CreateAuthenticatedClientAsync(_factory,
            "cred_courses_" + Guid.NewGuid().ToString("N")[..6]);

        var response = await client.GetAsync("/api/chessable/courses");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private record CredentialResp(int Id, bool UseBearer, bool HasCredentials, string? MaskedBearer, string? MaskedEmail, string? MaskedPassword);
}
