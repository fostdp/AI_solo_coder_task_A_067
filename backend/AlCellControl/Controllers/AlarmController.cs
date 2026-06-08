using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AlCellControl.Data;
using AlCellControl.Models;

namespace AlCellControl.Controllers;

[ApiController]
[Route("api/alarm")]
public class AlarmController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlarmController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("active")]
    public async Task<IActionResult> Active()
    {
        var alarms = await _db.AlarmRecords
            .Where(a => !a.IsAcknowledged)
            .OrderByDescending(a => a.TriggeredAt)
            .Include(a => a.CellInfo)
            .ToListAsync();

        return Ok(alarms);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var query = _db.AlarmRecords
            .Include(a => a.CellInfo)
            .OrderByDescending(a => a.TriggeredAt);

        var total = await query.CountAsync();
        var alarms = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Data = alarms
        });
    }

    [HttpPost("{id}/acknowledge")]
    public async Task<IActionResult> Acknowledge(long id)
    {
        var alarm = await _db.AlarmRecords.FindAsync(id);
        if (alarm == null)
            return NotFound();

        alarm.IsAcknowledged = true;
        alarm.AcknowledgedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(alarm);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var activeAlarms = await _db.AlarmRecords
            .Where(a => !a.IsAcknowledged)
            .ToListAsync();

        var byLevel = activeAlarms
            .GroupBy(a => a.AlarmLevel)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var byType = activeAlarms
            .GroupBy(a => a.AlarmType)
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(new
        {
            TotalActive = activeAlarms.Count,
            ByLevel = byLevel,
            ByType = byType
        });
    }
}
