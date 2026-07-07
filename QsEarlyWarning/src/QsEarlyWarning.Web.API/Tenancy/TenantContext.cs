namespace QsEarlyWarning.Web.API.Tenancy;

/// <summary>
/// The authenticated caller + selected project for one request (plan §5.0 Choice 4, §7 Phase 2).
///
/// In this build the identity is read from request headers (X-User-Id, X-Project-Slug) by
/// <see cref="TenantContextMiddleware"/> — a stand-in for a real token/OIDC layer that would populate
/// the same two values after validating a bearer token. The AUTHORIZATION half is not a stand-in:
/// project membership is enforced by PostgreSQL RLS (the registry passes the user id into the
/// transaction, and a non-member simply sees an empty panel).
/// </summary>
public sealed class TenantContext
{
    public long? UserId { get; set; }
    public string? ProjectSlug { get; set; }
    public bool IsAuthenticated => UserId is > 0 && !string.IsNullOrWhiteSpace(ProjectSlug);
}
