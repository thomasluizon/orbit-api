using System.Security.Cryptography;

namespace Orbit.Api.Middleware;

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ReferrerPolicyHeader = "Referrer-Policy";
    private const string StrictTransportSecurityHeader = "Strict-Transport-Security";
    private const string PermissionsPolicyHeader = "Permissions-Policy";
    private const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    /// <summary>
    /// Per-request CSP nonce stashed in HttpContext.Items. Read from
    /// HttpContext via <c>HttpContext.GetCspNonce()</c> when rendering inline
    /// scripts/styles. Replaces the previous 'unsafe-inline' allowance on
    /// /oauth pages (PLAN.md frontend P1 + backend CORS/Headers P1).
    /// </summary>
    public const string CspNonceItemKey = "CspNonce";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers[ReferrerPolicyHeader] = "strict-origin-when-cross-origin";
        headers.XXSSProtection = "0";
        headers[StrictTransportSecurityHeader] = "max-age=31536000; includeSubDomains";
        headers[PermissionsPolicyHeader] = "camera=(), microphone=(), geolocation=(), payment=()";

        // CSP: skip for Scalar docs and OpenAPI spec (they need inline scripts/styles)
        if (context.Request.Path.StartsWithSegments("/oauth"))
        {
            // Generate a fresh nonce per request and use it on inline <script>/<style>.
            // No 'unsafe-inline' for scripts. We retain 'unsafe-inline' for styles only
            // because Google fonts inject inline style attributes; if we ever fully
            // own the CSS we can drop that too. The script CSP is now nonce-only.
            var nonceBytes = RandomNumberGenerator.GetBytes(18);
            var nonce = Convert.ToBase64String(nonceBytes);
            context.Items[CspNonceItemKey] = nonce;

            headers[ContentSecurityPolicyHeader] =
                $"default-src 'self'; " +
                $"script-src 'self' 'nonce-{nonce}' https://accounts.google.com https://apis.google.com; " +
                $"style-src 'self' 'nonce-{nonce}' https://fonts.googleapis.com; " +
                "font-src https://fonts.gstatic.com; " +
                "connect-src 'self'; " +
                "frame-ancestors 'none'";
        }
        else if (!context.Request.Path.StartsWithSegments("/scalar") &&
                 !context.Request.Path.StartsWithSegments("/openapi"))
        {
            headers[ContentSecurityPolicyHeader] = "default-src 'none'; frame-ancestors 'none'";
        }

        return next(context);
    }
}

public static class HttpContextCspExtensions
{
    /// <summary>
    /// Returns the per-request CSP nonce set by <see cref="SecurityHeadersMiddleware"/>,
    /// or empty string if none (e.g. routes outside /oauth).
    /// </summary>
    public static string GetCspNonce(this HttpContext context)
    {
        return context.Items.TryGetValue(SecurityHeadersMiddleware.CspNonceItemKey, out var v) && v is string s
            ? s
            : string.Empty;
    }
}
