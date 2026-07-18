using Microsoft.EntityFrameworkCore;
using NexusApp.Models;

namespace NexusApp.Data;

/// <summary>
/// Entity Framework Core DbContext for the trading application
/// </summary>
public class TradingDbContext : DbContext
{
    public TradingDbContext(DbContextOptions<TradingDbContext> options) : base(options)
    {
    }

    public DbSet<TradingSignal> TradingSignals { get; set; }
    public DbSet<Order> Orders { get; set; }
    public DbSet<Position> Positions { get; set; }
    public DbSet<TradingAccount> TradingAccounts { get; set; }
    public DbSet<ApplicationSetting> ApplicationSettings { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // TradingSignal configuration
        modelBuilder.Entity<TradingSignal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OriginalMessage).HasMaxLength(4000);
            entity.Property(e => e.Index).HasMaxLength(50);
            entity.Property(e => e.Symbol).HasMaxLength(50);
            entity.HasIndex(e => e.TelegramMessageId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedTimestamp);
        });

        // Order configuration
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Symbol).HasMaxLength(50);
            entity.Property(e => e.BrokerId).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.Signal)
                .WithMany(s => s.Orders)
                .HasForeignKey(e => e.SignalId)
                .HasConstraintName("FK_Order_Signal")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.TradingAccount)
                .WithMany(a => a.Orders)
                .HasForeignKey(e => e.TradingAccountId)
                .HasConstraintName("FK_Order_Account")
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Position configuration
        modelBuilder.Entity<Position>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Symbol).HasMaxLength(50);
            entity.HasIndex(e => e.TradingAccountId);
            entity.HasIndex(e => e.OpenedAt);
            entity.HasOne(e => e.Signal)
                .WithMany()
                .HasForeignKey(e => e.SignalId)
                .HasConstraintName("FK_Position_Signal")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.TradingAccount)
                .WithMany(a => a.Positions)
                .HasForeignKey(e => e.TradingAccountId)
                .HasConstraintName("FK_Position_Account")
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TradingAccount configuration
        modelBuilder.Entity<TradingAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.BrokerType).HasMaxLength(50);
            entity.Property(e => e.ClientId).HasMaxLength(100);
            entity.HasIndex(e => e.ClientId).IsUnique();
            entity.HasIndex(e => e.IsEnabled);
        });

        // ApplicationSetting configuration
        modelBuilder.Entity<ApplicationSetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(100);
            entity.Property(e => e.Value).HasMaxLength(4000);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.HasIndex(e => e.Key).IsUnique();
        });

        // AuditLog configuration
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasMaxLength(100);
            entity.Property(e => e.EntityType).HasMaxLength(100);
            entity.Property(e => e.Action).HasMaxLength(100);
            entity.Property(e => e.OldValue).HasMaxLength(1000);
            entity.Property(e => e.NewValue).HasMaxLength(1000);
            entity.Property(e => e.Details).HasMaxLength(2000);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.EventType);
        });

        // Seed default data
        SeedDefaultData(modelBuilder);
    }

    // Seeds are performed at startup by DbSeeder in Program.cs to avoid the
    // non-deterministic values (DateTime.UtcNow, entity IDs) that EF Core
    // rejects in HasData model seeding.
    private static void SeedDefaultData(ModelBuilder modelBuilder)
    {
    }
}
