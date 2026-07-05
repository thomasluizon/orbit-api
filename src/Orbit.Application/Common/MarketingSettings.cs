namespace Orbit.Application.Common;

public class MarketingSettings
{
    public const string SectionName = "Marketing";

    public string ApiBaseUrl { get; set; } = "https://api.useorbit.org";
    public int SendDelayMilliseconds { get; set; } = 100;
}
