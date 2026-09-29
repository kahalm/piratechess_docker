using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PirateChess.Api.Authorization;

/// <summary>
/// Header-based service-to-service authentication. Compares the request header
/// <c>X-Service-Key</c> against the configured <c>Service:ApiKey</c>. Used by
/// the stateless <c>/api/chessable/direct/*</c> endpoints that rookhub calls.
/// Läuft als Authorization-Filter, also VOR Modellbindung und der ModelState-Prüfung von [ApiController]: ein
/// Aufrufer ohne gültigen Key bekommt 401, bevor sein Body gelesen und deserialisiert wird (course/parse nimmt bis
/// 100 MB an), und erfährt keine Feldnamen aus einer 400-Antwort. Dieselbe Prüfung (<see cref="Check"/>) teilt der
/// Rate-Limiter "direct" in gültige und ungültige Aufrufer (Program.cs).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class ServiceKeyAuthAttribute : Attribute, IAsyncAuthorizationFilter
{
    private const string HeaderName = "X-Service-Key";

    public enum KeyState { Valid, Invalid, NotConfigured }

    /// <summary>
    /// Genau EIN Header-Wert erwartet (mehrere → verdächtig/ungültig), danach zeitkonstanter Vergleich, damit die
    /// Antwortzeit den Key nicht zeichenweise verrät (Timing-Angriff).
    /// </summary>
    public static KeyState Check(HttpContext http)
    {
        var expected = http.RequestServices.GetRequiredService<IConfiguration>()["Service:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected))
            return KeyState.NotConfigured;
        var header = http.Request.Headers[HeaderName];
        return header.Count == 1 && FixedTimeEquals(header.ToString(), expected) ? KeyState.Valid : KeyState.Invalid;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        switch (Check(context.HttpContext))
        {
            case KeyState.NotConfigured:
                context.Result = new ObjectResult(new { message = "Service authentication is not configured" })
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
                break;
            case KeyState.Invalid:
                context.Result = new UnauthorizedObjectResult(new { message = "Invalid service key" });
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>Zeitkonstanter String-Vergleich (verhindert Längen-/Inhalts-Leak über Timing).</summary>
    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
