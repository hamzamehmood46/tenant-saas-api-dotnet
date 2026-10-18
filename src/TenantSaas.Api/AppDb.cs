using Microsoft.EntityFrameworkCore;

namespace TenantSaas.Api;

/// <summary>
/// Every tenant-owned table is filtered by the caller's tenant, so a query cannot return another tenant's rows
/// even if an endpoint forgets to filter. With no tenant resolved the filter matches nothing (fail closed).
/// Writes are checked too: a row can only be created for, or changed within, the caller's own tenant.
/// </summary>
public class AppDb : DbContext
{
    private readonly TenantContext _tenant;

    public AppDb(DbContextOptions<AppDb> options, TenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    // Read by the query filters; EF re-evaluates it per context instance.
    private Guid CurrentTenantId => _tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ApiKey>().HasIndex(k => k.KeyHash).IsUnique();
        b.Entity<ApiKey>().HasQueryFilter(k => k.TenantId == CurrentTenantId);
        b.Entity<Project>().HasQueryFilter(p => p.TenantId == CurrentTenantId);
        b.Entity<TaskItem>().HasQueryFilter(t => t.TenantId == CurrentTenantId);
        b.Entity<AuditEntry>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (_tenant.TenantId == Guid.Empty)
                {
                    throw new InvalidOperationException("No tenant resolved: refusing to write a tenant-owned row.");
                }

                if (entry.Entity.TenantId == Guid.Empty)
                {
                    entry.Entity.TenantId = _tenant.TenantId;
                }
                else if (entry.Entity.TenantId != _tenant.TenantId)
                {
                    throw new InvalidOperationException("Cannot create a row for another tenant.");
                }
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                var original = entry.OriginalValues.GetValue<Guid>(nameof(ITenantOwned.TenantId));
                if (original != _tenant.TenantId || entry.Entity.TenantId != original)
                {
                    throw new InvalidOperationException("Cannot change or delete another tenant's row.");
                }
            }
        }
    }
}
