using System.Security.Claims;

namespace PirateChess.Api.Authorization;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Liest die numerische User-Id aus dem <see cref="ClaimTypes.NameIdentifier"/>-Claim.
    /// Wirft <see cref="UnauthorizedAccessException"/> (→ 401 in der Request-Middleware) statt einer
    /// FormatException/NullReferenceException (→ 500), wenn der Claim fehlt oder nicht numerisch ist.
    /// </summary>
    public static int GetRequiredUserId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(raw, out var id))
            throw new UnauthorizedAccessException("Missing or invalid user id claim.");
        return id;
    }
}
