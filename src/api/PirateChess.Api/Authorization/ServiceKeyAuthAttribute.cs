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

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided)
            || !string.Equals(provided.ToString(), expected, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid service key" });
            return;
        }

        await next();
    }
}
