using System.Security.Claims;
using PirateChess.Api.Authorization;

namespace PirateChess.Api.Tests;

public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void GetRequiredUserId_ValidNumericClaim_ReturnsId()
    {
        var user = Principal(new Claim(ClaimTypes.NameIdentifier, "42"));
        Assert.Equal(42, user.GetRequiredUserId());
    }

    [Fact]
    public void GetRequiredUserId_MissingClaim_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Principal().GetRequiredUserId());
    }

    [Fact]
    public void GetRequiredUserId_NonNumericClaim_Throws()
    {
        var user = Principal(new Claim(ClaimTypes.NameIdentifier, "not-a-number"));
        Assert.Throws<UnauthorizedAccessException>(() => user.GetRequiredUserId());
    }
}
