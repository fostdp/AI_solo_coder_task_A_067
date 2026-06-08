using System.Collections.Concurrent;

namespace AluminumCellControl.Services;

public class DataBufferService
{
    private readonly ConcurrentDictionary<int, ConcurrentQueue<double>> _voltageBuffers = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<double>> _currentBuffers = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<double>> _noiseBuffer = new();
    private readonly ConcurrentDictionary<int, double> _lastVoltage = new();
    private readonly ConcurrentDictionary<int, double> _voltageSlope = new();
    private readonly ConcurrentDictionary<int, int> _spikeCount = new();
    private readonly ConcurrentDictionary<int, double> _voltageRange = new();
    private readonly ConcurrentDictionary<int, double> _minVoltage = new();
    private readonly ConcurrentDictionary<int, double> _maxVoltage = new();
    private const int BufferSize = 120;

    public void AddVoltage(int cellId, double voltage)
    {
        var buffer = _voltageBuffers.GetOrAdd(cellId, _ => new ConcurrentQueue<double>());
        buffer.Enqueue(voltage);
        while (buffer.Count > BufferSize) buffer.TryDequeue(out _);

        if (_lastVoltage.TryGetValue(cellId, out var lastV))
        {
            if (voltage - lastV > 0.5) _spikeCount.AddOrUpdate(cellId, 1, (_, v) => v + 1);
        }
        _lastVoltage[cellId] = voltage;

        _minVoltage.AddOrUpdate(cellId, voltage, (_, v) => Math.Min(v, voltage));
        _maxVoltage.AddOrUpdate(cellId, voltage, (_, v) => Math.Max(v, voltage));
        _voltageRange[cellId] = _maxVoltage.GetValueOrDefault(cellId) - _minVoltage.GetValueOrDefault(cellId);
    }

    public void AddCurrent(int cellId, double current)
    {
        var buffer = _currentBuffers.GetOrAdd(cellId, _ => new ConcurrentQueue<double>());
        buffer.Enqueue(current);
        while (buffer.Count > BufferSize) buffer.TryDequeue(out _);
    }

    public void AddNoise(int cellId, double noise)
    {
        var buffer = _noiseBuffer.GetOrAdd(cellId, _ => new ConcurrentQueue<double>());
        buffer.Enqueue(noise);
        while (buffer.Count > BufferSize) buffer.TryDequeue(out _);
    }

    public void UpdateSlope(int cellId, double slope)
    {
        _voltageSlope[cellId] = slope;
    }

    public List<double> GetVoltages(int cellId) => _voltageBuffers.GetValueOrDefault(cellId)?.ToList() ?? new();
    public List<double> GetCurrents(int cellId) => _currentBuffers.GetValueOrDefault(cellId)?.ToList() ?? new();
    public double GetVoltageNoise(int cellId) => _noiseBuffer.GetValueOrDefault(cellId)?.Average() ?? 0;
    public double GetVoltageSlope(int cellId) => _voltageSlope.GetValueOrDefault(cellId);
    public int GetSpikeCount(int cellId) => _spikeCount.GetValueOrDefault(cellId);
    public double GetVoltageRange(int cellId) => _voltageRange.GetValueOrDefault(cellId);
    public double GetVoltageMean(int cellId) => _voltageBuffers.GetValueOrDefault(cellId)?.Average() ?? 4.0;

    public void ResetSpikeCount(int cellId) => _spikeCount[cellId] = 0;
    public void ResetVoltageRange(int cellId)
    {
        _minVoltage[cellId] = _lastVoltage.GetValueOrDefault(cellId);
        _maxVoltage[cellId] = _lastVoltage.GetValueOrDefault(cellId);
        _voltageRange[cellId] = 0;
    }
}
