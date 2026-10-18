using Microsoft.EntityFrameworkCore;
using TenantSaas.Api;

namespace TenantSaas.Tests;

/// <summary>The database layer must hold the line even if an endpoint is written carelessly.</summary>
public class DataLayerTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly TenantSeed _acme;
    private readonly TenantSeed _globex;

    public DataLayerTests()
    {
        _acme = _app.CreateTenant("acme");
        _globex = _app.CreateTenant("globex");
        using var db = _app.DbFor(_acme.Id);
        db.Projects.Add(new Project { Name = "Acme project" });
        db.SaveChanges();
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_plain_query_only_returns_the_current_tenants_rows()
    {
        using var db = _app.DbFor(_globex.Id);
        db.Projects.Add(new Project { Name = "Globex project" });
        await db.SaveChangesAsync();

        Assert.Equal(["Globex project"], await db.Projects.Select(p => p.Name).ToListAsync());
    }

    [Fact]
    public async Task With_no_tenant_resolved_queries_return_nothing()
    {
        using var db = _app.DbFor(Guid.Empty);

        Assert.Empty(await db.Projects.ToListAsync());
        Assert.Empty(await db.ApiKeys.ToListAsync());
    }

    [Fact]
    public async Task With_no_tenant_resolved_writes_are_refused()
    {
        using var db = _app.DbFor(Guid.Empty);
        db.Projects.Add(new Project { Name = "Orphan" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_row_cannot_be_created_for_another_tenant()
    {
        using var db = _app.DbFor(_acme.Id);
        db.Projects.Add(new Project { Name = "Planted", TenantId = _globex.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_row_cannot_be_moved_to_another_tenant()
    {
        using var db = _app.DbFor(_acme.Id);
        var project = await db.Projects.SingleAsync();
        project.TenantId = _globex.Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Ignoring_the_filter_to_load_another_tenants_row_still_cannot_change_it()
    {
        using var other = _app.DbFor(_globex.Id);
        other.Projects.Add(new Project { Name = "Globex project" });
        await other.SaveChangesAsync();

        using var db = _app.DbFor(_acme.Id);
        var theirs = await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Name == "Globex project");
        theirs.Name = "Hijacked";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
