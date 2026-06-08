using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("FeedingRecord")]
public class FeedingRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey(nameof(CellInfo))]
    public int CellId { get; set; }

    [StringLength(50)]
    public string FeedType { get; set; } = string.Empty;

    public double FeedAmountKg { get; set; }

    public DateTime FedAt { get; set; } = DateTime.UtcNow;

    public CellInfo CellInfo { get; set; } = null!;
}
