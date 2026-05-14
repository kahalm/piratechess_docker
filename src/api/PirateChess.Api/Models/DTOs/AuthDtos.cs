namespace PirateChess.Api.Models.DTOs;

public record LoginRequest(string Username, string Password);
public record RegisterRequest(string Username, string Email, string Password);
public record AuthResponse(string Token, string Username);
