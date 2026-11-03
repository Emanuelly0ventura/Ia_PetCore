using Microsoft.EntityFrameworkCore;
using PetCore.Api.Models;

namespace PetCore.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>().HasIndex(x => x.Identifier).IsUnique();
        b.Entity<Measurement>().HasIndex(x => x.Fingerprint).IsUnique();
        b.Entity<Measurement>().HasIndex(x => new { x.DeviceId, x.ReceivedAt });
        b.Entity<Measurement>().Property(x => x.OriginalData).HasColumnType("jsonb");
        b.Entity<AiModel>().Property(x => x.Parameters).HasColumnType("jsonb");
        b.Entity<AiAnalysis>().HasIndex(x => x.MeasurementId).IsUnique();
        b.Entity<AiAnalysis>().HasIndex(x => x.Result);
        b.Entity<Alert>().HasIndex(x => new { x.AnalysisId, x.MeasurementId }).IsUnique();
        b.Entity<AiAnalysis>().HasOne(x => x.Measurement).WithOne(x => x.Analysis).HasForeignKey<AiAnalysis>(x => x.MeasurementId);
        b.Entity<Alert>().HasOne(x => x.Measurement).WithMany().HasForeignKey(x => x.MeasurementId);
        b.Entity<Alert>().HasOne(x => x.Analysis).WithMany().HasForeignKey(x => x.AnalysisId);
    }
}
