using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("AluminaConcentrationHistory")]
public class AluminaConcentrationHistory
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey(nameof(CellInfo))]
    public int CellId { get; set; }

    public double EstimatedConcentration { get; set; }

    [StringLength(50)]
    public string ModelVersion { get; set; } = string.Empty;

    public DateTime EstimatedAt { get; set; } = DateTime.UtcNow;

    public CellInfo CellInfo { get; set; } = null!;
}
