using AluminumCellControl.Data;
using AluminumCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Services;

public class PredictionService
{
    private readonly AppDbContext _db;
    private readonly RandomForestEffectModel _rfModel;
    private readonly DataBufferService _bufferService;
    private readonly ILogger<PredictionService> _logger;

    public PredictionService(AppDbContext db, RandomForestEffectModel rfModel,
        DataBufferService bufferService, ILogger<PredictionService> logger)
    {
        _db = db;
        _rfModel = rfModel;
        _bufferService = bufferService;
        _logger = logger;
    }

    public async Task<double> PredictAnodeEffectAsync(int cellId, SensorData data)
    {
        var voltageNoise = _bufferService.GetVoltageNoise(cellId);
        var voltageMean = _bufferService.GetVoltageMean(cellId);
        var spikeCount = _bufferService.GetSpikeCount(cellId);
        var voltageRange = _bufferService.GetVoltageRange(cellId);
        var concentration = 3.0;

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell?.Concentration != null) concentration = (double)cell.Concentration.Value;

        var freq = data.VoltageFluctuationFreq.HasValue ? (double)data.VoltageFluctuationFreq.Value : 0.5;

        var probability = _rfModel.PredictAnodeEffect(
            voltageNoise, freq, voltageMean, spikeCount, voltageRange, concentration);

        var probDecimal = (decimal)Math.Round(probability * 100, 1);

        var record = new AnodeEffectPrediction
        {
            CellId = cellId,
            Timestamp = data.Timestamp,
            Probability = probDecimal,
            PredictedMinutesAhead = 3,
            ModelVersion = "RF-v1.0"
        };
        _db.AnodeEffectPredictions.Add(record);

        if (cell != null)
        {
            cell.AnodeEffectProbability = probDecimal;
            if (probDecimal >= 80m)
            {
                cell.Status = "效应预警";
                _logger.LogWarning("Cell {CellId}: Anode effect probability = {Probability}% >= 80%, FLASHING ALERT", cellId, probDecimal);
            }
            else if (cell.Status == "效应预警")
            {
                cell.Status = "正常";
            }
        }

        await _db.SaveChangesAsync();

        _bufferService.ResetSpikeCount(cellId);
        _bufferService.ResetVoltageRange(cellId);

        return probability;
    }
}
