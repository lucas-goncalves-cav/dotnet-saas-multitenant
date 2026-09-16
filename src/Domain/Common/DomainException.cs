namespace SaasMultiTenant.Domain.Common;

public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}

/// <summary>
/// A tenant tried to exceed what its plan allows. Distinct from a validation
/// error because the fix is upgrading, not correcting the request.
/// </summary>
public sealed class PlanLimitExceededException : DomainException
{
    public PlanLimitExceededException(string resource, int limit, string planName)
        : base($"The {planName} plan allows at most {limit} {resource}. Upgrade to add more.")
    {
        Resource = resource;
        Limit = limit;
        PlanName = planName;
    }

    public string Resource { get; }

    public int Limit { get; }

    public string PlanName { get; }
}
