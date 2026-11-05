namespace Orbit.Api.Observability;

/// <summary>
/// The memory budget of one Render Starter instance. The warning applies to anonymous plus shmem memory,
/// which the kernel cannot reclaim. The rest of the container holds the file-backed code pages and transient growth.
/// See infra/memory-budget.md.
/// </summary>
internal static class ApiMemoryBudget
{
    internal const long ContainerBytes = 512L * 1024 * 1024;
    internal const long WarningBytes = 300L * 1024 * 1024;
    internal const long HeapBytes = 256L * 1024 * 1024;
}
