using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VpnController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IVpnRotationService _vpn;

    public VpnController(IHttpClientFactory httpClientFactory, IVpnRotationService vpn)
    {
        _httpClientFactory = httpClientFactory;
        _vpn = vpn;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        // Bevorzugt die echte Exit-IP über den gluetun-Control-Server. Fällt nur
        // dann auf den (durch den Proxy gehenden) Mullvad-Check zurück, wenn kein
        // Control-Server konfiguriert ist.
        var controlIp = await _vpn.GetPublicIpAsync(ct);
        if (controlIp is not null)
            return Ok(new { vpn = true, ip = controlIp });

        try
        {
            var client = _httpClientFactory.CreateClient(ChessableHttpClientFactory.ClientName);
            var response = await client.GetStringAsync("https://am.i.mullvad.net/ip", ct);
            return Ok(new { vpn = true, ip = response.Trim() });
        }
        catch (Exception ex)
        {
            return Ok(new { vpn = false, error = ex.Message });
        }
    }

    /// <summary>Erzwingt eine sofortige Rotation der gluetun-Exit-IP (manueller Trigger / Test).</summary>
    [Authorize]
    [HttpPost("rotate")]
    public async Task<IActionResult> Rotate(CancellationToken ct)
    {
        var ip = await _vpn.RotateNowAsync(ct);
        return Ok(new { rotated = ip is not null, ip });
    }
}
