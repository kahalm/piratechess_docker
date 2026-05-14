using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Data;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChessableController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly EncryptionService _encryption;
    private readonly IChessableHttpService _chessableHttp;

    public ChessableController(AppDbContext db, EncryptionService encryption, IChessableHttpService chessableHttp)
    {
        _db = db;
        _encryption = encryption;
        _chessableHttp = chessableHttp;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("credentials")]
    public async Task<IActionResult> GetCredentials()
    {
        var cred = await _db.ChessableCredentials
            .FirstOrDefaultAsync(c => c.UserId == UserId);

        if (cred is null)
            return Ok(new CredentialResponse(0, false, false, null, null, null));

        return Ok(ToCredentialDto(cred));
    }

    [HttpPost("credentials")]
    public async Task<IActionResult> SaveCredentials(SaveCredentialRequest request)
    {
        var cred = await _db.ChessableCredentials
            .FirstOrDefaultAsync(c => c.UserId == UserId);

        if (cred is null)
        {
            cred = new ChessableCredential { UserId = UserId };
            _db.ChessableCredentials.Add(cred);
        }

        cred.UseBearer = request.UseBearer;
        cred.EncryptedBearer = request.Bearer is not null ? _encryption.Encrypt(request.Bearer) : null;
        cred.EncryptedEmail = request.Email is not null ? _encryption.Encrypt(request.Email) : null;
        cred.EncryptedPassword = request.Password is not null ? _encryption.Encrypt(request.Password) : null;

        await _db.SaveChangesAsync();
        return Ok(ToCredentialDto(cred));
    }

    [HttpDelete("credentials")]
    public async Task<IActionResult> DeleteCredentials()
    {
        var cred = await _db.ChessableCredentials
            .FirstOrDefaultAsync(c => c.UserId == UserId);

        if (cred is not null)
        {
            _db.ChessableCredentials.Remove(cred);
            await _db.SaveChangesAsync();
        }

        return NoContent();
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestCredentials()
    {
        var cred = await _db.ChessableCredentials
            .FirstOrDefaultAsync(c => c.UserId == UserId);

        if (cred is null)
            return BadRequest(new { message = "No credentials saved" });

        try
        {
            string? bearer;
            string uid;

            if (cred.UseBearer && cred.EncryptedBearer is not null)
            {
                bearer = _encryption.Decrypt(cred.EncryptedBearer);
                var (extractedUid, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
                if (uidError is not null)
                    return BadRequest(new { message = $"Login failed: {uidError}" });
                uid = extractedUid;
            }
            else if (!cred.UseBearer && cred.EncryptedEmail is not null && cred.EncryptedPassword is not null)
            {
                var email = _encryption.Decrypt(cred.EncryptedEmail);
                var password = _encryption.Decrypt(cred.EncryptedPassword);
                var (jwt, loginError) = await _chessableHttp.LoginAsync(email, password);
                if (loginError is not null)
                {
                    var cleanMessage = loginError.Trim() is "{}" or "" ? "Invalid credentials" : loginError;
                    return BadRequest(new { message = $"Login failed: {cleanMessage}" });
                }
                bearer = jwt!;
                var (extractedUid, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
                if (uidError is not null)
                    return BadRequest(new { message = $"Login failed: {uidError}" });
                uid = extractedUid;
            }
            else
            {
                return BadRequest(new { message = "Incomplete credentials" });
            }

            // Validate by fetching courses
            var (courses, error) = await _chessableHttp.GetCoursesAsync(bearer, uid);
            if (error is not null)
            {
                var cleanMessage = error.Trim() is "{}" or "" ? "Invalid credentials" : error;
                return BadRequest(new { message = $"Login failed: {cleanMessage}" });
            }

            return Ok(new { message = "Login successful" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Login failed: {ex.Message}" });
        }
    }

    [HttpGet("courses")]
    public async Task<IActionResult> GetCourses()
    {
        var cred = await _db.ChessableCredentials
            .FirstOrDefaultAsync(c => c.UserId == UserId);

        if (cred is null)
            return BadRequest(new { message = "No credentials saved" });

        try
        {
            string? bearer;
            string uid;

            if (cred.UseBearer && cred.EncryptedBearer is not null)
            {
                bearer = _encryption.Decrypt(cred.EncryptedBearer);
                var (extractedUid, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
                if (uidError is not null)
                    return BadRequest(new { message = $"Login failed: {uidError}" });
                uid = extractedUid;
            }
            else if (!cred.UseBearer && cred.EncryptedEmail is not null && cred.EncryptedPassword is not null)
            {
                var email = _encryption.Decrypt(cred.EncryptedEmail);
                var password = _encryption.Decrypt(cred.EncryptedPassword);
                var (jwt, loginError) = await _chessableHttp.LoginAsync(email, password);
                if (loginError is not null)
                    return BadRequest(new { message = $"Login failed: {loginError}" });
                bearer = jwt!;
                var (extractedUid, uidError) = _chessableHttp.ExtractUidFromBearer(bearer);
                if (uidError is not null)
                    return BadRequest(new { message = $"Login failed: {uidError}" });
                uid = extractedUid;
            }
            else
            {
                return BadRequest(new { message = "Incomplete credentials" });
            }

            var (courses, error) = await _chessableHttp.GetCoursesAsync(bearer, uid);
            if (error is not null)
                return BadRequest(new { message = $"Failed to fetch courses: {error}" });

            var result = courses!.Select(c => new CourseListItem(c.Key, c.Value)).ToList();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Failed to fetch courses: {ex.Message}" });
        }
    }

    private CredentialResponse ToCredentialDto(ChessableCredential cred)
    {
        string? maskedBearer = null;
        string? maskedEmail = null;
        string? maskedPassword = null;

        if (cred.EncryptedBearer is not null)
        {
            var plain = _encryption.Decrypt(cred.EncryptedBearer);
            maskedBearer = Mask(plain);
        }
        if (cred.EncryptedEmail is not null)
        {
            var plain = _encryption.Decrypt(cred.EncryptedEmail);
            maskedEmail = MaskEmail(plain);
        }
        if (cred.EncryptedPassword is not null)
        {
            maskedPassword = "********";
        }

        return new CredentialResponse(cred.Id, cred.UseBearer, true, maskedBearer, maskedEmail, maskedPassword);
    }

    private static string Mask(string value)
    {
        if (value.Length <= 8) return new string('*', value.Length);
        return value[..4] + new string('*', value.Length - 8) + value[^4..];
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***" + email[at..];
        return email[0] + new string('*', at - 1) + email[at..];
    }
}
