using FileSignatures;
using FileSignatures.Formats;
using Microsoft.AspNetCore.Http;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class ImageValidationService : IImageValidationService
{
    private const long MaxFileSizeBytes = 20_971_520; // 20MB (Gemini inline data limit)
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private static readonly FileFormatInspector Inspector = new();

    public async Task<Result<(string MimeType, long Size)>> ValidateAsync(IFormFile file)
    {
        // 1. Size check
        if (file.Length > MaxFileSizeBytes)
            return Result.Failure<(string, long)>($"File size exceeds maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)}MB.");

        if (file.Length == 0)
            return Result.Failure<(string, long)>("File is empty.");

        // 2. Extension check
        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            return Result.Failure<(string, long)>($"File extension '{extension}' is not allowed. Allowed: {string.Join(", ", AllowedExtensions)}.");

        // 3. Magic bytes signature check using FileSignatures library
        using var stream = file.OpenReadStream();
        var format = await Inspector.DetermineFileFormatAsync(stream);

        if (format == null)
            return Result.Failure<(string, long)>("Unable to determine file format from magic bytes.");

        // Map FileSignatures format to MIME type
        var mimeType = format switch
        {
            Jpeg => "image/jpeg",
            Png => "image/png",
            WebP => "image/webp",
            _ => null
        };

        if (mimeType == null)
            return Result.Failure<(string, long)>($"File format '{format.GetType().Name}' is not a supported image type.");

        return Result.Success((mimeType, file.Length));
    }
}
