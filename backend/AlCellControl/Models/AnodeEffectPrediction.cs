using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("AnodeEffectPrediction")]
public class AnodeEffectPrediction
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey(nameof(CellInfo))]
    public int CellId { get; set; }

    public double Probability { get; set; }

    public DateTime PredictedAt { get; set; } = DateTime.UtcNow;

    public CellInfo CellInfo { get; set; } = null!;
}
