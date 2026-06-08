using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("AlarmRecords")]
public class AlarmRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey(nameof(CellInfo))]
    public int CellId { get; set; }

    public int AlarmLevel { get; set; }

    [StringLength(50)]
    public string AlarmType { get; set; } = string.Empty;

    [StringLength(500)]
    public string AlarmMessage { get; set; } = string.Empty;

    public bool IsAcknowledged { get; set; }

    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;

    public DateTime? AcknowledgedAt { get; set; }

    public CellInfo CellInfo { get; set; } = null!;
}
