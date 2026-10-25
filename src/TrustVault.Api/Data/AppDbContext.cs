
using Microsoft.EntityFrameworkCore;

namespace TrustVault.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
    public DbSet<DocumentGrant> DocumentGrants => Set<DocumentGrant>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserAccount>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<DocumentRecord>()
            .HasIndex(d => d.StorageKey)
            .IsUnique();

        modelBuilder.Entity<DocumentGrant>()
            .HasIndex(g => new { g.DocumentId, g.UserId })
            .IsUnique();

        modelBuilder.Entity<DocumentRecord>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(d => d.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DocumentGrant>()
            .HasOne<DocumentRecord>()
            .WithMany()
            .HasForeignKey(g => g.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DocumentGrant>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(g => g.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AuditEvent>()
            .HasOne<UserAccount>()
            .WithMany()
            .HasForeignKey(a => a.ActorId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AuditEvent>()
            .HasOne<DocumentRecord>()
            .WithMany()
            .HasForeignKey(a => a.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class UserAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Owner";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class DocumentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string Category { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Recycle Bin support
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAtUtc { get; set; } = null;
}

public class DocumentGrant
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DocumentId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Permission { get; set; } = "Read";
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAtUtc { get; set; }
}

public class AuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ActorId { get; set; }
    public string? DocumentId { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? RequestId { get; set; }
    public string? Details { get; set; }
}
