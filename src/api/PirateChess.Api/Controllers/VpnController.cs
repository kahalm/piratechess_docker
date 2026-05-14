using Microsoft.AspNetCore.Mvc;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VpnController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public VpnController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        try
        {
            var client = _httpClientFactory.CreateClient(ChessableHttpClientFactory.ClientName);
            var response = await client.GetStringAsync("https://am.i.mullvad.net/ip");
            return Ok(new { vpn = true, ip = response.Trim() });
        }
        catch (Exception ex)
        {
            return Ok(new { vpn = false, error = ex.Message });
        }
    }
}
