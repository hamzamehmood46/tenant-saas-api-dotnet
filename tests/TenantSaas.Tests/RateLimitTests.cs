using System.Net;
using TenantSaas.Api;

namespace TenantSaas.Tests;

public class RateLimitTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static async Task<HttpStatusCode> Hit(HttpClient c) => (await c.GetAsync("/api/me")).StatusCode;

    [Fact]
    public async Task A_free_tenant_gets_twenty_requests_a_minute_then_429_with_retry_after()
    {
        var acme = _app.CreateTenant("acme", Plan.Free);
        var client = _app.As(acme.AdminKey);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await Hit(client));
        }

        var blocked = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retry));
        Assert.InRange(int.Parse(retry!.Single()), 1, 60);
    }

    [Fact]
    public async Task The_allowance_comes_back_in_the_next_minute()
    {
        var acme = _app.CreateTenant("acme", Plan.Free);
        var client = _app.As(acme.AdminKey);
        for (var i = 0; i < 21; i++)
        {
            await Hit(client);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(client));
        _app.Clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.OK, await Hit(client));
    }

    [Fact]
    public async Task A_pro_tenant_gets_a_bigger_allowance()
    {
        var globex = _app.CreateTenant("globex", Plan.Pro);
        var client = _app.As(globex.AdminKey);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await Hit(client));
        }

        var last = await client.GetAsync("/api/me");
        Assert.Equal("200", last.Headers.GetValues("X-RateLimit-Limit").Single());
    }

    [Fact]
    public async Task One_tenant_using_up_its_limit_does_not_affect_another()
    {
        var noisy = _app.CreateTenant("noisy", Plan.Free);
        var quiet = _app.CreateTenant("quiet", Plan.Free);
        var noisyClient = _app.As(noisy.AdminKey);
        for (var i = 0; i < 25; i++)
        {
            await Hit(noisyClient);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(noisyClient));
        Assert.Equal(HttpStatusCode.OK, await Hit(_app.As(quiet.AdminKey)));
    }

    [Fact]
    public async Task The_limit_is_per_tenant_so_extra_keys_do_not_add_capacity()
    {
        var acme = _app.CreateTenant("acme", Plan.Free);
        var admin = _app.As(acme.AdminKey);
        var member = _app.As(acme.MemberKey);

        for (var i = 0; i < 10; i++)
        {
            await Hit(admin);
            await Hit(member);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(admin));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Hit(member));
    }

    [Fact]
    public async Task Rejected_requests_without_a_key_do_not_use_up_a_tenants_allowance()
    {
        var acme = _app.CreateTenant("acme", Plan.Free);
        for (var i = 0; i < 30; i++)
        {
            await Hit(_app.As("wrong-key"));
        }

        Assert.Equal(HttpStatusCode.OK, await Hit(_app.As(acme.AdminKey)));
    }

    [Fact]
    public async Task Parallel_requests_never_get_more_than_the_limit_through()
    {
        var acme = _app.CreateTenant("acme", Plan.Free);
        var client = _app.As(acme.AdminKey);

        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Hit(client)));

        Assert.Equal(20, results.Count(r => r == HttpStatusCode.OK));
        Assert.Equal(40, results.Count(r => r == HttpStatusCode.TooManyRequests));
    }
}
