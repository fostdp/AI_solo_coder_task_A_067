using System.Collections.Concurrent;

namespace AluminumCellControl.Services;

public class DataBufferService
{
    private readonly ConcurrentDictionary<int, CellBuffer> _cellBuffers = new();
    private const int BufferSize = 120;

    private class CellBuffer
    {
        public readonly ReaderWriterLockSlim Lock = new();
        public readonly List<double> Voltages = new(BufferSize + 16);
        public readonly List<double> Currents = new(BufferSize + 16);
        public readonly List<double> Noises = new(BufferSize + 16);
        public double LastVoltage;
        public double VoltageSlope;
        public int SpikeCount;
        public double VoltageRange;
        public double MinVoltage;
        public double MaxVoltage;
        public double[] FftSpectrum = Array.Empty<double>();
        public double FftDominantFreq;
        public double FftSpectralEnergy;
        public double FtfHighFreqRatio;
    }

    private CellBuffer GetBuffer(int cellId) =>
        _cellBuffers.GetOrAdd(cellId, _ => new CellBuffer());

    public void AddVoltage(int cellId, double voltage)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.Voltages.Add(voltage);
            while (buf.Voltages.Count > BufferSize) buf.Voltages.RemoveAt(0);

            if (buf.LastVoltage != 0 && voltage - buf.LastVoltage > 0.5)
                buf.SpikeCount++;

            buf.LastVoltage = voltage;

            if (voltage < buf.MinVoltage || buf.MinVoltage == 0) buf.MinVoltage = voltage;
            if (voltage > buf.MaxVoltage) buf.MaxVoltage = voltage;
            buf.VoltageRange = buf.MaxVoltage - buf.MinVoltage;
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }

    public void AddCurrent(int cellId, double current)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.Currents.Add(current);
            while (buf.Currents.Count > BufferSize) buf.Currents.RemoveAt(0);
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }

    public void AddNoise(int cellId, double noise)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.Noises.Add(noise);
            while (buf.Noises.Count > BufferSize) buf.Noises.RemoveAt(0);
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }

    public void UpdateSlope(int cellId, double slope)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.VoltageSlope = slope;
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }

    public void UpdateFftFeatures(int cellId, double[] spectrum, double dominantFreq, double spectralEnergy, double highFreqRatio)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.FftSpectrum = spectrum;
            buf.FftDominantFreq = dominantFreq;
            buf.FftSpectralEnergy = spectralEnergy;
            buf.FtfHighFreqRatio = highFreqRatio;
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }

    public List<double> GetVoltages(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.Voltages.ToList();
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public List<double> GetCurrents(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.Currents.ToList();
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public double GetVoltageNoise(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.Noises.Count > 0 ? buf.Noises.Average() : 0;
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public double GetVoltageSlope(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.VoltageSlope;
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public int GetSpikeCount(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.SpikeCount;
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public double GetVoltageRange(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.VoltageRange;
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public double GetVoltageMean(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return buf.Voltages.Count > 0 ? buf.Voltages.Average() : 4.0;
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public (double DominantFreq, double SpectralEnergy, double HighFreqRatio) GetFftFeatures(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterReadLock();
        try
        {
            return (buf.FftDominantFreq, buf.FftSpectralEnergy, buf.FtfHighFreqRatio);
        }
        finally
        {
            buf.Lock.ExitReadLock();
        }
    }

    public void ResetSpikeCount(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.SpikeCount = 0;
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }

    public void ResetVoltageRange(int cellId)
    {
        var buf = GetBuffer(cellId);
        buf.Lock.EnterWriteLock();
        try
        {
            buf.MinVoltage = buf.LastVoltage;
            buf.MaxVoltage = buf.LastVoltage;
            buf.VoltageRange = 0;
        }
        finally
        {
            buf.Lock.ExitWriteLock();
        }
    }
}
