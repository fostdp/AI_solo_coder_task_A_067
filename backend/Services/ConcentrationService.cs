using AluminumCellControl.Data;
using AluminumCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Services;

public class ConcentrationService
{
    private readonly AppDbContext _db;
    private readonly SvrConcentrationModel _svrModel;
    private readonly DataBufferService _bufferService;
    private readonly ILogger<ConcentrationService> _logger;

    public ConcentrationService(AppDbContext db, SvrConcentrationModel svrModel,
        DataBufferService bufferService, ILogger<ConcentrationService> logger)
    {
        _db = db;
        _svrModel = svrModel;
        _bufferService = bufferService;
        _logger = logger;
    }

    public async Task<decimal> EstimateConcentrationAsync(int cellId, SensorData data)
    {
        var voltages = _bufferService.GetVoltages(cellId);
        var currents = _bufferService.GetCurrents(cellId);
        var voltageNoise = _bufferService.GetVoltageNoise(cellId);
        var voltageSlope = _bufferService.GetVoltageSlope(cellId);

        if (voltages.Count < 5) return 3.0m;

        var concentration = _svrModel.PredictConcentration(voltages, currents, voltageNoise, voltageSlope);
        var concentrationDecimal = (decimal)Math.Round(concentration, 2);

        var status = concentrationDecimal switch
        {
            >= 2.5m => "正常",
            >= 1.8m => "偏低",
            _ => "极低"
        };

        var record = new AluminaConcentration
        {
            CellId = cellId,
            Timestamp = data.Timestamp,
            Concentration = concentrationDecimal,
            Status = status,
            ModelVersion = "SVR-v1.0"
        };
        _db.AluminaConcentrations.Add(record);

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell != null)
        {
            cell.Concentration = concentrationDecimal;
            cell.ConcentrationStatus = status;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Cell {CellId}: Al2O3 concentration = {Concentration}%, Status = {Status}", cellId, concentrationDecimal, status);

        return concentrationDecimal;
    }
}
