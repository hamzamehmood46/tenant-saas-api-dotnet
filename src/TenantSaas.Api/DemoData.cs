using Microsoft.EntityFrameworkCore;

namespace TenantSaas.Api;

public static class DemoData
{
    // Public demo keys, listed in the README. They only exist when Demo:Seed is on.
    public const string AcmeAdmin = "demo-acme-admin";
    public const string AcmeReadOnly = "demo-acme-readonly";
    public const string GlobexAdmin = "demo-globex-admin";

    public static void Initialize(DbContextOptions<AppDb> options, bool seed, TimeProvider time)
    {
        using (var setup = new AppDb(options, new TenantContext()))
        {
            setup.Database.EnsureCreated();
            if (!seed || setup.Tenants.Any())
            {
                return;
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        Seed(options, "Acme Corp", Plan.Free, now,
            [(AcmeAdmin, "Acme admin", Role.Admin), (AcmeReadOnly, "Acme read-only", Role.ReadOnly)],
            ["Website redesign", "Q4 invoicing"]);
        Seed(options, "Globex", Plan.Pro, now,
            [(GlobexAdmin, "Globex admin", Role.Admin)],
            ["Warehouse migration", "Partner onboarding"]);
    }

    private static void Seed(DbContextOptions<AppDb> options, string name, Plan plan, DateTime now, (string Key, string Label, Role Role)[] keys, string[] projects)
    {
        var tenant = new Tenant { Name = name, Plan = plan };
        using (var setup = new AppDb(options, new TenantContext()))
        {
            setup.Tenants.Add(tenant);
            setup.SaveChanges();
        }

        // Rows are written through a context scoped to the tenant, the same path the API uses.
        using var db = new AppDb(options, new TenantContext { TenantId = tenant.Id, Plan = plan, Role = Role.Admin });
        foreach (var (key, label, role) in keys)
        {
            db.ApiKeys.Add(new ApiKey { Label = label, Prefix = key[..8], KeyHash = KeyHasher.Hash(key), Role = role });
        }

        foreach (var projectName in projects)
        {
            var project = new Project { Name = projectName, CreatedAt = now };
            db.Projects.Add(project);
            db.Tasks.Add(new TaskItem { ProjectId = project.Id, Title = "Kick-off meeting", CreatedAt = now });
            db.Tasks.Add(new TaskItem { ProjectId = project.Id, Title = "Write the plan", CreatedAt = now.AddSeconds(1) });
        }

        db.SaveChanges();
    }
}
