using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TenantSaas.Api;

namespace TenantSaas.Tests;

public class IsolationTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly TenantSeed _acme;
    private readonly TenantSeed _globex;

    public IsolationTests()
    {
        _acme = _app.CreateTenant("acme");
        _globex = _app.CreateTenant("globex");
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_list_only_contains_the_callers_own_projects()
    {
        var acme = _app.As(_acme.AdminKey);
        var globex = _app.As(_globex.AdminKey);
        await acme.CreateProject("Acme secret plan");
        await globex.CreateProject("Globex secret plan");

        var seen = await acme.GetFromJsonAsync<List<ProjectDto>>("/api/projects");

        Assert.Equal(["Acme secret plan"], seen!.Select(p => p.Name));
    }

    [Fact]
    public async Task Another_tenants_project_looks_like_it_does_not_exist()
    {
        var theirs = await _app.As(_globex.AdminKey).CreateProject("Globex only");
        var acme = _app.As(_acme.AdminKey);

        Assert.Equal(HttpStatusCode.NotFound, (await acme.GetAsync($"/api/projects/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acme.PutAsJsonAsync($"/api/projects/{theirs}", new { name = "Hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acme.DeleteAsync($"/api/projects/{theirs}")).StatusCode);

        var still = await _app.As(_globex.AdminKey).GetFromJsonAsync<ProjectDto>($"/api/projects/{theirs}");
        Assert.Equal("Globex only", still!.Name);
    }

    [Fact]
    public async Task A_tenant_id_in_the_request_body_is_ignored()
    {
        var acme = _app.As(_acme.AdminKey);

        var response = await acme.PostAsJsonAsync("/api/projects", new { name = "Sneaky", tenantId = _globex.Id });
        var created = await response.Content.ReadFromJsonAsync<ProjectDto>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(await _app.As(_globex.AdminKey).GetFromJsonAsync<List<ProjectDto>>("/api/projects") ?? []);
        Assert.Single(await acme.GetFromJsonAsync<List<ProjectDto>>("/api/projects") ?? []);
        using var db = _app.DbFor(_acme.Id);
        Assert.Equal(_acme.Id, (await db.Projects.SingleAsync(p => p.Id == created!.Id)).TenantId);
    }

    [Fact]
    public async Task Tasks_cannot_be_read_or_added_under_another_tenants_project()
    {
        var theirs = await _app.As(_globex.AdminKey).CreateProject("Globex only");
        await _app.As(_globex.AdminKey).CreateTask(theirs, "Globex task");
        var acme = _app.As(_acme.AdminKey);

        Assert.Equal(HttpStatusCode.NotFound, (await acme.GetAsync($"/api/projects/{theirs}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acme.PostAsJsonAsync($"/api/projects/{theirs}/tasks", new { title = "Planted" })).StatusCode);

        var tasks = await _app.As(_globex.AdminKey).GetFromJsonAsync<List<TaskDto>>($"/api/projects/{theirs}/tasks");
        Assert.Equal(["Globex task"], tasks!.Select(t => t.Title));
    }

    [Fact]
    public async Task Another_tenants_task_cannot_be_completed_or_deleted()
    {
        var project = await _app.As(_globex.AdminKey).CreateProject("Globex only");
        var task = await _app.As(_globex.AdminKey).CreateTask(project, "Globex task");
        var acme = _app.As(_acme.AdminKey);

        Assert.Equal(HttpStatusCode.NotFound, (await acme.PostAsync($"/api/tasks/{task}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acme.DeleteAsync($"/api/tasks/{task}")).StatusCode);

        var tasks = await _app.As(_globex.AdminKey).GetFromJsonAsync<List<TaskDto>>($"/api/projects/{project}/tasks");
        Assert.False(tasks!.Single().Done);
    }

    [Fact]
    public async Task Deleting_a_project_removes_only_its_own_tasks()
    {
        var acme = _app.As(_acme.AdminKey);
        var globex = _app.As(_globex.AdminKey);
        var acmeProject = await acme.CreateProject("Acme");
        await acme.CreateTask(acmeProject, "a1");
        var globexProject = await globex.CreateProject("Globex");
        await globex.CreateTask(globexProject, "g1");

        Assert.Equal(HttpStatusCode.NoContent, (await acme.DeleteAsync($"/api/projects/{acmeProject}")).StatusCode);

        Assert.Single(await globex.GetFromJsonAsync<List<TaskDto>>($"/api/projects/{globexProject}/tasks") ?? []);
    }

    [Fact]
    public async Task The_audit_log_and_key_list_only_show_the_callers_tenant()
    {
        await _app.As(_acme.AdminKey).CreateProject("Acme");
        await _app.As(_globex.AdminKey).CreateProject("Globex");

        var audit = await _app.As(_acme.AdminKey).GetFromJsonAsync<List<AuditRow>>("/api/audit");
        var keys = await _app.As(_acme.AdminKey).GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/keys");

        Assert.Single(audit!);
        Assert.Equal(3, keys!.Count);
        using var db = _app.DbFor(_globex.Id);
        Assert.Equal(3, await db.ApiKeys.CountAsync());
    }

    [Fact]
    public async Task One_tenant_cannot_revoke_another_tenants_key()
    {
        using var db = _app.DbFor(_globex.Id);
        var theirKey = await db.ApiKeys.FirstAsync(k => k.Role == Role.Admin);

        var response = await _app.As(_acme.AdminKey).DeleteAsync($"/api/keys/{theirKey.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.As(_globex.AdminKey).GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Parallel_writes_from_two_tenants_never_mix()
    {
        var acme = _app.As(_acme.AdminKey);
        var globex = _app.As(_globex.AdminKey);

        await Task.WhenAll(Enumerable.Range(0, 30).SelectMany(i => new[]
        {
            acme.CreateProject($"acme-{i}"),
            globex.CreateProject($"globex-{i}"),
        }));

        var acmeNames = (await acme.GetFromJsonAsync<List<ProjectDto>>("/api/projects"))!.Select(p => p.Name).ToList();
        var globexNames = (await globex.GetFromJsonAsync<List<ProjectDto>>("/api/projects"))!.Select(p => p.Name).ToList();
        Assert.Equal(30, acmeNames.Count);
        Assert.Equal(30, globexNames.Count);
        Assert.All(acmeNames, n => Assert.StartsWith("acme-", n));
        Assert.All(globexNames, n => Assert.StartsWith("globex-", n));
    }

    private record AuditRow(string Action, string Entity);
}
