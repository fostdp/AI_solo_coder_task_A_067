using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("CellInfo")]
public class CellInfo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int CellId { get; set; }

    [Required]
    [StringLength(50)]
    public string CellName { get; set; } = string.Empty;

    public int RowIndex { get; set; }

    public int ColIndex { get; set; }

    [StringLength(20)]
    public string Zone { get; set; } = string.Empty;

    public bool IsOnline { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CellRealtimeData> CellRealtimeData { get; set; } = new List<CellRealtimeData>();
    public ICollection<AluminaConcentrationHistory> AluminaConcentrationHistory { get; set; } = new List<AluminaConcentrationHistory>();
    public ICollection<FeedingRecord> FeedingRecords { get; set; } = new List<FeedingRecord>();
    public ICollection<AnodeEffectPrediction> AnodeEffectPredictions { get; set; } = new List<AnodeEffectPrediction>();
    public ICollection<AlarmRecord> AlarmRecords { get; set; } = new List<AlarmRecord>();
    public ICollection<CellControlCommand> ControlCommands { get; set; } = new List<CellControlCommand>();
}
