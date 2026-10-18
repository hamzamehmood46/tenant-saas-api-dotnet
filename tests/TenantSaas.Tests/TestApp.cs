using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantSaas.Api;

namespace TenantSaas.Tests;

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed record TenantSeed(Guid Id, string AdminKey, string MemberKey, string ReadOnlyKey);

/// <summary>The real app on a throwaway database with a movable clock.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tenantsaas-{Guid.NewGuid():N}.db");

    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }

    public AppDb DbFor(Guid tenantId, Role role = Role.Admin)
    {
        var options = Services.CreateScope().ServiceProvider.GetRequiredService<DbContextOptions<AppDb>>();
        return new AppDb(options, new TenantContext { TenantId = tenantId, Role = role });
    }

    public TenantSeed CreateTenant(string name, Plan plan = Plan.Pro)
    {
        _ = Server; // make sure the app has started and the schema exists
        var options = Services.CreateScope().ServiceProvider.GetRequiredService<DbContextOptions<AppDb>>();
        var tenant = new Tenant { Name = name, Plan = plan };
        using (var setup = new AppDb(options, new TenantContext()))
        {
            setup.Tenants.Add(tenant);
            setup.SaveChanges();
        }

        string Key(Role role) => $"{name}-{role}-{Guid.NewGuid():N}";
        var seed = new TenantSeed(tenant.Id, Key(Role.Admin), Key(Role.Member), Key(Role.ReadOnly));
        using var db = new AppDb(options, new TenantContext { TenantId = tenant.Id, Role = Role.Admin });
        db.ApiKeys.Add(new ApiKey { Label = "admin", Prefix = "x", KeyHash = KeyHasher.Hash(seed.AdminKey), Role = Role.Admin });
        db.ApiKeys.Add(new ApiKey { Label = "member", Prefix = "x", KeyHash = KeyHasher.Hash(seed.MemberKey), Role = Role.Member });
        db.ApiKeys.Add(new ApiKey { Label = "readonly", Prefix = "x", KeyHash = KeyHasher.Hash(seed.ReadOnlyKey), Role = Role.ReadOnly });
        db.SaveChanges();
        return seed;
    }

    public HttpClient As(string? key)
    {
        var client = CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public static class Http
{
    public static async Task<Guid> CreateProject(this HttpClient c, string name)
    {
        var response = await c.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!.Id;
    }

    public static async Task<Guid> CreateTask(this HttpClient c, Guid projectId, string title)
    {
        var response = await c.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskDto>())!.Id;
    }
}
