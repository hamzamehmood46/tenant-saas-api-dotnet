# Tenant SaaS API: many customers, one database, no leaks

[![CI](https://github.com/hamzamehmood46/tenant-saas-api-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/hamzamehmood46/tenant-saas-api-dotnet/actions/workflows/ci.yml)

**Live demo: [tenant-saas-api-dotnet.onrender.com/swagger](https://tenant-saas-api-dotnet.onrender.com/swagger)** (Swagger UI). Hosted on Render's free tier, so the first load after idle can take about a minute, and demo data resets on restart.

A multi-tenant API in **.NET 8 / ASP.NET Core**: several customers (tenants) share one database and one deployment, and none of them can ever see, change or even detect another's data. It is the shape of most real SaaS backends, built around the hard part: making isolation a property of the system instead of something every endpoint has to remember.

## Try it (2 minutes)

The demo has two tenants. These are public demo keys:

| Key | Tenant | Plan | Role |
|---|---|---|---|
| `demo-acme-admin` | Acme Corp | Free (20 requests a minute) | Admin |
| `demo-acme-readonly` | Acme Corp | Free | Read-only |
| `demo-globex-admin` | Globex | Pro (200 requests a minute) | Admin |

1. Open the Swagger page, press **Authorize**, paste `demo-acme-admin`, and call `GET /api/projects`. You see Acme's projects only.
2. Authorize as `demo-globex-admin` and list again: a completely different set.
3. Copy a project id from Globex, switch back to Acme, and call `GET /api/projects/{id}`. It returns **404**, not 403: Acme cannot even tell that the project exists.
4. Try `POST /api/projects` with a `tenantId` of Globex's in the body. It is ignored; the project is created under Acme.
5. Authorize as `demo-acme-readonly` and try to create something: **403**.
6. As Acme admin, press Execute on `GET /api/me` more than 20 times in a minute: **429** with a `Retry-After` header. Globex is unaffected.
7. `POST /api/keys` to mint a new key. It is shown once and stored only as a hash; `DELETE /api/keys/{id}` revokes it and the next call with it is a 401.

## How isolation works

| Layer | What it does |
|---|---|
| **Authentication** | The `X-Api-Key` header is hashed (SHA-256) and looked up. The key decides the tenant, role and plan; nothing in the URL or body can. |
| **Query filters** | Every tenant-owned table (projects, tasks, keys, audit log) has an EF Core global filter on the caller's tenant, so a forgotten `Where` in an endpoint still cannot leak rows. With no tenant resolved, the filter matches nothing (fail closed). |
| **Write guard** | `SaveChanges` refuses to create a row for another tenant, move a row between tenants, or change or delete a row that is not the caller's, even if the filter was bypassed with `IgnoreQueryFilters`. |
| **404 instead of 403** | Another tenant's id behaves exactly like an id that does not exist, so ids cannot be probed. |
| **Roles** | Read-only can read, Member can create and edit, Admin can also delete, manage keys and read the audit log. |
| **Per-tenant rate limits** | Counted per tenant (not per key), so extra keys add no capacity and one noisy tenant cannot starve another. Free and Pro plans have different limits. A movable clock makes it testable. |
| **Keys** | Random, shown once, stored only as a hash, revocable. |
| **Audit log** | Every write records which key did what, scoped to the tenant. |

## Tests

```bash
dotnet test
```

28 tests against the real app on a throwaway database, including:

- tenant A cannot read, rename, delete, or add tasks under tenant B's projects, tasks or keys, and B's data is unchanged afterwards;
- a `tenantId` in the request body is ignored;
- the database layer holds the line on its own: queries return only the current tenant's rows, nothing at all with no tenant resolved, and writes across tenants are refused even with filters bypassed;
- 60 parallel writes from two tenants never mix, and 60 parallel requests let exactly 20 through a 20-a-minute limit;
- rate limits: per-tenant, plan-specific, reset on the next minute, `Retry-After` set, unaffected by other tenants, not consumed by requests with bad keys;
- roles are enforced on every kind of write, and keys are stored only as hashes and stop working when revoked.

I also broke the rules on purpose to check the tests notice: removing the project query filter, the cross-tenant change guard, the role check, the rate-limit window, per-tenant counting, and key hashing. Each made tests fail.

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/TenantSaas.Api
```

Seed the demo tenants and keys with `Demo__Seed=true`, or create your own through `DemoData`.

## Scope, honestly

- SQLite with a shared schema and a `TenantId` column. At larger scale you would consider a schema or database per tenant for the biggest customers; the filters here are the same idea either way.
- API keys only, no user accounts, OAuth or billing. The rate limiter is in memory, so it is per instance; a real deployment with several instances would use a shared store such as Redis.
- Hosted on a free tier that sleeps, with demo data that resets.

## Related projects

- [Claims API](https://github.com/hamzamehmood46/claims-api-dotnet): JWT and role-based workflow in Clean Architecture
- [OrderFlow](https://github.com/hamzamehmood46/orderflow-dotnet): idempotent APIs and retries
- [API Rate Limiter](https://github.com/hamzamehmood46/api-rate-limiter-dotnet): token bucket and sliding window

## License

MIT
