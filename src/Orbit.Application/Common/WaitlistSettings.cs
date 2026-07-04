namespace Orbit.Application.Common;

public class WaitlistSettings
{
    public const string SectionName = "Waitlist";

    public string SigningKey { get; set; } = "";
    public int TokenLifetimeHours { get; set; } = 48;
    public string LandingBaseUrl { get; set; } = "https://useorbit.org";
    public string ApiBaseUrl { get; set; } = "https://api.useorbit.org";
}
