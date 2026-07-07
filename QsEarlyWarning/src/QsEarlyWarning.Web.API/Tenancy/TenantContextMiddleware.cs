namespace QsEarlyWarning.Web.API.Tenancy;

/// <summary>
/// Populates the per-request <see cref="TenantContext"/> from headers. Stands in for a real auth
/// layer: X-User-Id is the authenticated principal, X-Project-Slug is the selected tenant. Both are
/// then validated against the database (existence + RLS membership) downstream.
/// </summary>
public sealed class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    public TenantContextMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext http, TenantContext ctx)
    {
        if (http.Request.Headers.TryGetValue("X-User-Id", out var u) && long.TryParse(u, out var userId))
            ctx.UserId = userId;
        if (http.Request.Headers.TryGetValue("X-Project-Slug", out var s) && !string.IsNullOrWhiteSpace(s))
            ctx.ProjectSlug = s.ToString().Trim();
        await _next(http);
    }
}
