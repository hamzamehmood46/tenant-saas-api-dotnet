using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantSaas.Api;

namespace TenantSaas.Tests;

public class AccessTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly TenantSeed _acme;

    public AccessTests()
    {
        _acme = _app.CreateTenant("acme");
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Requests_without_a_valid_key_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.As(null).GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.As("not-a-real-key").GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.As("").GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task A_read_only_key_can_read_but_not_write()
    {
        var readOnly = _app.As(_acme.ReadOnlyKey);

        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync("/api/projects", new { name = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task A_member_can_write_but_only_an_admin_can_delete_or_manage_keys()
    {
        var member = _app.As(_acme.MemberKey);
        var project = await member.CreateProject("Roadmap");
        var task = await member.CreateTask(project, "Draft");

        Assert.Equal(HttpStatusCode.OK, (await member.PostAsync($"/api/tasks/{task}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/tasks/{task}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/projects/{project}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/audit")).StatusCode);

        var admin = _app.As(_acme.AdminKey);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/projects/{project}")).StatusCode);
    }

    [Fact]
    public async Task A_new_key_works_is_stored_only_as_a_hash_and_stops_working_when_revoked()
    {
        var admin = _app.As(_acme.AdminKey);
        var created = await admin.PostAsJsonAsync("/api/keys", new { label = "ci", role = "Member" });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var plaintext = body.GetProperty("key").GetString()!;
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.As(plaintext).GetAsync("/api/me")).StatusCode);
        using (var db = _app.DbFor(_acme.Id))
        {
            var stored = await db.ApiKeys.SingleAsync(k => k.Id == id);
            Assert.NotEqual(plaintext, stored.KeyHash);
            Assert.DoesNotContain(plaintext, stored.KeyHash);
            Assert.Equal(KeyHasher.Hash(plaintext), stored.KeyHash);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.As(plaintext).GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Invalid_names_are_rejected()
    {
        var admin = _app.As(_acme.AdminKey);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/projects", new { name = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/projects", new { name = new string('x', 101) })).StatusCode);
    }

    [Fact]
    public async Task The_audit_log_records_who_did_what()
    {
        var member = _app.As(_acme.MemberKey);
        var project = await member.CreateProject("Roadmap");
        _app.Clock.Advance(TimeSpan.FromSeconds(5));
        await member.PutAsJsonAsync($"/api/projects/{project}", new { name = "Roadmap v2" });

        var audit = await _app.As(_acme.AdminKey).GetFromJsonAsync<List<JsonElement>>("/api/audit");

        Assert.Equal(["rename", "create"], audit!.Select(a => a.GetProperty("action").GetString()));
    }
}
