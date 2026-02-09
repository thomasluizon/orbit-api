using Microsoft.AspNetCore.Http;
using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IImageValidationService
{
    Task<Result<(string MimeType, long Size)>> ValidateAsync(IFormFile file);
}
