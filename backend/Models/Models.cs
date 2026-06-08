namespace AluminumCellControl.Models;

public class Cell
{
    public int CellId { get; set; }
    public string CellName { get; set; } = string.Empty;
    public int RowIndex { get; set; }
    public int ColIndex { get; set; }
    public string Status { get; set; } = "正常";
    public decimal? Concentration { get; set; }
    public string ConcentrationStatus { get; set; } = "正常";
    public decimal? AnodeEffectProbability { get; set; }
    public DateTime? LastDataTime { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SensorData
{
    public long Id { get; set; }
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal Voltage { get; set; }
    public string? AnodeCurrentDistribution { get; set; }
    public decimal? CellTemp { get; set; }
    public decimal? BathTemp { get; set; }
    public decimal? AlLevel { get; set; }
    public decimal? BathLevel { get; set; }
    public decimal? VoltageNoise { get; set; }
    public decimal? VoltageFluctuationFreq { get; set; }
}

public class AluminaConcentration
{
    public long Id { get; set; }
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal Concentration { get; set; }
    public string Status { get; set; } = "正常";
    public string? ModelVersion { get; set; }
}

public class FeedingRecord
{
    public long Id { get; set; }
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal FeedAmountKg { get; set; }
    public string FeedType { get; set; } = "自动";
    public string? TriggerReason { get; set; }
}

public class AnodeEffectPrediction
{
    public long Id { get; set; }
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal Probability { get; set; }
    public int PredictedMinutesAhead { get; set; }
    public string? ModelVersion { get; set; }
}

public class Alarm
{
    public long Id { get; set; }
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public string AlarmType { get; set; } = string.Empty;
    public int AlarmLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class ConcentrationAlarmTracker
{
    public int CellId { get; set; }
    public DateTime? LowStartTime { get; set; }
    public bool IsAlarmActive { get; set; }
}
