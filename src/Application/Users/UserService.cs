using SaasMultiTenant.Application.Abstractions;
using SaasMultiTenant.Application.Auth;
using SaasMultiTenant.Application.Common;
using SaasMultiTenant.Application.Customers;
using SaasMultiTenant.Domain.Common;
using SaasMultiTenant.Domain.Tenants;
using SaasMultiTenant.Domain.Users;

namespace SaasMultiTenant.Application.Users;

public sealed record CreateUserRequest(string Name, string Email, string Password, string Role);

public sealed record ChangeRoleRequest(string Role);

public sealed record UserResponse(
    Guid Id,
    string Name,
    string Email,
    string Role,
    bool Active,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public interface IUserService
{
    Task<Result<UserResponse>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<UserResponse>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result<UserResponse>> ChangeRoleAsync(
        Guid id,
        ChangeRoleRequest request,
        CancellationToken cancellationToken = default);

    Task<Result> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PlanUsage>> UsageAsync(CancellationToken cancellationToken = default);
}

public sealed class UserService : IUserService
{
    private readonly IUserRepository _users;
    private readonly ITenantRepository _tenants;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public UserService(
        IUserRepository users,
        ITenantRepository tenants,
        IPasswordHasher passwordHasher,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _users = users;
        _tenants = tenants;
        _passwordHasher = passwordHasher;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<Result<UserResponse>> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
        {
            return Result.Failure<UserResponse>(
                Error.Validation($"Role must be one of: {string.Join(", ", Enum.GetNames<UserRole>())}."));
        }

        var tenant = await CurrentTenantAsync(cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<UserResponse>(Error.NotFound("The current tenant no longer exists."));
        }

        // Scoped by the filter, so a colleague at another company holding the
        // same address is not a conflict. Email is unique per tenant, not
        // across the platform.
        if (await _users.EmailExistsAsync(request.Email, cancellationToken))
        {
            return Result.Failure<UserResponse>(
                Error.Conflict($"A user with email {request.Email} already exists in this tenant."));
        }

        var currentCount = await _users.CountAsync(cancellationToken);

        try
        {
            tenant.EnsureCanAdd(ResourceKind.User, currentCount);
        }
        catch (PlanLimitExceededException exception)
        {
            return Result.Failure<UserResponse>(Error.PlanLimit(exception.Message));
        }

        User user;

        try
        {
            user = new User(request.Name, request.Email, _passwordHasher.Hash(request.Password), role);
        }
        catch (DomainException exception)
        {
            return Result.Failure<UserResponse>(Error.Validation(exception.Message));
        }

        await _users.AddAsync(user, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(user));
    }

    public async Task<Result<IReadOnlyCollection<UserResponse>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var users = await _users.GetAllAsync(cancellationToken);

        return Result.Success<IReadOnlyCollection<UserResponse>>(users.Select(Map).ToList());
    }

    public async Task<Result<UserResponse>> ChangeRoleAsync(
        Guid id,
        ChangeRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
        {
            return Result.Failure<UserResponse>(
                Error.Validation($"Role must be one of: {string.Join(", ", Enum.GetNames<UserRole>())}."));
        }

        var user = await _users.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return Result.Failure<UserResponse>(Error.NotFound($"User {id} was not found."));
        }

        // An admin demoting themselves could leave the tenant with nobody able
        // to manage it, so it is refused outright rather than counted.
        if (user.Id == _tenantContext.UserId && role != UserRole.Admin)
        {
            return Result.Failure<UserResponse>(
                Error.Forbidden("You cannot remove your own administrator role."));
        }

        user.ChangeRole(role);

        _users.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(user));
    }

    public async Task<Result> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _users.GetByIdAsync(id, cancellationToken);

        if (user is null)
        {
            return Result.Failure(Error.NotFound($"User {id} was not found."));
        }

        if (user.Id == _tenantContext.UserId)
        {
            return Result.Failure(Error.Forbidden("You cannot deactivate your own account."));
        }

        user.Deactivate();

        _users.Update(user);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result<PlanUsage>> UsageAsync(CancellationToken cancellationToken = default)
    {
        var tenant = await CurrentTenantAsync(cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<PlanUsage>(Error.NotFound("The current tenant no longer exists."));
        }

        var used = await _users.CountAsync(cancellationToken);

        return Result.Success(new PlanUsage(used, tenant.Plan.MaxUsers, tenant.Plan.IsUnlimited(ResourceKind.User)));
    }

    private Task<Tenant?> CurrentTenantAsync(CancellationToken cancellationToken) =>
        _tenants.GetByIdAsync(_tenantContext.RequireTenantId(), cancellationToken);

    private static UserResponse Map(User user) => new(
        user.Id,
        user.Name,
        user.Email,
        user.Role.ToString(),
        user.Active,
        user.CreatedAt,
        user.LastLoginAt);
}
