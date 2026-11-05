namespace Orbit.Api.Observability;

internal static class ApiMemoryBudget
{
    internal const long ContainerBytes = 512L * 1024 * 1024;
    internal const int WarningPercent = 80;
    internal const long WarningBytes = ContainerBytes * WarningPercent / 100;
    internal const long HeapBytes = 256L * 1024 * 1024;
}
