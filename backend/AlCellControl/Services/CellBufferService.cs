using System.Collections.Concurrent;

namespace AlCellControl.Services;

public class CellBufferService
{
    private readonly ConcurrentDictionary<int, CellBuffer> _buffers = new();

    public CellBuffer GetBuffer(int cellId)
    {
        return _buffers.GetOrAdd(cellId, _ => new CellBuffer());
    }

    public void Write(int cellId, double voltage, double currentAvg)
    {
        var buffer = GetBuffer(cellId);
        buffer.Write(voltage, currentAvg);
    }

    public double[] ReadVoltages(int cellId, int count)
    {
        var buffer = GetBuffer(cellId);
        return buffer.ReadVoltages(count);
    }

    public double[] ReadCurrents(int cellId, int count)
    {
        var buffer = GetBuffer(cellId);
        return buffer.ReadCurrents(count);
    }
}

public class CellBuffer
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly List<double> _voltages = new();
    private readonly List<double> _currents = new();
    private const int MaxCapacity = 120;

    public void Write(double voltage, double currentAvg)
    {
        _lock.EnterWriteLock();
        try
        {
            _voltages.Add(voltage);
            _currents.Add(currentAvg);
            if (_voltages.Count > MaxCapacity)
            {
                _voltages.RemoveAt(0);
                _currents.RemoveAt(0);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public double[] ReadVoltages(int count)
    {
        _lock.EnterReadLock();
        try
        {
            int skip = Math.Max(0, _voltages.Count - count);
            return _voltages.Skip(skip).ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public double[] ReadCurrents(int count)
    {
        _lock.EnterReadLock();
        try
        {
            int skip = Math.Max(0, _currents.Count - count);
            return _currents.Skip(skip).ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
