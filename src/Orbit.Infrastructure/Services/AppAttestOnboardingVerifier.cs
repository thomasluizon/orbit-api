using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class AppAttestOnboardingVerifier : IOnboardingAttestationVerifier
{
    private readonly OnboardingSecuritySettings settings;
    private readonly TimeProvider clock;
    private readonly byte[] attestationRoot;
    private readonly byte[] receiptRoot;

    public AppAttestOnboardingVerifier(IOptions<OnboardingSecuritySettings> options, TimeProvider clock)
        : this(options, clock, ReadRoot("AppleAppAttestationRootCA.pem", true), ReadRoot("AppleRootCA-G3.cer", false)) { }

    internal AppAttestOnboardingVerifier(IOptions<OnboardingSecuritySettings> options, TimeProvider clock,
        byte[] attestationRoot, byte[] receiptRoot)
    {
        settings = options.Value;
        this.clock = clock;
        this.attestationRoot = attestationRoot;
        this.receiptRoot = receiptRoot;
    }

    public bool Supports(string platform) => platform == "ios";
    public bool IsConfigured => settings.AppleTeamId.Length == 10 && !string.IsNullOrWhiteSpace(settings.AppleBundleId)
        && settings.AppleBundleVersions.Length > 0 && settings.AppleValidationCategories.Length > 0;

    public Task<VerifiedOnboardingAttestation?> VerifyAsync(OnboardingAttestation evidence,
        string challenge, string? publicKey, uint counter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured || !Supports(evidence.Platform) || string.IsNullOrWhiteSpace(evidence.Token) || evidence.Token.Length > 32768)
            return Task.FromResult<VerifiedOnboardingAttestation?>(null);
        try
        {
            var data = Convert.FromBase64String(evidence.Token);
            var appId = $"{settings.AppleTeamId}.{settings.AppleBundleId}";
            return Task.FromResult(publicKey is null
                ? VerifyAttestation(data, evidence.KeyId, challenge, appId, clock.GetUtcNow())
                : VerifyAssertion(data, publicKey, counter, challenge, appId,
                    settings.AppleBundleVersions, settings.AppleValidationCategories));
        }
        catch (Exception exception) when (exception is FormatException or CborContentException
            or AsnContentException or CryptographicException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or OverflowException)
        {
            return Task.FromResult<VerifiedOnboardingAttestation?>(null);
        }
    }

    private VerifiedOnboardingAttestation? VerifyAttestation(byte[] data, string? keyId,
        string challenge, string appId, DateTimeOffset now)
    {
        var fields = ReadMap(data);
        if (fields.Count != 3 || ReadText(fields, "fmt") != "apple-appattest" || keyId is null)
            return null;
        var statement = ReadMap(fields["attStmt"]);
        if (!statement.ContainsKey("receipt") || ReadBytes(statement, "receipt").Length == 0)
            return null;
        var certificates = new CborReader(statement["x5c"], CborConformanceMode.Strict);
        if (certificates.ReadStartArray() != 2)
            return null;
        using var leaf = X509CertificateLoader.LoadCertificate(certificates.ReadByteString());
        using var intermediate = X509CertificateLoader.LoadCertificate(certificates.ReadByteString());
        certificates.ReadEndArray();
        if (certificates.BytesRemaining != 0 || !VerifyChain(leaf, intermediate, now)
            || !AppAttestReceiptVerifier.Verify(ReadBytes(statement, "receipt"), leaf, appId, now, receiptRoot))
            return null;

        var auth = ReadBytes(fields, "authData");
        var expectedKeyId = Convert.FromBase64String(keyId);
        if (!ValidAttestationData(auth, expectedKeyId, appId))
            return null;

        using var key = leaf.GetECDsaPublicKey();
        if (key is null || key.KeySize != 256)
            return null;
        var parameters = key.ExportParameters(false);
        if (parameters.Q.X is null || parameters.Q.Y is null)
            return null;
        var point = new byte[] { 4 }.Concat(parameters.Q.X).Concat(parameters.Q.Y).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(point), expectedKeyId)
            || !CoseMatches(auth.AsMemory(87), parameters))
            return null;

        var extension = leaf.Extensions.SingleOrDefault(item => item.Oid?.Value == "1.2.840.113635.100.8.2");
        if (extension is null)
            return null;
        var extensionReader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = extensionReader.ReadSequence();
        var tagged = sequence.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true));
        var nonce = tagged.ReadOctetString();
        tagged.ThrowIfNotEmpty();
        sequence.ThrowIfNotEmpty();
        extensionReader.ThrowIfNotEmpty();
        return CryptographicOperations.FixedTimeEquals(nonce, Nonce(auth, challenge))
            ? new VerifiedOnboardingAttestation(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), 0) : null;
    }

    private static bool ValidAttestationData(byte[] auth, byte[] keyId, string appId)
        => auth.Length >= 87 && (auth[32] & 0x40) != 0 && keyId.Length == 32
           && RpMatches(auth, appId) && BinaryPrimitives.ReadUInt32BigEndian(auth.AsSpan(33, 4)) == 0
           && auth.AsSpan(37, 16).SequenceEqual("appattest\0\0\0\0\0\0\0"u8)
           && BinaryPrimitives.ReadUInt16BigEndian(auth.AsSpan(53, 2)) == 32
           && CryptographicOperations.FixedTimeEquals(auth.AsSpan(55, 32), keyId);

    internal static VerifiedOnboardingAttestation? VerifyAssertion(byte[] data, string publicKey,
        uint counter, string challenge, string appId, string[] versions, uint[] categories)
    {
        var fields = ReadMap(data);
        if (fields.Count != 2)
            return null;
        var auth = ReadBytes(fields, "authenticatorData");
        if (auth.Length < 37 || !RpMatches(auth, appId))
            return null;
        var nextCounter = BinaryPrimitives.ReadUInt32BigEndian(auth.AsSpan(33, 4));
        if (nextCounter <= counter || (auth[32] & 0x40) != 0
            || auth.Length > 37 && !ValidExtensions(auth.AsMemory(37), versions, categories, assertion: true)
            || (auth[32] & 0x80) != 0 && auth.Length == 37)
            return null;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out var bytesRead);
        if (bytesRead != Convert.FromBase64String(publicKey).Length || key.KeySize != 256)
            return null;
        return key.VerifyHash(Nonce(auth, challenge), ReadBytes(fields, "signature"), DSASignatureFormat.Rfc3279DerSequence)
            ? new VerifiedOnboardingAttestation(publicKey, nextCounter) : null;
    }

    private bool CoseMatches(ReadOnlyMemory<byte> data, ECParameters parameters)
    {
        var reader = new CborReader(data, CborConformanceMode.Strict, allowMultipleRootLevelValues: true);
        if (reader.ReadStartMap() != 5)
            return false;
        var seen = new HashSet<int>();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var field = reader.ReadInt32();
            if (!seen.Add(field))
                return false;
            var valid = field switch
            {
                1 => reader.ReadInt32() == 2,
                3 => reader.ReadInt32() == -7,
                -1 => reader.ReadInt32() == 1,
                -2 => reader.ReadByteString().AsSpan().SequenceEqual(parameters.Q.X),
                -3 => reader.ReadByteString().AsSpan().SequenceEqual(parameters.Q.Y),
                _ => false
            };
            if (!valid)
                return false;
        }
        reader.ReadEndMap();
        return reader.BytesRemaining == 0 || ValidExtensions(data[^reader.BytesRemaining..],
            settings.AppleBundleVersions, settings.AppleValidationCategories, assertion: false);
    }

    private static bool ValidExtensions(ReadOnlyMemory<byte> data, string[] versions, uint[] categories, bool assertion)
    {
        var fields = ReadMap(data);
        var versionKey = assertion ? "bundleVersion" : "apple_bundle_version_01";
        var categoryKey = assertion ? "validationCategory" : "apple_validation_category_01";
        var category = ReadBytes(fields, categoryKey);
        return category.Length == 4 && categories.Contains(BinaryPrimitives.ReadUInt32LittleEndian(category))
            && versions.Contains(ReadText(fields, versionKey), StringComparer.Ordinal);
    }

    private bool VerifyChain(X509Certificate2 leaf, X509Certificate2 intermediate, DateTimeOffset now)
    {
        using var root = X509CertificateLoader.LoadCertificate(attestationRoot);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.Add(intermediate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        return chain.Build(leaf) && chain.ChainElements.Count == 3
            && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData);
    }

    private static byte[] ReadRoot(string name, bool pem)
    {
        using var stream = typeof(AppAttestOnboardingVerifier).Assembly.GetManifestResourceStream(
            $"Orbit.Infrastructure.Certificates.{name}") ?? throw new InvalidOperationException("Apple trust anchor is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (!pem)
            return buffer.ToArray();
        using var certificate = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(buffer.ToArray()));
        return certificate.RawData;
    }

    private static byte[] Nonce(byte[] auth, string challenge)
        => SHA256.HashData(auth.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(challenge))).ToArray());
    private static bool RpMatches(byte[] auth, string appId)
        => auth.Length >= 37 && CryptographicOperations.FixedTimeEquals(auth.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(appId)));

    private static Dictionary<string, ReadOnlyMemory<byte>> ReadMap(ReadOnlyMemory<byte> data)
    {
        var reader = new CborReader(data, CborConformanceMode.Strict);
        var length = reader.ReadStartMap();
        if (length is null or > 8)
            throw new CborContentException("Invalid attestation map.");
        var fields = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        for (var index = 0; index < length; index++)
            fields.Add(reader.ReadTextString(), reader.ReadEncodedValue());
        reader.ReadEndMap();
        if (reader.BytesRemaining != 0)
            throw new CborContentException("Trailing attestation data.");
        return fields;
    }

    private static byte[] ReadBytes(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields, string name)
        => new CborReader(fields[name], CborConformanceMode.Strict).ReadByteString();
    private static string ReadText(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields, string name)
        => new CborReader(fields[name], CborConformanceMode.Strict).ReadTextString();
}
