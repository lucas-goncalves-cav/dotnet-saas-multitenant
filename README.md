# SaaS Multi Tenant API

A multi tenant SaaS backend where several companies share one database, one schema and one set of tables, and none of them can reach another's rows.

The interesting part is not that it filters by tenant. It is **where** it filters. Not one service, repository or endpoint in this project mentions `TenantId` in a query. The isolation lives in the persistence layer, so forgetting to write the filter is not a way to leak data, because there is nothing to forget.

[![CI](https://github.com/lucas-goncalves-cav/dotnet-saas-multitenant/actions/workflows/ci.yml/badge.svg)](https://github.com/lucas-goncalves-cav/dotnet-saas-multitenant/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## The problem

In a shared schema SaaS, every table holds every customer's data side by side:

| Id | TenantId | Name |
|----|----------|------|
| 1  | acme     | Wile E. Coyote |
| 2  | globex   | Hank Scorpio |
| 3  | acme     | Road Runner |

One missing `WHERE TenantId = @current` shows Acme's customers to Globex. It is one line, in one query, out of hundreds, written by whoever joined last month.

The usual answer is a code review rule: *always filter by tenant*. That is not a mechanism. It is a hope, checked by humans, on every query, forever.

This project takes a different line: **make the wrong query impossible to write.**

---

## How isolation actually works

Two mechanisms, because one is not enough.

### 1. Reads: a global query filter

Every entity implementing `ITenantOwned` gets a filter built by expression tree at model creation ([AppDbContext.cs](src/Infrastructure/Persistence/AppDbContext.cs)):

```csharp
private void ApplyTenantFilter(ModelBuilder modelBuilder, Type entityType)
{
    var parameter = Expression.Parameter(entityType, "entity");
    var tenantProperty = Expression.Property(parameter, nameof(ITenantOwned.TenantId));

    // Closes over `this`, not over a captured value, so the tenant is read at
    // query time. Capturing the id when the model is built would bake one
    // tenant into the compiled model and hand every request the first tenant
    // the process ever saw.
    var currentTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

    modelBuilder.Entity(entityType)
        .HasQueryFilter(Expression.Lambda(Expression.Equal(tenantProperty, currentTenant), parameter));
}
```

The repository then writes queries that look completely naive:

```csharp
public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
    _context.Customers.FirstOrDefaultAsync(customer => customer.Id == id, cancellationToken);
```

Here is the SQL that query actually produced, read back from `sys.dm_exec_query_stats` on the running SQL Server container:

```sql
(@__ef_filter__CurrentTenantId_0 uniqueidentifier, @__id_0 uniqueidentifier)
SELECT TOP(1) [c].[Id], [c].[Active], [c].[CreatedAt], [c].[Document],
              [c].[Email], [c].[Name], [c].[Phone], [c].[TenantId], [c].[UpdatedAt]
FROM [Customers] AS [c]
WHERE [c].[TenantId] = @__ef_filter__CurrentTenantId_0 AND [c].[Id] = @__id_0
```

The predicate is there, and nobody wrote it. The CI job `isolation-on-sql-server` asserts exactly this, so the claim cannot quietly stop being true.

### 2. Writes: a guard in SaveChanges

A query filter protects reads only. An entity attached by id, or reached through a navigation, never runs a filtered query, so `Update` and `Remove` would go straight through. `SaveChanges` therefore checks every pending change:

```csharp
case EntityState.Modified:
case EntityState.Deleted:
    // The ORIGINAL value, not the current one. Comparing the current value
    // would let an attacker rewrite TenantId and then pass the check.
    var originalTenantId = entry.OriginalValues.GetValue<Guid>(nameof(TenantEntity.TenantId));

    if (tenantId is null || originalTenantId != tenantId.Value)
    {
        throw new CrossTenantAccessException(...);
    }
```

New entities are stamped from the context rather than from anything the caller supplied, and `TenantId` has a private setter that refuses reassignment once set. No request DTO in this project has a tenant field at all, which is the first line of that defence; the stamp is the second.

### 3. The tenant comes from the token, and only the token

```csharp
public Guid? TenantId
{
    get
    {
        var claim = User?.FindFirst(JwtTokenGenerator.TenantIdClaim)?.Value;

        return Guid.TryParse(claim, out var tenantId) ? tenantId : null;
    }
}
```

What [HttpTenantContext](src/Infrastructure/Auth/HttpTenantContext.cs) deliberately does not do is read a tenant id from a route parameter, a query string, a header or the request body. A tenant id the client can influence is a tenant id the client can change. There is no `/api/tenants/{id}/customers` route in this API, because such a route invites exactly that mistake.

### Escape hatches, named so you can find them

Two operations genuinely have to run before a tenant exists: signing in has to find the user before there is a token, and registration writes the first user of a tenant it just created. Both go through names that stand out in a diff, `IgnoringTenantFilter<T>()` and `OverrideTenant(tenantId)`, and both live in a single file each. The escape hatch for reads does not bypass the write guard, which is asserted by a test.

---

## Objective

Show how multi tenancy is done when isolation is a property of the system rather than a habit of its authors, and to show the failure modes honestly: where a query filter is not enough, why a cross tenant read must answer 404 rather than 403, and what plan limits look like when they are business rules instead of configuration.

---

## Technologies

| Area | Choice |
|------|--------|
| Runtime | .NET 9, C# 13 |
| API | ASP.NET Core Minimal APIs |
| Persistence | EF Core 9, SQL Server 2022 |
| Auth | JWT bearer, HS256, PBKDF2 password hashing (210,000 iterations) |
| Docs | Swagger / OpenAPI |
| Logging | Serilog, with the tenant id on every request scoped log line |
| Tests | xUnit, FluentAssertions, `WebApplicationFactory` |
| Infrastructure | Docker, Docker Compose, GitHub Actions |

Redis is listed as optional in the brief and is not used here. Nothing in this design needs a cache to be correct, and adding one would only blur the point the project is making.

---

## Features

- Company registration, creating the tenant and its first administrator in one call
- Sign in scoped to a tenant slug, because an email is unique per tenant rather than globally
- Users, customers, products and orders, all tenant scoped
- Roles: `Viewer`, `Member`, `Admin`, enforced by authorization policies
- Plans (`Free`, `Professional`, `Enterprise`) with per resource limits
- Plan limit responses that say what to do about it, with HTTP 402 rather than 403
- Cross tenant write attempts logged with both tenant ids, for alerting
- Health endpoints split into liveness and readiness
- Swagger UI with bearer authentication wired up

### Plans

| | Free | Professional | Enterprise |
|---|---|---|---|
| Users | 5 | 20 | unlimited |
| Customers | 100 | 5,000 | unlimited |
| Products | 50 | 1,000 | unlimited |
| API access | no | yes | yes |

Limits live in the domain ([Plan.cs](src/Domain/Tenants/Plan.cs)) rather than in configuration, because exceeding one is a business rule the API has to explain to a customer, not a setting an operator tunes. A downgrade below current usage is refused rather than silently deleting the excess: which customers to drop is the tenant's decision, not the platform's.

---

## Architecture

```text
┌──────────────────────────────────────────────────────────────┐
│ Api            Minimal API endpoints, ProblemDetails mapping │
│                Nothing here reads or passes a tenant id      │
├──────────────────────────────────────────────────────────────┤
│ Application    Services, Result pattern, repository          │
│                interfaces. No interface takes a tenant id.   │
├──────────────────────────────────────────────────────────────┤
│ Domain         Entities, plans, invariants. TenantId has a   │
│                private setter and cannot be reassigned.      │
├──────────────────────────────────────────────────────────────┤
│ Infrastructure AppDbContext: query filters + write guard     │
│                HttpTenantContext: reads the JWT, only        │
└──────────────────────────────────────────────────────────────┘
```

The dependency rule points inward. What matters more here is that **tenant knowledge points outward**: only `Infrastructure` knows how the tenant is resolved and applied. The layers above it cannot express the wrong tenant, because none of their methods accept one.

### Request flow

```text
POST /api/customers
  │
  ├─ JwtBearer middleware validates signature, issuer, audience, lifetime
  │
  ├─ Authorization policy requires an authenticated user with a tenant_id claim
  │
  ├─ HttpTenantContext reads tenant_id from the validated principal
  │
  ├─ CustomerService counts this tenant's customers, checks the plan limit
  │
  ├─ new Customer(...) with no tenant id anywhere in sight
  │
  └─ SaveChanges stamps TenantId from the context, and would throw if the
     entity belonged to anyone else
```

---

## Folder structure

```text
dotnet-saas-multitenant/
├── src/
│   ├── Domain/
│   │   ├── Common/          BaseEntity, TenantEntity, ITenantOwned
│   │   ├── Tenants/         Tenant, Plan, PlanTier, ResourceKind
│   │   ├── Users/           User, UserRole
│   │   ├── Customers/       Customer
│   │   ├── Products/        Product
│   │   └── Orders/          Order, OrderItem, OrderStatus
│   ├── Application/
│   │   ├── Abstractions/    ITenantContext, repository interfaces
│   │   ├── Auth/            Sign in and registration
│   │   ├── Common/          Result, Error, PagedResponse
│   │   └── …                One folder per feature
│   ├── Infrastructure/
│   │   ├── Auth/            JwtTokenGenerator, HttpTenantContext, hashing
│   │   ├── Persistence/     AppDbContext, configurations, repositories,
│   │   │                    migrations
│   │   └── DependencyInjection.cs
│   └── Api/
│       ├── Endpoints/       One file per resource
│       ├── Extensions/      Result to HTTP, global exception handler
│       └── Program.cs
├── tests/
│   ├── UnitTests/           16 attempts to break isolation at the DbContext
│   └── IntegrationTests/    34 attempts to break it over HTTP
├── scripts/smoke-test.sh    The same boundary, against real SQL Server
├── docker-compose.yml
└── Dockerfile
```

---

## How to run

### With Docker

```bash
cp .env.example .env
# edit .env: set SQL_PASSWORD and JWT_SIGNING_KEY

docker compose up -d --build
```

The API comes up on <http://localhost:8080>, Swagger UI on <http://localhost:8080/swagger>. Migrations are applied on startup when `Database__MigrateOnStartup` is `true`, which compose sets for you.

Then prove the isolation for yourself:

```bash
API=http://localhost:8080 bash scripts/smoke-test.sh
```

```text
  ok: reading another tenant's customer returned 404
  ok: deleting another tenant's customer returned 404
  ok: an anonymous request returned 401
  ok: the second tenant's listing is empty
  ok: the owning tenant still reads its own customer
  ok: the sixth user on the free plan returned 402
```

### Locally

```bash
docker compose up -d sqlserver          # or point at your own SQL Server

dotnet ef database update -p src/Infrastructure -s src/Api
dotnet run --project src/Api
```

### Tests

```bash
dotnet test
```

50 tests: 16 unit tests against the `DbContext`, 34 integration tests through the full HTTP pipeline.

---

## Environment configuration

Every value below is a placeholder. There are no real credentials in this repository, and a CI job fails the build if one ever appears.

| Variable | Purpose |
|----------|---------|
| `SQL_PASSWORD` | SA password for the SQL Server container |
| `SQL_DATABASE` | Database name, default `SaasMultiTenant` |
| `SQL_PORT` | Host port for SQL Server, default `1433` |
| `JWT_SIGNING_KEY` | HS256 signing key, **at least 32 bytes** |
| `JWT_ISSUER` / `JWT_AUDIENCE` | Validated on every request |
| `JWT_EXPIRY_MINUTES` | Token lifetime, default 60 |
| `API_PORT` | Host port for the API, default `8080` |

The signing key deserves a sentence of its own. Every tenant boundary in this application rests on it: anyone holding it can mint a token naming any tenant. It belongs in a secret store, not in source control, and the application refuses to start if it is shorter than 32 bytes rather than failing at the first sign in.

---

## Main endpoints

| Method | Route | Who | Notes |
|--------|-------|-----|-------|
| `POST` | `/api/auth/register` | anonymous | Creates a tenant and its first admin |
| `POST` | `/api/auth/login` | anonymous | Needs the tenant slug as well as the email |
| `GET` | `/api/tenant` | member | The caller's own tenant, plan and usage |
| `GET` | `/api/tenant/plans` | member | Available plans and their limits |
| `PUT` | `/api/tenant/plan` | admin | Upgrade or downgrade |
| `GET` `POST` | `/api/users` | admin | List and invite users |
| `PUT` | `/api/users/{id}/role` | admin | Change a role |
| `DELETE` | `/api/users/{id}` | admin | Deactivate |
| `GET` `POST` | `/api/customers` | member | Search and create |
| `GET` `DELETE` | `/api/customers/{id}` | member | Fetch and deactivate |
| `GET` | `/api/customers/usage` | member | Usage against the plan limit |
| `GET` `POST` `PUT` `DELETE` | `/api/products…` | member | Including `POST /{id}/stock` |
| `GET` `POST` | `/api/orders` | member | Orders with items |
| `POST` | `/api/orders/{id}/confirm` `…/cancel` | member | State transitions |
| `GET` | `/health/live` `/health/ready` | anonymous | Liveness never touches the database |

Notice what is absent: no route contains a tenant segment, and no request body carries a tenant id.

### Status codes worth explaining

**Another tenant's resource returns 404, not 403.** A 403 would confirm the id names a real row, which is enough to enumerate another tenant's data one guess at a time. The row is simply not there, as far as this caller is concerned, and the response says so.

**A plan limit returns 402, not 403.** The request is well formed and the caller is entitled to make it. What is missing is a larger plan. That distinction is what lets a client show an upgrade prompt instead of an error page.

**A blocked cross tenant write returns a bare 403** and logs both tenant ids at warning level. The response says nothing about which entity was touched; the log says everything, because that is the line an operator wants an alert on.

---

## What the tests actually test

Every test is written from the attacker's side. Each one asks for something belonging to someone else and asserts that the answer gives nothing away.

**Unit tests** ([TenantIsolationTests.cs](tests/UnitTests/TenantIsolationTests.cs)) go at the `DbContext` directly, where the guarantees live:

- a query returns only the current tenant's rows, and `Find` by a foreign id returns null
- an anonymous context sees nothing at all, rather than everything
- the filter follows the current tenant instead of being baked in at model build
- attaching a foreign entity and calling `Update` throws
- rewriting `TenantId` and then saving throws, because the check reads original values
- `Remove` on a foreign entity throws
- the read escape hatch does not disable the write guard

**Integration tests** run the real pipeline with real tokens:

- two tenants, one API, and a list of things the second cannot reach
- a rejected order does not decrement the other tenant's stock on its way to failing
- a token naming another tenant but signed with the wrong key is rejected
- a `tenantId` in the query string or an `X-Tenant-Id` header changes nothing
- the same email, and the same order reference, can exist in both tenants
- plan limits bite at the boundary, and an upgrade lifts them immediately

**In CI**, a third job runs the same boundary against SQL Server in Docker and greps the generated SQL for the tenant predicate. The in memory provider proves the filter logic; only real SQL Server proves the SQL.

---

## Database notes

Every index on a tenant owned table leads with `TenantId`, because every query begins with it:

```text
IX_Customers_TenantId_Email
IX_Products_TenantId_Name
IX_Orders_TenantId_Reference   (unique)
IX_Orders_TenantId_CustomerId
IX_Users_TenantId_Email        (unique)
```

There is deliberately **no** standalone index on `TenantId`. SQL Server can use a leading column prefix of a composite index, so a second index on the same leading column would be redundant and would cost a write on every insert for nothing.

Uniqueness is per tenant, never global. Two companies can both have an employee at the same email address, and both can use the order reference `INV-001`. A global unique constraint would not only be wrong, it would leak: one tenant could discover another's data by watching for conflicts.

---

## Roadmap

- Row level security in SQL Server as a second, independent layer, so a direct database connection is bound by the same rule as the application
- Per tenant rate limiting, with the limit tied to the plan
- An audit trail of cross tenant attempts, queryable rather than only logged
- Refresh tokens and revocation
- A tenant admin area for suspending and reactivating tenants
- Optional per tenant database for enterprise customers, behind the same `ITenantContext`

---

## License

MIT. See [LICENSE](LICENSE).
