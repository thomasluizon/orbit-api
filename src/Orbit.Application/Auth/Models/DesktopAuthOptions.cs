namespace Orbit.Application.Auth.Models;

public sealed class DesktopAuthOptions
{
    public const string SectionName = "DesktopAuth";
    public string RedirectUri { get; set; } = "org.useorbit.desktop://auth/callback";
}
