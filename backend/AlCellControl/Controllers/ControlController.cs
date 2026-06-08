using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AlCellControl.Data;
using AlCellControl.Models;

namespace AlCellControl.Controllers;

[ApiController]
[Route("api/control")]
public class ControlController : ControllerBase
{
    private readonly AppDbContext _db;

    public ControlController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("feed")]
    public async Task<IActionResult> Feed([FromBody] FeedCommandDto dto)
    {
        var feedingRecord = new FeedingRecord
        {
            CellId = dto.CellId,
            FeedType = dto.FeedType,
            FeedAmountKg = dto.FeedAmountKg,
            FedAt = DateTime.UtcNow
        };
        _db.FeedingRecords.Add(feedingRecord);

        var command = new CellControlCommand
        {
            CellId = dto.CellId,
            CommandType = "ManualFeed",
            CommandParams = $"{{\"feedType\":\"{dto.FeedType}\",\"feedAmountKg\":{dto.FeedAmountKg}}}",
            IssuedAt = DateTime.UtcNow,
            Status = "Pending"
        };
        _db.CellControlCommands.Add(command);

        await _db.SaveChangesAsync();

        return Ok(new { FeedingRecord = feedingRecord, Command = command });
    }

    [HttpPost("extinguish")]
    public async Task<IActionResult> Extinguish([FromBody] ExtinguishDto dto)
    {
        var command = new CellControlCommand
        {
            CellId = dto.CellId,
            CommandType = "ExtinguishAnodeEffect",
            CommandParams = $"{{\"cellId\":{dto.CellId}}}",
            IssuedAt = DateTime.UtcNow,
            Status = "Pending"
        };
        _db.CellControlCommands.Add(command);

        await _db.SaveChangesAsync();

        return Ok(command);
    }

    [HttpGet("commands")]
    public async Task<IActionResult> Commands()
    {
        var commands = await _db.CellControlCommands
            .OrderByDescending(c => c.IssuedAt)
            .Take(50)
            .Include(c => c.CellInfo)
            .ToListAsync();

        return Ok(commands);
    }
}

public record FeedCommandDto(
    int CellId,
    string FeedType,
    double FeedAmountKg
);

public record ExtinguishDto(
    int CellId
);
