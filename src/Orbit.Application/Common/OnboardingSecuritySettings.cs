namespace Orbit.Application.Common;

public sealed class OnboardingSecuritySettings
{
    public const string SectionName = "OnboardingSecurity";
    public string SigningSecret { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = [];
    public string[] TurnstileHostnames { get; set; } = [];
    public string PlayIntegrityCredentialsJson { get; set; } = "";
    public string AndroidPackageName { get; set; } = "";
    public string[] AndroidCertificateDigests { get; set; } = [];
    public string AppleTeamId { get; set; } = "";
    public string AppleBundleId { get; set; } = "";
    public string[] AppleBundleVersions { get; set; } = [];
    public uint[] AppleValidationCategories { get; set; } = [];
}
