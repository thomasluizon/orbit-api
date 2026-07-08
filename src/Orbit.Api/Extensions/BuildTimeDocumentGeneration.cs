using System.Reflection;

namespace Orbit.Api.Extensions;

/// <summary>
/// Detects the build-time OpenAPI document generation pass, where the
/// <c>Microsoft.Extensions.ApiDescription.Server</c> tool launches the app entry point under a
/// mock server (entry assembly renamed to <c>GetDocument.Insider</c>). Under that pass the host
/// still starts every <see cref="Microsoft.Extensions.Hosting.IHostedService"/> and runs full DI,
/// so database-touching startup side effects must be skipped when this is active.
/// </summary>
internal static class BuildTimeDocumentGeneration
{
    /// <summary>
    /// <c>true</c> while the OpenAPI spec is being emitted at build time (no infrastructure available).
    /// </summary>
    public static bool IsActive =>
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
}
