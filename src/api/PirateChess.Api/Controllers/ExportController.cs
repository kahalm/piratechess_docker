using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PirateChess.Api.BackgroundJobs;
using PirateChess.Api.Data;
using PirateChess.Api.Models.DTOs;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ExportController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ExportJobQueue _jobQueue;

    public ExportController(AppDbContext db, ExportJobQueue jobQueue)
    {
        _db = db;
        _jobQueue = jobQueue;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public async Task<IActionResult> StartExport(StartExportRequest request)
    {
        var validModes = new[] { "AllKeyMoves", "FirstKeyMove", "None" };
        if (!validModes.Contains(request.TrainingMode))
            return BadRequest(new { message = "Invalid training mode. Use: AllKeyMoves, FirstKeyMove, None" });

        var export = new ExportHistory
        {
            UserId = UserId,
            ChessableBid = request.Bid,
            CourseName = request.CourseName,
            TrainingMode = request.TrainingMode,
            Status = "Running"
        };

        _db.ExportHistories.Add(export);
        await _db.SaveChangesAsync();

        await _jobQueue.EnqueueAsync(new ExportJobRequest(
            UserId, export.Id, request.Bid, request.CourseName, request.TrainingMode));

        return Ok(ToDto(export));
    }

    [HttpGet]
    public async Task<IActionResult> GetExports()
    {
        var exports = await _db.ExportHistories
            .Where(e => e.UserId == UserId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync();

        return Ok(exports.Select(ToDto));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetExport(int id)
    {
        var export = await _db.ExportHistories
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == UserId);

        if (export is null)
            return NotFound();

        return Ok(ToDto(export));
    }

    [HttpGet("{id:int}/pgn")]
    public async Task<IActionResult> DownloadPgn(int id)
    {
        var export = await _db.ExportHistories
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == UserId);

        if (export is null)
            return NotFound();

        if (export.Status != "Completed")
            return BadRequest(new { message = "Export not completed yet" });

        var pgn = await _db.GeneratedPgns
            .FirstOrDefaultAsync(p => p.UserId == UserId
                && p.CachedCourse!.ChessableBid == export.ChessableBid
                && p.TrainingMode == export.TrainingMode);

        if (pgn is null)
            return NotFound(new { message = "PGN not found" });

        var fileName = $"{export.CourseName.Replace(" ", "_")}_{export.TrainingMode}.pgn";
        var bytes = System.Text.Encoding.UTF8.GetBytes(pgn.PgnContent);
        return File(bytes, "application/x-chess-pgn", fileName);
    }

    private static ExportStatusResponse ToDto(ExportHistory e) => new(
        e.Id, e.Status, e.ChessableBid, e.CourseName, e.TrainingMode,
        e.ChapterCount, e.LineCount, e.StartedAt, e.CompletedAt);
}
