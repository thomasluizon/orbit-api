using System.Reflection;

namespace Orbit.Api.Extensions;

internal static class BuildTimeDocumentGeneration
{
    /// <summary>
    /// <c>true</c> while the OpenAPI spec is being emitted at build time (no infrastructure available).
    /// </summary>
    public static bool IsActive =>
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
}
