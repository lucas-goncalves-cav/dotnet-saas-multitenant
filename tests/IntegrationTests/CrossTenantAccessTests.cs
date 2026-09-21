using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Application.Orders;
using SaasMultiTenant.Application.Products;
using SaasMultiTenant.Application.Tenants;

namespace SaasMultiTenant.IntegrationTests;

/// <summary>
/// Two tenants, one API, and a series of attempts by one to reach the other.
///
/// Every test here is written from the attacker's side: it asks for something
/// belonging to someone else and asserts that the answer gives nothing away.
/// The unit tests prove the DbContext filters correctly; these prove the whole
/// pipeline does, with real tokens over real HTTP.
/// </summary>
public sealed class CrossTenantAccessTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    private TenantClient _acme = null!;
    private TenantClient _globex = null!;

    public CrossTenantAccessTests(ApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _acme = await _factory.RegisterTenantAsync($"acme-{Suffix()}", "Acme");
        _globex = await _factory.RegisterTenantAsync($"globex-{Suffix()}", "Globex");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Two_tenants_registering_get_different_tenant_ids()
    {
        _acme.TenantId.Should().NotBe(_globex.TenantId);
    }

    [Fact]
    public async Task A_tenant_does_not_see_another_tenants_customers_in_a_listing()
    {
        await CreateCustomerAsync(_acme, "Acme Customer");

        var response = await _globex.Client.GetFromJsonAsync<PagedResponsePayload<CustomerResponse>>(
            "/api/customers");

        response!.Items.Should().BeEmpty();
        response.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task Fetching_another_tenants_customer_by_id_returns_not_found()
    {
        var customer = await CreateCustomerAsync(_acme, "Acme Customer");

        var response = await _globex.Client.GetAsync($"/api/customers/{customer.Id}");

        // 404 and not 403. A 403 would confirm the id names a real row, which
        // is enough to enumerate another tenant's customers one guess at a time.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_another_tenants_customer_returns_not_found_and_leaves_it_intact()
    {
        var customer = await CreateCustomerAsync(_acme, "Acme Customer");

        var response = await _globex.Client.DeleteAsync($"/api/customers/{customer.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The point of the test: the answer was 404, and the row is untouched.
        var stillThere = await _acme.Client.GetFromJsonAsync<CustomerResponse>($"/api/customers/{customer.Id}");

        stillThere!.Active.Should().BeTrue();
    }

    [Fact]
    public async Task Updating_another_tenants_product_returns_not_found_and_does_not_change_the_price()
    {
        var product = await CreateProductAsync(_acme, "Anvil", 100m);

        var response = await _globex.Client.PutAsJsonAsync(
            $"/api/products/{product.Id}",
            new UpdateProductRequest("Anvil", "Repriced by another tenant", 1m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unchanged = await _acme.Client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}");

        unchanged!.Price.Should().Be(100m);
    }

    [Fact]
    public async Task An_order_cannot_be_created_against_another_tenants_customer()
    {
        var acmeCustomer = await CreateCustomerAsync(_acme, "Acme Customer");
        var globexProduct = await CreateProductAsync(_globex, "Globex Widget", 10m);

        var response = await _globex.Client.PostAsJsonAsync("/api/orders", new CreateOrderRequest(
            acmeCustomer.Id,
            $"ORD-{Suffix()}",
            [new OrderItemRequest(globexProduct.Id, 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_order_cannot_be_created_against_another_tenants_product()
    {
        var globexCustomer = await CreateCustomerAsync(_globex, "Globex Customer");
        var acmeProduct = await CreateProductAsync(_acme, "Acme Anvil", 10m);

        var response = await _globex.Client.PostAsJsonAsync("/api/orders", new CreateOrderRequest(
            globexCustomer.Id,
            $"ORD-{Suffix()}",
            [new OrderItemRequest(acmeProduct.Id, 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Stock is the second thing to check: a rejected order must not have
        // decremented the other tenant's inventory on its way to failing.
        var product = await _acme.Client.GetFromJsonAsync<ProductResponse>($"/api/products/{acmeProduct.Id}");

        product!.Stock.Should().Be(50);
    }

    [Fact]
    public async Task Confirming_another_tenants_order_returns_not_found()
    {
        var customer = await CreateCustomerAsync(_acme, "Acme Customer");
        var product = await CreateProductAsync(_acme, "Acme Anvil", 25m);

        var created = await _acme.Client.PostAsJsonAsync("/api/orders", new CreateOrderRequest(
            customer.Id,
            $"ORD-{Suffix()}",
            [new OrderItemRequest(product.Id, 2)]));

        var order = await created.Content.ReadFromJsonAsync<OrderResponse>();

        var response = await _globex.Client.PostAsync($"/api/orders/{order!.Id}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var untouched = await _acme.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{order.Id}");

        untouched!.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task The_same_order_reference_can_be_used_by_two_tenants()
    {
        var reference = $"ORD-{Suffix()}";

        var acmeOrder = await CreateOrderAsync(_acme, reference);
        var globexOrder = await CreateOrderAsync(_globex, reference);

        // Uniqueness is per tenant. A global constraint would let one tenant
        // discover another's references by watching for conflicts.
        acmeOrder.StatusCode.Should().Be(HttpStatusCode.Created);
        globexOrder.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_same_email_can_belong_to_a_user_of_each_tenant()
    {
        const string sharedEmail = "consultant@example.test";

        var inAcme = await _acme.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Shared Consultant",
            email = sharedEmail,
            password = "correct horse battery staple",
            role = "Member",
        });

        var inGlobex = await _globex.Client.PostAsJsonAsync("/api/users", new
        {
            name = "Shared Consultant",
            email = sharedEmail,
            password = "correct horse battery staple",
            role = "Member",
        });

        inAcme.StatusCode.Should().Be(HttpStatusCode.Created);
        inGlobex.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_tenant_endpoint_returns_only_the_callers_own_tenant()
    {
        var acme = await _acme.Client.GetFromJsonAsync<TenantResponse>("/api/tenant");
        var globex = await _globex.Client.GetFromJsonAsync<TenantResponse>("/api/tenant");

        acme!.Id.Should().Be(_acme.TenantId);
        globex!.Id.Should().Be(_globex.TenantId);
        acme.Id.Should().NotBe(globex.Id);
    }

    [Fact]
    public async Task An_anonymous_request_is_rejected()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/customers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_naming_another_tenant_but_signed_with_the_wrong_key_is_rejected()
    {
        // The whole isolation model rests on the signature. This is the test
        // that says so: same claims, same shape, different key.
        var forged = ForgeToken(_acme.TenantId, "an_attacker_signing_key_that_is_long_enough_32");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        var response = await client.GetAsync("/api/customers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_request_with_a_tenant_id_in_the_query_string_is_ignored()
    {
        await CreateCustomerAsync(_acme, "Acme Customer");

        // A caller guessing at an implementation that reads the tenant from
        // the request gets exactly what their own token entitles them to.
        var response = await _globex.Client.GetFromJsonAsync<PagedResponsePayload<CustomerResponse>>(
            $"/api/customers?tenantId={_acme.TenantId}&tenant_id={_acme.TenantId}");

        response!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_id_header_does_not_override_the_token()
    {
        await CreateCustomerAsync(_acme, "Acme Customer");

        _globex.Client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        _globex.Client.DefaultRequestHeaders.Add("X-Tenant-Id", _acme.TenantId.ToString());

        try
        {
            var response = await _globex.Client.GetFromJsonAsync<PagedResponsePayload<CustomerResponse>>(
                "/api/customers");

            response!.Items.Should().BeEmpty();
        }
        finally
        {
            _globex.Client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        }
    }

    [Fact]
    public async Task Each_tenant_counts_only_its_own_usage_against_its_plan()
    {
        await CreateCustomerAsync(_acme, "Acme One");
        await CreateCustomerAsync(_acme, "Acme Two");
        await CreateCustomerAsync(_globex, "Globex One");

        var acme = await _acme.Client.GetFromJsonAsync<PlanUsage>("/api/customers/usage");
        var globex = await _globex.Client.GetFromJsonAsync<PlanUsage>("/api/customers/usage");

        acme!.Used.Should().Be(2);
        globex!.Used.Should().Be(1);
    }

    private static string ForgeToken(Guid tenantId, string signingKey)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "saas-multitenant-tests",
            audience: "saas-multitenant-tests",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.Role, "Admin"),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<CustomerResponse> CreateCustomerAsync(TenantClient tenant, string name)
    {
        var response = await tenant.Client.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(
            name,
            $"{Guid.NewGuid():N}@example.test",
            null,
            null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<CustomerResponse>())!;
    }

    private static async Task<ProductResponse> CreateProductAsync(TenantClient tenant, string name, decimal price)
    {
        var response = await tenant.Client.PostAsJsonAsync("/api/products", new CreateProductRequest(
            name,
            null,
            price,
            50));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private static async Task<HttpResponseMessage> CreateOrderAsync(TenantClient tenant, string reference)
    {
        var customer = await CreateCustomerAsync(tenant, "Buyer");
        var product = await CreateProductAsync(tenant, "Item", 5m);

        return await tenant.Client.PostAsJsonAsync("/api/orders", new CreateOrderRequest(
            customer.Id,
            reference,
            [new OrderItemRequest(product.Id, 1)]));
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// A local shape for the paged payload, so the tests deserialise the JSON the
/// API actually returns rather than reusing the server side generic.
/// </summary>
public sealed record PagedResponsePayload<T>(
    IReadOnlyCollection<T> Items,
    int TotalItems,
    int Page,
    int PageSize,
    int TotalPages);
