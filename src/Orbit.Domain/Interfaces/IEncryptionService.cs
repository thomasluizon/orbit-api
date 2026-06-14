namespace Orbit.Domain.Interfaces;

public interface IEncryptionService
{
    bool IsConfigured { get; }
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
    string? EncryptNullable(string? plaintext);
    string? DecryptNullable(string? ciphertext);
}
