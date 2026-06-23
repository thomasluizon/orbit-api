using MediatR;
using Orbit.Application.Uploads.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Uploads.Commands;

public record SignUploadResponse(string Key, string SignedUrl, string PublicUrl);

public record SignUploadCommand(
    Guid UserId,
    string ContentType,
    long SizeBytes) : IRequest<Result<SignUploadResponse>>;

public class SignUploadCommandHandler(IObjectStorageService objectStorage)
    : IRequestHandler<SignUploadCommand, Result<SignUploadResponse>>
{
    public async Task<Result<SignUploadResponse>> Handle(SignUploadCommand request, CancellationToken cancellationToken)
    {
        var extension = UploadContentTypes.ExtensionFor(request.ContentType);
        var objectKey = $"{request.UserId}/{Guid.NewGuid()}.{extension}";

        var signed = await objectStorage.CreateSignedUploadAsync(objectKey, cancellationToken);

        return Result.Success(new SignUploadResponse(signed.Key, signed.SignedUrl, signed.PublicUrl));
    }
}
