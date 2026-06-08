using Microsoft.EntityFrameworkCore;
using AlCellControl.Models;

namespace AlCellControl.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<CellInfo> CellInfos { get; set; }
    public DbSet<CellRealtimeData> CellRealtimeData { get; set; }
    public DbSet<AluminaConcentrationHistory> AluminaConcentrationHistory { get; set; }
    public DbSet<FeedingRecord> FeedingRecords { get; set; }
    public DbSet<AnodeEffectPrediction> AnodeEffectPredictions { get; set; }
    public DbSet<AlarmRecord> AlarmRecords { get; set; }
    public DbSet<CellControlCommand> CellControlCommands { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CellInfo>(entity =>
        {
            entity.HasKey(c => c.CellId);

            entity.HasMany(c => c.CellRealtimeData)
                .WithOne(d => d.CellInfo)
                .HasForeignKey(d => d.CellId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.AluminaConcentrationHistory)
                .WithOne(h => h.CellInfo)
                .HasForeignKey(h => h.CellId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.FeedingRecords)
                .WithOne(f => f.CellInfo)
                .HasForeignKey(f => f.CellId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.AnodeEffectPredictions)
                .WithOne(p => p.CellInfo)
                .HasForeignKey(p => p.CellId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.AlarmRecords)
                .WithOne(a => a.CellInfo)
                .HasForeignKey(a => a.CellId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(c => c.ControlCommands)
                .WithOne(cmd => cmd.CellInfo)
                .HasForeignKey(cmd => cmd.CellId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CellRealtimeData>(entity =>
        {
            entity.HasIndex(d => d.CellId);
            entity.HasIndex(d => d.ReceivedAt);
            entity.HasIndex(d => new { d.CellId, d.ReceivedAt });
        });
    }
}
