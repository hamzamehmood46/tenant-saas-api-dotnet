using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using TenantSaas.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TenantRateLimiter>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=tenantsaas.db"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Tenant SaaS API",
        Version = "v1",
        Description = "A multi-tenant API. Press Authorize and paste an API key; every request is then scoped to that key's tenant."
    });
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "Your tenant API key."
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = []
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    DemoData.Initialize(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDb>>(), app.Configuration.GetValue<bool>("Demo:Seed"), app.Services.GetRequiredService<TimeProvider>());
}

app.UseSwagger();
app.UseSwaggerUI();

// Who is calling? Resolve the API key to a tenant, then apply that tenant's rate limit.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var key = ctx.Request.Headers["X-Api-Key"].ToString();
    var db = ctx.RequestServices.GetRequiredService<AppDb>();
    var hash = KeyHasher.Hash(key);
    var apiKey = string.IsNullOrWhiteSpace(key)
        ? null
        : await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash);
    if (apiKey is null || apiKey.Revoked)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing, unknown or revoked API key." });
        return;
    }

    var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == apiKey.TenantId);
    var tenantContext = ctx.RequestServices.GetRequiredService<TenantContext>();
    tenantContext.TenantId = tenant.Id;
    tenantContext.KeyId = apiKey.Id;
    tenantContext.Role = apiKey.Role;
    tenantContext.Plan = tenant.Plan;

    var verdict = ctx.RequestServices.GetRequiredService<TenantRateLimiter>().Take(tenant.Id, tenant.Plan);
    ctx.Response.Headers["X-RateLimit-Limit"] = verdict.Limit.ToString();
    ctx.Response.Headers["X-RateLimit-Remaining"] = verdict.Remaining.ToString();
    if (!verdict.Allowed)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = verdict.RetryAfterSeconds.ToString();
        await ctx.Response.WriteAsJsonAsync(new { error = $"Rate limit of {verdict.Limit} requests a minute reached for your tenant." });
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapApi();

app.Run();

public partial class Program;
