using Microsoft.EntityFrameworkCore;
using PirateChess.Api.Models.Entities;

namespace PirateChess.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ChessableCredential> ChessableCredentials => Set<ChessableCredential>();
    public DbSet<CachedCourse> CachedCourses => Set<CachedCourse>();
    public DbSet<GeneratedPgn> GeneratedPgns => Set<GeneratedPgn>();
    public DbSet<ExportHistory> ExportHistories => Set<ExportHistory>();
    public DbSet<ChessableRawResponse> ChessableRawResponses => Set<ChessableRawResponse>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<ChessableCredential>(e =>
        {
            e.HasOne(c => c.User)
                .WithMany(u => u.ChessableCredentials)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CachedCourse>(e =>
        {
            e.Property(c => c.RestResponseJson).HasColumnType("LONGTEXT");
            e.HasOne(c => c.User)
                .WithMany(u => u.CachedCourses)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GeneratedPgn>(e =>
        {
            e.Property(p => p.PgnContent).HasColumnType("LONGTEXT");
            e.HasOne(p => p.CachedCourse)
                .WithMany(c => c.GeneratedPgns)
                .HasForeignKey(p => p.CachedCourseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.User)
                .WithMany(u => u.GeneratedPgns)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExportHistory>(e =>
        {
            e.HasOne(h => h.User)
                .WithMany(u => u.ExportHistories)
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChessableRawResponse>(e =>
        {
            e.Property(r => r.RawJson).HasColumnType("LONGTEXT");
            e.Property(r => r.Endpoint).HasMaxLength(50);
            e.Property(r => r.Url).HasMaxLength(500);
            e.Property(r => r.ChessableUid).HasMaxLength(50);
            e.HasIndex(r => new { r.ChessableUid, r.RequestedAt });
            e.HasIndex(r => r.Endpoint);
        });
    }
}
