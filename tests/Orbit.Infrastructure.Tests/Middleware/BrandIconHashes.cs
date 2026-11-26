namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// SHA-256 of each brand icon that the API serves. Each value equals the hash of the source file in
/// <c>orbit-ui-mobile</c>. The hosted <c>pwa-512x512.png</c> that the emails load is the same file as
/// <c>Icon512</c>. Change a value here only, in the same commit as the icon file.
/// </summary>
internal static class BrandIconHashes
{
    public const string FaviconIco = "8cb06c7aad887c9d8eb7bb62675c143737da7b2dc57652bb2feaabd4820c8e0a";
    public const string AppleTouchIcon = "091d8d1fde24baddb3c328260a1c7fe49dd6cf1b08846110d3c2798e7351b7a0";
    public const string Icon512 = "f2e00a9f1f43c555ddf6812cb3236999a1623d9b624d6f25029e29807e3ca8c1";
}
