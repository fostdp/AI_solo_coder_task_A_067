using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AlCellControl.Models;

[Table("CellControlCommand")]
public class CellControlCommand
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey(nameof(CellInfo))]
    public int CellId { get; set; }

    [StringLength(50)]
    public string CommandType { get; set; } = string.Empty;

    [Column(TypeName = "nvarchar(max)")]
    public string CommandParams { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExecutedAt { get; set; }

    [StringLength(20)]
    public string Status { get; set; } = string.Empty;

    public CellInfo CellInfo { get; set; } = null!;
}
