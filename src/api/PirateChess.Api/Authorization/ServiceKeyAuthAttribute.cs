using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PirateChess.Api.Authorization;

/// <summary>
/// Header-based service-to-service authentication. Compares the request header
/// <c>X-Service-Key</c> against the configured <c>Service:ApiKey</c>. Used by
/// the stateless <c>/api/chessable/direct/*</c> endpoints that rookhub calls.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class ServiceKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    private const string HeaderName = "X-Service-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = config["Service:ApiKey"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            context.Result = new ObjectResult(new { message = "Service authentication is not configured" })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        // Genau EIN Header-Wert erwartet (mehrere → verdächtig/ungültig), danach zeitkonstanter
        // Vergleich, damit die Antwortzeit den Key nicht zeichenweise verrät (Timing-Angriff).
        var header = context.HttpContext.Request.Headers[HeaderName];
        if (header.Count != 1 || !FixedTimeEquals(header.ToString(), expected))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid service key" });
            return;
        }

        await next();
    }

    /// <summary>Zeitkonstanter String-Vergleich (verhindert Längen-/Inhalts-Leak über Timing).</summary>
    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
