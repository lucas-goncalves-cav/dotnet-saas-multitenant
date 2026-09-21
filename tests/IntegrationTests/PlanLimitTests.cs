using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Application.Tenants;

namespace SaasMultiTenant.IntegrationTests;

/// <summary>
/// Plan limits over HTTP, including what happens at the boundary and what a
/// caller is told when they reach it.
/// </summary>
public sealed class PlanLimitTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PlanLimitTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_new_tenant_starts_on_the_free_plan()
    {
        var tenant = await RegisterAsync();

        var response = await tenant.Client.GetFromJsonAsync<TenantResponse>("/api/tenant");

        response!.PlanTier.Should().Be("Free");
        response.Plan.MaxUsers.Should().Be(5);
        response.Users.Used.Should().Be(1);
        response.Users.Remaining.Should().Be(4);
    }

    [Fact]
    public async Task The_sixth_user_on_the_free_plan_is_refused_with_payment_required()
    {
        var tenant = await RegisterAsync();

        // The administrator created at registration is the first of five.
        for (var index = 2; index <= 5; index++)
        {
            var allowed = await CreateUserAsync(tenant, index);

            allowed.StatusCode.Should().Be(HttpStatusCode.Created, $"user {index} is within the free plan");
        }

        var refused = await CreateUserAsync(tenant, 6);

        // 402 rather than 403: the request is legitimate and the caller is
        // entitled to make it. What is missing is a larger plan, and that is
        // what tells a client to show an upgrade prompt rather than an error.
        refused.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);

        var problem = await refused.Content.ReadFromJsonAsync<ProblemPayload>();

        problem!.Detail.Should().Contain("Free").And.Contain("5");
    }

    [Fact]
    public async Task Upgrading_the_plan_raises_the_limit()
    {
        var tenant = await RegisterAsync();

        for (var index = 2; index <= 5; index++)
        {
            await CreateUserAsync(tenant, index);
        }

        (await CreateUserAsync(tenant, 6)).StatusCode.Should().Be(HttpStatusCode.PaymentRequired);

        var upgrade = await tenant.Client.PutAsJsonAsync("/api/tenant/plan", new ChangePlanRequest("Professional"));

        upgrade.StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateUserAsync(tenant, 6)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Downgrading_below_current_usage_is_refused_rather_than_deleting_the_excess()
    {
        var tenant = await RegisterAsync();

        await tenant.Client.PutAsJsonAsync("/api/tenant/plan", new ChangePlanRequest("Professional"));

        for (var index = 2; index <= 7; index++)
        {
            (await CreateUserAsync(tenant, index)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var downgrade = await tenant.Client.PutAsJsonAsync("/api/tenant/plan", new ChangePlanRequest("Free"));

        downgrade.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);

        // Which users to remove is the customer's decision, so the plan stays
        // where it was rather than the platform picking for them.
        var current = await tenant.Client.GetFromJsonAsync<TenantResponse>("/api/tenant");

        current!.PlanTier.Should().Be("Professional");
        current.Users.Used.Should().Be(7);
    }

    [Fact]
    public async Task The_enterprise_plan_reports_no_remaining_count()
    {
        var tenant = await RegisterAsync();

        await tenant.Client.PutAsJsonAsync("/api/tenant/plan", new ChangePlanRequest("Enterprise"));

        var usage = await tenant.Client.GetFromJsonAsync<PlanUsage>("/api/customers/usage");

        usage!.Unlimited.Should().BeTrue();
        usage.Remaining.Should().BeNull();
    }

    [Fact]
    public async Task A_member_cannot_manage_users_or_change_the_plan()
    {
        var tenant = await RegisterAsync();

        var created = await CreateUserAsync(tenant, 2);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var member = await SignInAsync(tenant.Login.TenantSlug, $"user2@{tenant.Login.TenantSlug}.test");

        (await member.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var planChange = await member.PutAsJsonAsync("/api/tenant/plan", new ChangePlanRequest("Enterprise"));

        planChange.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A member is not locked out of the product, only out of administration.
        (await member.GetAsync("/api/customers")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_administrator_cannot_remove_their_own_administrator_role()
    {
        var tenant = await RegisterAsync();

        var response = await tenant.Client.PutAsJsonAsync(
            $"/api/users/{tenant.UserId}/role",
            new { role = "Viewer" });

        // Otherwise the last administrator can lock the whole tenant out of
        // its own user management, and only support can undo it.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_plan_name_is_a_validation_error()
    {
        var tenant = await RegisterAsync();

        var response = await tenant.Client.PutAsJsonAsync("/api/tenant/plan", new ChangePlanRequest("Platinum"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private Task<TenantClient> RegisterAsync() =>
        _factory.RegisterTenantAsync($"plan-{Guid.NewGuid():N}"[..20], "Plan Tests");

    private static Task<HttpResponseMessage> CreateUserAsync(TenantClient tenant, int index) =>
        tenant.Client.PostAsJsonAsync("/api/users", new
        {
            name = $"User {index}",
            email = $"user{index}@{tenant.Login.TenantSlug}.test",
            password = "correct horse battery staple",
            role = "Member",
        });

    private async Task<HttpClient> SignInAsync(string slug, string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(slug, email, "correct horse battery staple"));

        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }
}

public sealed record ProblemPayload(string? Title, string? Detail, int? Status, string? Code);
