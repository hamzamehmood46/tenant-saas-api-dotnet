using Microsoft.EntityFrameworkCore;

namespace TenantSaas.Api;

public record NameRequest(string? Name);
public record TitleRequest(string? Title);
public record NewKeyRequest(string? Label, Role Role);

public record ProjectDto(Guid Id, string Name, DateTime CreatedAt);
public record TaskDto(Guid Id, Guid ProjectId, string Title, bool Done);
public record KeyDto(Guid Id, string Label, string Prefix, Role Role, bool Revoked);

public static class Endpoints
{
    private static IResult? Need(TenantContext t, Role minimum) =>
        t.Role >= minimum ? null : Results.Json(new { error = $"This needs the {minimum} role; your key is {t.Role}." }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult? BadText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 100
            ? Results.BadRequest(new { error = $"{field} is required and at most 100 characters." })
            : null;

    private static void Audit(AppDb db, TenantContext t, TimeProvider time, string action, string entity, Guid id) =>
        db.Audit.Add(new AuditEntry { KeyId = t.KeyId, Action = action, Entity = entity, EntityId = id, At = time.GetUtcNow().UtcDateTime });

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/me", (TenantContext t) => new { tenantId = t.TenantId, plan = t.Plan.ToString(), role = t.Role.ToString(), requestsPerMinute = PlanLimits.RequestsPerMinute(t.Plan) });

        // Projects
        api.MapGet("/projects", async (AppDb db) =>
            await db.Projects.OrderBy(p => p.Name).Take(100).Select(p => new ProjectDto(p.Id, p.Name, p.CreatedAt)).ToListAsync());

        api.MapGet("/projects/{id:guid}", async (Guid id, AppDb db) =>
            await db.Projects.Where(p => p.Id == id).Select(p => new ProjectDto(p.Id, p.Name, p.CreatedAt)).FirstOrDefaultAsync() is { } dto
                ? Results.Ok(dto)
                : Results.NotFound());

        api.MapPost("/projects", async (NameRequest body, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Member) is { } denied) return denied;
            if (BadText(body.Name, "Name") is { } bad) return bad;
            var project = new Project { Name = body.Name!.Trim(), CreatedAt = time.GetUtcNow().UtcDateTime };
            db.Projects.Add(project);
            Audit(db, t, time, "create", nameof(Project), project.Id);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{project.Id}", new ProjectDto(project.Id, project.Name, project.CreatedAt));
        });

        api.MapPut("/projects/{id:guid}", async (Guid id, NameRequest body, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Member) is { } denied) return denied;
            if (BadText(body.Name, "Name") is { } bad) return bad;
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();
            project.Name = body.Name!.Trim();
            Audit(db, t, time, "rename", nameof(Project), id);
            await db.SaveChangesAsync();
            return Results.Ok(new ProjectDto(project.Id, project.Name, project.CreatedAt));
        });

        api.MapDelete("/projects/{id:guid}", async (Guid id, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Admin) is { } denied) return denied;
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();
            db.Tasks.RemoveRange(await db.Tasks.Where(x => x.ProjectId == id).ToListAsync());
            db.Projects.Remove(project);
            Audit(db, t, time, "delete", nameof(Project), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Tasks
        api.MapGet("/projects/{id:guid}/tasks", async (Guid id, AppDb db) =>
            await db.Projects.AnyAsync(p => p.Id == id)
                ? Results.Ok(await db.Tasks.Where(x => x.ProjectId == id).OrderBy(x => x.CreatedAt).Take(100).Select(x => new TaskDto(x.Id, x.ProjectId, x.Title, x.Done)).ToListAsync())
                : Results.NotFound());

        api.MapPost("/projects/{id:guid}/tasks", async (Guid id, TitleRequest body, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Member) is { } denied) return denied;
            if (BadText(body.Title, "Title") is { } bad) return bad;
            if (!await db.Projects.AnyAsync(p => p.Id == id)) return Results.NotFound();
            var task = new TaskItem { ProjectId = id, Title = body.Title!.Trim(), CreatedAt = time.GetUtcNow().UtcDateTime };
            db.Tasks.Add(task);
            Audit(db, t, time, "create", nameof(TaskItem), task.Id);
            await db.SaveChangesAsync();
            return Results.Created($"/api/tasks/{task.Id}", new TaskDto(task.Id, task.ProjectId, task.Title, task.Done));
        });

        api.MapPost("/tasks/{id:guid}/complete", async (Guid id, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Member) is { } denied) return denied;
            var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
            if (task is null) return Results.NotFound();
            task.Done = true;
            Audit(db, t, time, "complete", nameof(TaskItem), id);
            await db.SaveChangesAsync();
            return Results.Ok(new TaskDto(task.Id, task.ProjectId, task.Title, task.Done));
        });

        api.MapDelete("/tasks/{id:guid}", async (Guid id, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Admin) is { } denied) return denied;
            var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
            if (task is null) return Results.NotFound();
            db.Tasks.Remove(task);
            Audit(db, t, time, "delete", nameof(TaskItem), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // API keys (admin only). The plaintext key is returned once, at creation; only its hash is stored.
        api.MapGet("/keys", async (AppDb db, TenantContext t) =>
            Need(t, Role.Admin) ?? Results.Ok(await db.ApiKeys.OrderBy(k => k.Label).Select(k => new KeyDto(k.Id, k.Label, k.Prefix, k.Role, k.Revoked)).ToListAsync()));

        api.MapPost("/keys", async (NewKeyRequest body, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Admin) is { } denied) return denied;
            if (BadText(body.Label, "Label") is { } bad) return bad;
            var plaintext = KeyHasher.NewKey();
            var key = new ApiKey { Label = body.Label!.Trim(), Prefix = plaintext[..8], KeyHash = KeyHasher.Hash(plaintext), Role = body.Role };
            db.ApiKeys.Add(key);
            Audit(db, t, time, "create", nameof(ApiKey), key.Id);
            await db.SaveChangesAsync();
            return Results.Created($"/api/keys/{key.Id}", new { id = key.Id, key = plaintext, role = key.Role.ToString(), note = "Copy this key now. It is not stored and cannot be shown again." });
        });

        api.MapDelete("/keys/{id:guid}", async (Guid id, AppDb db, TenantContext t, TimeProvider time) =>
        {
            if (Need(t, Role.Admin) is { } denied) return denied;
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id);
            if (key is null) return Results.NotFound();
            key.Revoked = true;
            Audit(db, t, time, "revoke", nameof(ApiKey), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapGet("/audit", async (AppDb db, TenantContext t) =>
            Need(t, Role.Admin) ?? Results.Ok(await db.Audit.OrderByDescending(a => a.At).Take(100).Select(a => new { a.Action, a.Entity, a.EntityId, a.KeyId, a.At }).ToListAsync()));
    }
}
