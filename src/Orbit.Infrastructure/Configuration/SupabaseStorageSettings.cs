namespace Orbit.Infrastructure.Configuration;

public sealed class SupabaseStorageSettings
{
    public const string SectionName = "Supabase";
    public required string Url { get; init; }
    public required string SecretKey { get; init; }
    public string Bucket { get; init; } = "uploads";
}
