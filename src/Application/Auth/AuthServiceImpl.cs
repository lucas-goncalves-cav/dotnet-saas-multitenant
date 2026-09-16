using Microsoft.Extensions.Logging;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Application.Auth;

public sealed class AuthService : IAuthService
{
    private readonly IAuthRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IAuthRepository repository,
        IPasswordHasher passwordHasher,
        ITokenGenerator tokenGenerator,
        ILogger<AuthService> logger)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _logger = logger;
    }

    public async Task<Result<LoginResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _repository.GetTenantBySlugAsync(request.TenantSlug, cancellationToken);

        // Every failure below returns the same message. Distinguishing "no such
        // tenant" from "no such user" would let anyone enumerate which
        // companies use the platform and who works there.
        if (tenant is null)
        {
            _logger.LogWarning("Login attempted for unknown tenant slug {Slug}.", request.TenantSlug);

            return Result.Failure<LoginResponse>(Error.Unauthorized("Invalid tenant, email or password."));
        }

        if (!tenant.Active)
        {
            return Result.Failure<LoginResponse>(
                Error.Forbidden($"This account is suspended. {tenant.SuspendedReason}"));
        }

        var user = await _repository.GetUserAsync(tenant.Id, request.Email, cancellationToken);

        if (user is null || !user.Active)
        {
            return Result.Failure<LoginResponse>(Error.Unauthorized("Invalid tenant, email or password."));
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Failed password for user {UserId} of tenant {TenantId}.", user.Id, tenant.Id);

            return Result.Failure<LoginResponse>(Error.Unauthorized("Invalid tenant, email or password."));
        }

        user.RecordLogin(DateTime.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        return Result.Success(BuildResponse(tenant, user));
    }

    public async Task<Result<LoginResponse>> RegisterTenantAsync(
        RegisterTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        var slug = request.TenantSlug.Trim().ToLowerInvariant();

        if (await _repository.TenantSlugExistsAsync(slug, cancellationToken))
        {
            return Result.Failure<LoginResponse>(
                Error.Conflict($"The slug '{slug}' is already taken."));
        }

        if (request.AdminPassword.Length < 8)
        {
            return Result.Failure<LoginResponse>(
                Error.Validation("The password must be at least 8 characters."));
        }

        // A new tenant starts on Free. Upgrading is a separate, deliberate act.
        var tenant = new Tenant(request.TenantName, slug, PlanTier.Free);

        await _repository.AddTenantAsync(tenant, cancellationToken);

        var admin = new User(
            request.AdminName,
            request.AdminEmail,
            _passwordHasher.Hash(request.AdminPassword),
            UserRole.Admin);

        // The tenant is passed explicitly because there is no tenant context
        // yet: this request created the tenant it is writing to.
        await _repository.AddUserAsync(admin, tenant.Id, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registered tenant {Slug} ({TenantId}).", tenant.Slug, tenant.Id);

        return Result.Success(BuildResponse(tenant, admin));
    }

    private LoginResponse BuildResponse(Tenant tenant, User user)
    {
        var (token, expiresAt) = _tokenGenerator.Generate(tenant, user);

        return new LoginResponse(
            token,
            expiresAt,
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.PlanTier.ToString(),
            user.Id,
            user.Name,
            user.Role.ToString());
    }
}
