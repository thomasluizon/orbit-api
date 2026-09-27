namespace Orbit.Infrastructure.Configuration;

public sealed class S3StorageSettings
{
    public const string SectionName = "Storage:S3";

    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
}
