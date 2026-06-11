using Microsoft.AspNetCore.Mvc;
using PirateChess.Api.Authorization;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Services;

namespace PirateChess.Api.Controllers;

/// <summary>
/// Stateless Chessable endpoints for service-to-service callers (rookhub).
/// The bearer is passed per request and never persisted in piratechess.
/// Authenticated via the <c>X-Service-Key</c> header (see <see cref="ServiceKeyAuthAttribute"/>).
/// </summary>
[ApiController]
[Route("api/chessable/direct")]
[ServiceKeyAuth]
public class ChessableDirectController : ControllerBase
{
    private readonly IChessableHttpService _chessableHttp;

    public ChessableDirectController(IChessableHttpService chessableHttp)
    {
        _chessableHttp = chessableHttp;
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DirectBearerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (courses, error) = await _chessableHttp.GetCoursesAsync(request.Bearer, uid, ct);
        if (error is not null)
        {
            var cleanMessage = error.Trim() is "{}" or "" ? "Invalid bearer" : error;
            return BadRequest(new { message = cleanMessage });
        }

        return Ok(new DirectTestResponse(uid, courses!.Count));
    }

    [HttpPost("courses")]
    public async Task<IActionResult> Courses([FromBody] DirectBearerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Bearer))
            return BadRequest(new { message = "Bearer is required" });

        var (uid, uidError) = _chessableHttp.ExtractUidFromBearer(request.Bearer);
        if (uidError is not null)
            return BadRequest(new { message = uidError });

        var (courses, error) = await _chessableHttp.GetCoursesAsync(request.Bearer, uid, ct);
        if (error is not null)
        {
            var cleanMessage = error.Trim() is "{}" or "" ? "Invalid bearer" : error;
            return BadRequest(new { message = cleanMessage });
        }

        var result = courses!.Select(c => new CourseListItem(c.Key, c.Value)).ToList();
        return Ok(result);
    }
}
