using Microsoft.AspNetCore.Mvc;
using PirateChess.Api.Data;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly AppDbContext _db;

    public HealthController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        bool canConnect;
        try { canConnect = await _db.Database.CanConnectAsync(); }
        catch { canConnect = false; }
        var body = new { status = canConnect ? "healthy" : "unhealthy", database = canConnect };
        // Bei DB-Ausfall HTTP 503 (nicht 200) — sonst halten Docker-/LB-Healthchecks, die nur den
        // Statuscode prüfen, den Dienst für gesund.
        return canConnect ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
