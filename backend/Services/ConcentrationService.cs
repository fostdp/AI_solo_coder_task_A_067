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

        double fftDominantFreq, fftSpectralEnergy, fftHighFreqRatio;

        var fftFeatures = _bufferService.GetFftFeatures(cellId);
        if (fftFeatures.DominantFreq == 0 && voltages.Count >= 32)
        {
            var voltageArray = voltages.TakeLast(64).ToArray();
            var mean = voltageArray.Average();
            var detrended = voltageArray.Select(v => v - mean).ToArray();
            var spectrum = SvrConcentrationModel.ComputeFft(detrended);
            var samplingRateHz = 1.0 / 15.0;
            var features = SvrConcentrationModel.ExtractFftFeatures(spectrum, samplingRateHz);
            _bufferService.UpdateFftFeatures(cellId, spectrum, features.DominantFreq, features.SpectralEnergy, features.HighFreqRatio);
            fftDominantFreq = features.DominantFreq;
            fftSpectralEnergy = features.SpectralEnergy;
            fftHighFreqRatio = features.HighFreqRatio;
        }
        else
        {
            fftDominantFreq = fftFeatures.DominantFreq;
            fftSpectralEnergy = fftFeatures.SpectralEnergy;
            fftHighFreqRatio = fftFeatures.HighFreqRatio;
        }

        var concentration = _svrModel.PredictConcentration(voltages, currents, voltageNoise, voltageSlope,
            fftDominantFreq, fftSpectralEnergy, fftHighFreqRatio);
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
            ModelVersion = "SVR-v2.0-FFT"
        };
        _db.AluminaConcentrations.Add(record);

        var cell = await _db.Cells.FindAsync(cellId);
        if (cell != null)
        {
            cell.Concentration = concentrationDecimal;
            cell.ConcentrationStatus = status;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Cell {CellId}: Al2O3 = {Concentration}%, Status = {Status}, FFT: freq={Freq:F3} energy={Energy:F4} hfRatio={Ratio:F3}",
            cellId, concentrationDecimal, status, fftDominantFreq, fftSpectralEnergy, fftHighFreqRatio);

        return concentrationDecimal;
    }
}
