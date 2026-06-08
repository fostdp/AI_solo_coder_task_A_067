using AluminumCellControl.Data;
using AluminumCellControl.Hubs;
using AluminumCellControl.Models;
using AluminumCellControl.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CellsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DataProcessingService _processingService;
    private readonly FeedingService _feedingService;
    private readonly IHubContext<CellHub> _hubContext;
    private readonly ILogger<CellsController> _logger;

    public CellsController(AppDbContext db, DataProcessingService processingService,
        FeedingService feedingService, IHubContext<CellHub> hubContext, ILogger<CellsController> logger)
    {
        _db = db;
        _processingService = processingService;
        _feedingService = feedingService;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CellStatusDto>>> GetAllCells()
    {
        var cells = await _db.Cells
            .OrderBy(c => c.CellId)
            .Select(c => new CellStatusDto
            {
                CellId = c.CellId,
                CellName = c.CellName,
                RowIndex = c.RowIndex,
                ColIndex = c.ColIndex,
                Status = c.Status,
                Concentration = c.Concentration,
                ConcentrationStatus = c.ConcentrationStatus,
                AnodeEffectProbability = c.AnodeEffectProbability,
                LastDataTime = c.LastDataTime,
                IsFlashing = c.AnodeEffectProbability >= 80
            })
            .ToListAsync();

        return Ok(cells);
    }

    [HttpGet("{cellId}/history")]
    public async Task<ActionResult<CellHistoryDto>> GetCellHistory(int cellId, [FromQuery] int hours = 8)
    {
        var since = DateTime.UtcNow.AddHours(-hours);

        var sensorData = await _db.SensorData
            .Where(s => s.CellId == cellId && s.Timestamp >= since)
            .OrderBy(s => s.Timestamp)
            .ToListAsync();

        var voltageSeries = sensorData.Select(s => new SensorDataPoint
        {
            Timestamp = s.Timestamp,
            Value = (double)s.Voltage
        }).ToList();

        var currentDistSeries = new List<CurrentDistPoint>();
        foreach (var s in sensorData)
        {
            if (string.IsNullOrEmpty(s.AnodeCurrentDistribution)) continue;
            try
            {
                var values = s.AnodeCurrentDistribution.Split(',')
                    .Where(p => double.TryParse(p, out _))
                    .Select(double.Parse).ToList();
                if (values.Count > 0)
                {
                    currentDistSeries.Add(new CurrentDistPoint
                    {
                        Timestamp = s.Timestamp,
                        Mean = values.Average(),
                        StdDev = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v - values.Average()) * (v - values.Average())) / (values.Count - 1)) : 0
                    });
                }
            }
            catch { }
        }

        return Ok(new CellHistoryDto
        {
            VoltageSeries = voltageSeries,
            CurrentDistributionSeries = currentDistSeries
        });
    }

    [HttpGet("{cellId}/feedings")]
    public async Task<ActionResult<IEnumerable<FeedingRecord>>> GetFeedingRecords(int cellId, [FromQuery] int count = 10)
    {
        var records = await _db.FeedingRecords
            .Where(f => f.CellId == cellId)
            .OrderByDescending(f => f.Timestamp)
            .Take(count)
            .ToListAsync();

        return Ok(records);
    }

    [HttpGet("{cellId}/predictions")]
    public async Task<ActionResult<IEnumerable<AnodeEffectPrediction>>> GetPredictions(int cellId, [FromQuery] int count = 20)
    {
        var predictions = await _db.AnodeEffectPredictions
            .Where(p => p.CellId == cellId)
            .OrderByDescending(p => p.Timestamp)
            .Take(count)
            .ToListAsync();

        return Ok(predictions);
    }

    [HttpPost("data")]
    public async Task<ActionResult> ReceiveSensorData([FromBody] SensorDataDto dto)
    {
        if (dto.CellId < 1 || dto.CellId > 200)
            return BadRequest("CellId must be between 1 and 200");

        if (dto.Timestamp == default) dto.Timestamp = DateTime.UtcNow;

        await _processingService.ProcessSensorDataAsync(dto);
        return Ok(new { success = true, cellId = dto.CellId });
    }

    [HttpPost("{cellId}/feed")]
    public async Task<ActionResult> ManualFeed(int cellId, [FromBody] FeedCommandDto? command = null)
    {
        command ??= new FeedCommandDto();
        await _feedingService.ManualFeedAsync(cellId, command.AmountKg, command.Reason);

        await _hubContext.Clients.Group($"cell-{cellId}")
            .SendAsync("FeedingExecuted", new { CellId = cellId, Amount = command.AmountKg, Time = DateTime.UtcNow });

        return Ok(new { success = true, cellId, amount = command.AmountKg });
    }
}

[ApiController]
[Route("api/[controller]")]
public class AlarmsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlarmsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AlarmDto>>> GetAlarms([FromQuery] bool activeOnly = true)
    {
        var query = _db.Alarms.AsQueryable();
        if (activeOnly) query = query.Where(a => !a.IsResolved);

        var alarms = await query
            .OrderByDescending(a => a.Timestamp)
            .Take(100)
            .Select(a => new AlarmDto
            {
                Id = a.Id,
                CellId = a.CellId,
                Timestamp = a.Timestamp,
                AlarmType = a.AlarmType,
                AlarmLevel = a.AlarmLevel,
                Message = a.Message,
                IsResolved = a.IsResolved
            })
            .ToListAsync();

        return Ok(alarms);
    }

    [HttpPost("{id}/resolve")]
    public async Task<ActionResult> ResolveAlarm(long id)
    {
        var alarm = await _db.Alarms.FindAsync(id);
        if (alarm == null) return NotFound();

        alarm.IsResolved = true;
        alarm.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { success = true });
    }
}
