namespace Orbit.Application.Common;

public class MarketingSettings
{
    public const string SectionName = "Marketing";

    public string ApiBaseUrl { get; set; } = "https://api.useorbit.org";
    public int BroadcastPageSize { get; set; } = 500;
    public int MaxSendsPerSecond { get; set; } = 10;
    public int SendDelayMilliseconds { get; set; } = 100;
    public string UnsubscribeSigningKey { get; set; } = "";
}
