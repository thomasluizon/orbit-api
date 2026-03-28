namespace Orbit.Domain.Interfaces;

public interface IEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
    string? EncryptNullable(string? plaintext);
    string? DecryptNullable(string? ciphertext);
    string ComputeHmac(string input);
}
