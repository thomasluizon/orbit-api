namespace Orbit.Api.Middleware;

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-XSS-Protection"] = "0";
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

        // CSP: restrictive default for API responses
        if (!context.Request.Path.StartsWithSegments("/oauth"))
        {
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        }
        else
        {
            headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' https://accounts.google.com https://apis.google.com 'unsafe-inline'; style-src 'self' https://fonts.googleapis.com 'unsafe-inline'; font-src https://fonts.gstatic.com; connect-src 'self'; frame-ancestors 'none'";
        }

        return next(context);
    }
}
