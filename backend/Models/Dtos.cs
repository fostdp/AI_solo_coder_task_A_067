namespace AluminumCellControl.Models;

public class SensorDataDto
{
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public decimal Voltage { get; set; }
    public string? AnodeCurrentDistribution { get; set; }
    public decimal? CellTemp { get; set; }
    public decimal? BathTemp { get; set; }
    public decimal? AlLevel { get; set; }
    public decimal? BathLevel { get; set; }
}

public class CellStatusDto
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
    public decimal? Voltage { get; set; }
    public decimal? CellTemp { get; set; }
    public bool IsFlashing { get; set; }
}

public class CellHistoryDto
{
    public List<SensorDataPoint> VoltageSeries { get; set; } = new();
    public List<CurrentDistPoint> CurrentDistributionSeries { get; set; } = new();
}

public class SensorDataPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}

public class CurrentDistPoint
{
    public DateTime Timestamp { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
}

public class AlarmDto
{
    public long Id { get; set; }
    public int CellId { get; set; }
    public DateTime Timestamp { get; set; }
    public string AlarmType { get; set; } = string.Empty;
    public int AlarmLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
}

public class FeedCommandDto
{
    public decimal AmountKg { get; set; } = 25m;
    public string Reason { get; set; } = "手动补料";
}
