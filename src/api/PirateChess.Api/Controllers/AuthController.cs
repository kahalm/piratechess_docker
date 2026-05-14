using Microsoft.AspNetCore.Mvc;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth)
    {
        _auth = auth;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
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
