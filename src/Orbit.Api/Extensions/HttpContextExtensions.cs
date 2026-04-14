using System.Net;
using System.Security.Claims;
using Orbit.Domain.Models;

namespace Orbit.Api.Extensions;

public static class HttpContextExtensions
{
    public static Guid GetUserId(this HttpContext context)
    {
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");

        return Guid.Parse(userIdClaim);
    }

    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");

        return Guid.Parse(userIdClaim);
    }

    public static AgentAuthMethod GetAgentAuthMethod(this ClaimsPrincipal user)
    {
        var authMethod = user.FindFirst("auth_method")?.Value;
        return string.Equals(authMethod, "api_key", StringComparison.OrdinalIgnoreCase)
            ? AgentAuthMethod.ApiKey
            : AgentAuthMethod.Jwt;
    }

    public static IReadOnlyList<string> GetGrantedAgentScopes(this ClaimsPrincipal user)
    {
        return user.FindAll("scope")
            .Select(claim => claim.Value)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsReadOnlyCredential(this ClaimsPrincipal user)
    {
        return bool.TryParse(user.FindFirst("api_key_read_only")?.Value, out var isReadOnly) && isReadOnly;
    }

    private static readonly string[] CountryHeaderNames =
    [
        "CF-IPCountry",
        "X-Vercel-IP-Country",
        "CloudFront-Viewer-Country"
    ];

    private static readonly string[] IpHeaderNames =
    [
        "CF-Connecting-IP",
        "X-Forwarded-For",
        "X-Real-IP"
    ];

    public static string? GetClientCountryCode(this HttpContext context)
    {
        foreach (var headerName in CountryHeaderNames)
        {
            var countryCode = NormalizeCountryCode(context.Request.Headers[headerName].ToString());
            if (countryCode is not null)
                return countryCode;
        }

        var acceptLanguageCountryCode = NormalizeCountryCodeFromAcceptLanguage(
            context.Request.Headers.AcceptLanguage.ToString());
        if (acceptLanguageCountryCode is not null)
            return acceptLanguageCountryCode;

        return null;
    }

    public static string? GetClientIpAddress(this HttpContext context)
    {
        foreach (var headerName in IpHeaderNames)
        {
            var headerValue = context.Request.Headers[headerName].ToString();
            var firstIpAddress = headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (TryNormalizeIpAddress(firstIpAddress, out var normalizedIpAddress))
                return normalizedIpAddress;
        }

        return NormalizeIpAddress(context.Connection.RemoteIpAddress);
    }

    private static string? NormalizeCountryCode(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return null;

        var normalized = countryCode.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsLetter)
            ? normalized
            : null;
    }

    private static string? NormalizeCountryCodeFromAcceptLanguage(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return null;

        var languageTags = acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in languageTags)
        {
            var languageTag = entry.Split(';', 2, StringSplitOptions.TrimEntries)[0]
                .Replace('_', '-');
            var segments = languageTag.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
                continue;

            var countryCode = NormalizeCountryCode(segments[^1]);
            if (countryCode is not null)
                return countryCode;
        }

        return null;
    }

    private static bool TryNormalizeIpAddress(string? ipAddress, out string? normalizedIpAddress)
    {
        normalizedIpAddress = null;

        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress.Trim(), out var parsedIpAddress))
            return false;

        normalizedIpAddress = NormalizeIpAddress(parsedIpAddress);
        return normalizedIpAddress is not null;
    }

    private static string? NormalizeIpAddress(IPAddress? ipAddress)
    {
        if (ipAddress is null)
            return null;

        var normalizedIpAddress = ipAddress.IsIPv4MappedToIPv6
            ? ipAddress.MapToIPv4()
            : ipAddress;

        return normalizedIpAddress.ToString();
    }
}
