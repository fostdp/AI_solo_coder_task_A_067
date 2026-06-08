using AluminumCellControl.Models;
using Microsoft.EntityFrameworkCore;

namespace AluminumCellControl.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Cell> Cells => Set<Cell>();
    public DbSet<SensorData> SensorData => Set<SensorData>();
    public DbSet<AluminaConcentration> AluminaConcentrations => Set<AluminaConcentration>();
    public DbSet<FeedingRecord> FeedingRecords => Set<FeedingRecord>();
    public DbSet<AnodeEffectPrediction> AnodeEffectPredictions => Set<AnodeEffectPrediction>();
    public DbSet<Alarm> Alarms => Set<Alarm>();
    public DbSet<ConcentrationAlarmTracker> ConcentrationAlarmTrackers => Set<ConcentrationAlarmTracker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Cell>(e =>
        {
            e.HasKey(c => c.CellId);
            e.Property(c => c.CellName).IsRequired().HasMaxLength(50);
            e.Property(c => c.Status).IsRequired().HasMaxLength(20).HasDefaultValue("正常");
            e.Property(c => c.ConcentrationStatus).IsRequired().HasMaxLength(10).HasDefaultValue("正常");
        });

        modelBuilder.Entity<SensorData>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.CellId, s.Timestamp }).IsDescending(false, true);
            e.Property(s => s.Voltage).HasColumnType("decimal(6,3)");
        });

        modelBuilder.Entity<AluminaConcentration>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.CellId, a.Timestamp }).IsDescending(false, true);
        });

        modelBuilder.Entity<FeedingRecord>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.CellId, f.Timestamp }).IsDescending(false, true);
        });

        modelBuilder.Entity<AnodeEffectPrediction>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.CellId, a.Timestamp }).IsDescending(false, true);
        });

        modelBuilder.Entity<Alarm>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.CellId, a.Timestamp }).IsDescending(false, true);
            e.HasIndex(a => new { a.IsResolved, a.Timestamp }).IsDescending(false, true);
        });

        modelBuilder.Entity<ConcentrationAlarmTracker>(e =>
        {
            e.HasKey(t => t.CellId);
        });
    }
}
