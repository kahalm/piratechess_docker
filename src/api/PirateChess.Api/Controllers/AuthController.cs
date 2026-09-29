using Microsoft.AspNetCore.Mvc;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly IConfiguration _config;

    public AuthController(AuthService auth, IConfiguration config)
    {
        _auth = auth;
        _config = config;
    }

    /// <summary>Überbleibsel des entfernten piratechess-Frontends: rookhub nutzt nur /api/chessable/direct/*.
    /// Offen stellte die Registrierung jedem im LAN/VPN ein JWT aus → Chessable-Login-Proxy über den geteilten
    /// VPN-Tunnel. Darum standardmäßig aus (404); nur mit <c>Auth:RegistrationEnabled=true</c> wieder an.</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        if (!_config.GetValue("Auth:RegistrationEnabled", false))
            return NotFound();

        var result = await _auth.RegisterAsync(request);
        if (result is null)
            return Conflict(new { message = "Username or email already exists" });

        return Ok(result);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await _auth.LoginAsync(request);
        if (result is null)
            return Unauthorized(new { message = "Invalid credentials" });

        return Ok(result);
    }
}
