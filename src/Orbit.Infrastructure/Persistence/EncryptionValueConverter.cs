using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

/// <summary>
/// EF Core ValueConverter that encrypts string values on write and decrypts on read.
/// For non-nullable string columns.
/// </summary>
public sealed class EncryptionValueConverter : ValueConverter<string, string>
{
    public EncryptionValueConverter(IEncryptionService encryptionService)
        : base(
            v => encryptionService.Encrypt(v),
            v => encryptionService.Decrypt(v))
    {
    }
}

/// <summary>
/// EF Core ValueConverter that encrypts nullable string values on write and decrypts on read.
/// Passes through null without encrypting.
/// </summary>
public sealed class NullableEncryptionValueConverter : ValueConverter<string?, string?>
{
    public NullableEncryptionValueConverter(IEncryptionService encryptionService)
        : base(
            v => encryptionService.EncryptNullable(v),
            v => encryptionService.DecryptNullable(v))
    {
    }
}
