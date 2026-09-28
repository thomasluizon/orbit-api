namespace Orbit.Api.Uploads;

public static class UploadObjectKey
{
    public static bool TryCreate(string? userId, string? fileName, out string objectKey)
    {
        objectKey = string.Empty;
        if (fileName is null)
            return false;

        var extensionSeparator = fileName.LastIndexOf('.');
        if (!Guid.TryParseExact(userId, "D", out var parsedUserId) ||
            extensionSeparator < 0 ||
            !Guid.TryParseExact(fileName.AsSpan(0, extensionSeparator), "D", out var parsedFileId))
            return false;

        var parsedExtension = fileName[(extensionSeparator + 1)..] switch
        {
            "png" => "png",
            "jpg" => "jpg",
            "webp" => "webp",
            _ => null
        };
        var canonicalFileId = parsedFileId.ToString("D");
        if (parsedExtension is null || canonicalFileId[14] != '4' ||
            canonicalFileId[19] is not ('8' or '9' or 'a' or 'b'))
            return false;

        objectKey = $"{parsedUserId:D}/{canonicalFileId}.{parsedExtension}";
        return true;
    }
}
