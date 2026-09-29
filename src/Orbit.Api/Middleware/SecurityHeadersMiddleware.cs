namespace Orbit.Api.Middleware;

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ReferrerPolicyHeader = "Referrer-Policy";
    private const string StrictTransportSecurityHeader = "Strict-Transport-Security";
    private const string PermissionsPolicyHeader = "Permissions-Policy";
    private const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers[ReferrerPolicyHeader] = "strict-origin-when-cross-origin";
        headers.XXSSProtection = "0";
        headers[StrictTransportSecurityHeader] = "max-age=31536000; includeSubDomains";
        headers[PermissionsPolicyHeader] = "camera=(), microphone=(), geolocation=(), payment=()";

        if (context.Request.Path.StartsWithSegments("/oauth"))
        {
            headers[ContentSecurityPolicyHeader] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'";
        }
        else if (!context.Request.Path.StartsWithSegments("/scalar") &&
                 !context.Request.Path.StartsWithSegments("/openapi"))
        {
            headers[ContentSecurityPolicyHeader] = "default-src 'none'; frame-ancestors 'none'";
        }

        return next(context);
    }
}
