using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("CellRealtimeData")]
public class CellRealtimeData
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey(nameof(CellInfo))]
    public int CellId { get; set; }

    public double Voltage { get; set; }

    [Column(TypeName = "nvarchar(max)")]
    public string AnodeCurrentDistribution { get; set; } = string.Empty;

    public double CellTemperature { get; set; }

    public double BathTemperature { get; set; }

    public double AluminumLevel { get; set; }

    public double BathLevel { get; set; }

    public double AluminaConcentration { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public CellInfo CellInfo { get; set; } = null!;
}
