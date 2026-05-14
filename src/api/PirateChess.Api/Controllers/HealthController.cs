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
        var canConnect = await _db.Database.CanConnectAsync();
        return Ok(new { status = canConnect ? "healthy" : "unhealthy", database = canConnect });
    }
}
