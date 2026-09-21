using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Application.Users;

namespace SaasMultiTenant.IntegrationTests;

/// <summary>
/// Sign in and registration, including what a failure is allowed to reveal.
/// </summary>
public sealed class AuthenticationTests : IClassFixture<ApiFactory>
{
    private const string Password = "correct horse battery staple";

    private readonly ApiFactory _factory;

    public AuthenticationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Signing_in_succeeds_and_records_the_login()
    {
        var tenant = await RegisterAsync();

        // Sign in writes the login timestamp, which means the write guard runs
        // on a request that has no token yet. It is the one flow where that is
        // legitimate, and the one most likely to be broken by a change to the
        // guard, so it is asserted rather than assumed.
        var response = await SignInAsync(tenant.Login.TenantSlug, $"admin@{tenant.Login.TenantSlug}.test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var users = await tenant.Client.GetFromJsonAsync<IReadOnlyCollection<UserResponse>>("/api/users");

        users!.Single(user => user.Id == tenant.UserId).LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Signing_in_twice_works()
    {
        var tenant = await RegisterAsync();
        var email = $"admin@{tenant.Login.TenantSlug}.test";

        (await SignInAsync(tenant.Login.TenantSlug, email)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SignInAsync(tenant.Login.TenantSlug, email)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_tenant_and_a_wrong_password_give_the_same_answer()
    {
        var tenant = await RegisterAsync();
        var email = $"admin@{tenant.Login.TenantSlug}.test";

        var unknownTenant = await SignInAsync("no-such-tenant-at-all", email);
        var unknownUser = await SignInAsync(tenant.Login.TenantSlug, "nobody@example.test");
        var wrongPassword = await SignInAsync(tenant.Login.TenantSlug, email, "not the password");

        unknownTenant.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownUser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var messages = new[]
        {
            await DetailAsync(unknownTenant),
            await DetailAsync(unknownUser),
            await DetailAsync(wrongPassword),
        };

        // Identical on purpose. A different message for an unknown tenant would
        // turn the login form into a directory of which companies use the
        // platform, and then of who works at them.
        messages.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_taken_slug_is_rejected()
    {
        var tenant = await RegisterAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterTenantRequest(
            "Another Company",
            tenant.Login.TenantSlug,
            "Admin",
            "admin@example.test",
            Password));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_slug_is_normalised_to_lower_case_rather_than_rejected()
    {
        var slug = $"MiXeD-{Guid.NewGuid():N}"[..20];

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterTenantRequest(
            "Company",
            slug,
            "Admin",
            "admin@example.test",
            Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        // Typing a capital letter is a typo, not an error worth a form
        // rejection. What matters is that one slug means one tenant, which
        // normalising achieves.
        login!.TenantSlug.Should().Be(slug.ToLowerInvariant());
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("-leading-hyphen")]
    [InlineData("ab")]
    public async Task An_invalid_slug_is_rejected(string slug)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterTenantRequest(
            "Company",
            slug,
            "Admin",
            "admin@example.test",
            Password));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_short_password_is_rejected()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterTenantRequest(
            "Company",
            $"short-{Guid.NewGuid():N}"[..20],
            "Admin",
            "admin@example.test",
            "short"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_health_endpoints_are_anonymous()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private Task<TenantClient> RegisterAsync() =>
        _factory.RegisterTenantAsync($"auth-{Guid.NewGuid():N}"[..20], "Auth Tests");

    private Task<HttpResponseMessage> SignInAsync(string slug, string email, string password = Password) =>
        _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(slug, email, password));

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();

        return problem?.Detail ?? string.Empty;
    }
}
