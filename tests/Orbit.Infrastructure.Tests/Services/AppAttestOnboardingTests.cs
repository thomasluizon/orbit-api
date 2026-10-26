using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AppAttestOnboardingTests
{
    [Fact]
    public async Task ApplePublishedSample_WithRawChallenge_IsRefusedByHashedChallengeProtocol()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "AppAttest", "AppleValidationGuide.json")));
        var settings = new OnboardingSecuritySettings
        {
            AppleTeamId = "1234567890",
            AppleBundleId = "com.example.myapp",
            AppleBundleVersions = ["1"],
            AppleValidationCategories = [1]
        };
        var verifier = new AppAttestOnboardingVerifier(Options.Create(settings), new PublishedSampleClock());
        var evidence = new OnboardingAttestation("ios", fixture.RootElement.GetProperty("attestationObject").GetString()!,
            fixture.RootElement.GetProperty("keyId").GetString());
        var reader = new CborReader(Convert.FromBase64String(evidence.Token));
        reader.ReadStartMap();
        byte[]? authenticatorData = null;
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            if (reader.ReadTextString() == "authData")
                authenticatorData = reader.ReadByteString();
            else
                reader.SkipValue();
        }
        authenticatorData.Should().NotBeNull();
        var challenge = Encoding.UTF8.GetBytes("example_server_challenge");
        var publishedNonce = Convert.FromBase64String("h7fQbZOkKU5G8BHma2zEAPC6sgcpl2xhlYC0KuYL/24=");
        SHA256.HashData(authenticatorData!.Concat(challenge).ToArray()).Should().Equal(publishedNonce);
        SHA256.HashData(authenticatorData.Concat(SHA256.HashData(challenge)).ToArray()).Should().NotEqual(publishedNonce);

        (await verifier.VerifyAsync(evidence, "example_server_challenge", null, 0, default)).Should().BeNull();
    }

    [Fact]
    public async Task AttestationAndAssertion_ValidateChainNonceKeyAppIdAndCounter()
    {
        using var fixture = new AttestFixture();
        var verifier = fixture.Verifier();
        var attestation = await verifier.VerifyAsync(fixture.Evidence, AttestFixture.Challenge, null, 0, default);
        attestation.Should().NotBeNull();
        attestation!.PublicKey.Should().Be(Convert.ToBase64String(fixture.Key.ExportSubjectPublicKeyInfo()));
        var assertion = fixture.Assertion(1);
        var first = await verifier.VerifyAsync(assertion, AttestFixture.Challenge, attestation.PublicKey, 0, default);
        first!.Counter.Should().Be(1);
        (await verifier.VerifyAsync(assertion, AttestFixture.Challenge, attestation.PublicKey, 1, default)).Should().BeNull();
        (await verifier.VerifyAsync(assertion, "different-challenge", attestation.PublicKey, 0, default)).Should().BeNull();
        (await verifier.VerifyAsync(fixture.Assertion(2, "other.app"), AttestFixture.Challenge,
            attestation.PublicKey, 1, default)).Should().BeNull();
    }

    [Fact]
    public async Task ProductionTrustAnchor_RejectsLocallySignedAttestation()
    {
        using var fixture = new AttestFixture();
        var verifier = new AppAttestOnboardingVerifier(Options.Create(AttestFixture.Settings()), new AttestClock());
        (await verifier.VerifyAsync(fixture.Evidence, AttestFixture.Challenge, null, 0, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("counter")]
    [InlineData("nonce")]
    [InlineData("app-id")]
    [InlineData("development")]
    [InlineData("key-id")]
    [InlineData("key-hash")]
    [InlineData("cose")]
    [InlineData("receipt-app")]
    [InlineData("receipt-age")]
    [InlineData("receipt-signature")]
    [InlineData("receipt-type")]
    public async Task AttestationDefect_IsRefused(string defect)
    {
        using var fixture = new AttestFixture(defect);
        (await fixture.Verifier().VerifyAsync(fixture.Evidence, AttestFixture.Challenge, null, 0, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("oA==")]
    public async Task MalformedProof_IsRefused(string token)
    {
        using var fixture = new AttestFixture();
        (await fixture.Verifier().VerifyAsync(new OnboardingAttestation("ios", token), AttestFixture.Challenge,
            null, 0, default)).Should().BeNull();
    }

    private sealed class AttestClock : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PublishedSampleClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 4, 21, 18, 14, 0, TimeSpan.Zero);
    }

    private sealed class AttestFixture : IDisposable
    {
        internal const string Challenge = "server-issued-one-time-challenge";
        private const string AppId = "ABCDEFGHIJ.org.useorbit.app";
        private readonly ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        private readonly ECDsa intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        internal readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly X509Certificate2 root;
        private readonly X509Certificate2 intermediate;
        private readonly X509Certificate2 leaf;
        internal OnboardingAttestation Evidence { get; }

        internal static OnboardingSecuritySettings Settings() => new()
        {
            AppleTeamId = "ABCDEFGHIJ",
            AppleBundleId = "org.useorbit.app",
            AppleBundleVersions = ["1"],
            AppleValidationCategories = [4]
        };

        internal AppAttestOnboardingVerifier Verifier() => new(Options.Create(Settings()), new AttestClock(), root.RawData, root.RawData);

        internal AttestFixture(string defect = "")
        {
            var now = AttestClock.Now;
            var rootRequest = new CertificateRequest("CN=Test Attest Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
            root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var issuerRequest = new CertificateRequest("CN=Test Attest CA", intermediateKey, HashAlgorithmName.SHA256);
            issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            using var issuerCertificate = issuerRequest.Create(root, now.AddHours(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
            intermediate = issuerCertificate.CopyWithPrivateKey(intermediateKey);
            var parameters = Key.ExportParameters(false);
            var keyId = SHA256.HashData(new byte[] { 4 }.Concat(parameters.Q.X!).Concat(parameters.Q.Y!).ToArray());
            if (defect == "key-hash")
                keyId = new byte[32];
            var auth = new byte[87];
            SHA256.HashData(Encoding.UTF8.GetBytes(defect == "app-id" ? "other.app" : AppId)).CopyTo(auth, 0);
            auth[32] = 0x40;
            BinaryPrimitives.WriteUInt32BigEndian(auth.AsSpan(33, 4), defect == "counter" ? 1u : 0u);
            Encoding.ASCII.GetBytes(defect == "development" ? "appattestdevelop" : "appattest\0\0\0\0\0\0\0").CopyTo(auth, 37);
            BinaryPrimitives.WriteUInt16BigEndian(auth.AsSpan(53, 2), 32);
            keyId.CopyTo(auth, 55);
            var cose = new CborWriter();
            cose.WriteStartMap(5);
            cose.WriteInt32(1); cose.WriteInt32(2);
            cose.WriteInt32(3); cose.WriteInt32(-7);
            cose.WriteInt32(-1); cose.WriteInt32(1);
            cose.WriteInt32(-2); cose.WriteByteString(defect == "cose" ? new byte[32] : parameters.Q.X!);
            cose.WriteInt32(-3); cose.WriteByteString(parameters.Q.Y!);
            cose.WriteEndMap();
            auth = auth.Concat(cose.Encode()).ToArray();
            var nonce = SHA256.HashData(auth.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(Challenge))).ToArray());
            var extension = new AsnWriter(AsnEncodingRules.DER);
            extension.PushSequence();
            var tag = new Asn1Tag(TagClass.ContextSpecific, 1, true);
            extension.PushSequence(tag);
            extension.WriteOctetString(defect == "nonce" ? new byte[32] : nonce);
            extension.PopSequence(tag);
            extension.PopSequence();
            var request = new CertificateRequest("CN=Test App Key", Key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.8.2", extension.Encode(), false));
            leaf = request.Create(intermediate, now.AddMinutes(-10), now.AddMinutes(10), RandomNumberGenerator.GetBytes(16));
            var receipt = Receipt(defect);
            if (defect == "receipt-signature")
                receipt[^1] ^= 1;
            var writer = new CborWriter();
            writer.WriteStartMap(3);
            writer.WriteTextString("fmt"); writer.WriteTextString("apple-appattest");
            writer.WriteTextString("attStmt"); writer.WriteStartMap(2);
            writer.WriteTextString("x5c"); writer.WriteStartArray(2);
            writer.WriteByteString(leaf.RawData); writer.WriteByteString(intermediate.RawData); writer.WriteEndArray();
            writer.WriteTextString("receipt"); writer.WriteByteString(receipt); writer.WriteEndMap();
            writer.WriteTextString("authData"); writer.WriteByteString(auth); writer.WriteEndMap();
            Evidence = new("ios", Convert.ToBase64String(writer.Encode()),
                Convert.ToBase64String(defect == "key-id" ? new byte[32] : keyId));
        }

        private byte[] Receipt(string defect)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSetOf();
            var fields = new Dictionary<int, byte[]>
            {
                [2] = Encoding.UTF8.GetBytes(defect == "receipt-app" ? "other.app" : AppId),
                [3] = leaf.RawData,
                [6] = Encoding.UTF8.GetBytes(defect == "receipt-type" ? "RECEIPT" : "ATTEST"),
                [12] = Encoding.UTF8.GetBytes((defect == "receipt-age" ? AttestClock.Now.AddHours(-1) : AttestClock.Now).ToString("O"))
            };
            foreach (var (type, value) in fields)
            {
                writer.PushSequence(); writer.WriteInteger(type); writer.WriteInteger(1);
                writer.WriteOctetString(value); writer.PopSequence();
            }
            writer.PopSetOf();
            var cms = new SignedCms(new ContentInfo(writer.Encode()));
            cms.ComputeSignature(new CmsSigner(root));
            return cms.Encode();
        }

        internal OnboardingAttestation Assertion(uint counter, string appId = AppId)
        {
            var auth = new byte[37];
            SHA256.HashData(Encoding.UTF8.GetBytes(appId)).CopyTo(auth, 0);
            BinaryPrimitives.WriteUInt32BigEndian(auth.AsSpan(33, 4), counter);
            var nonce = SHA256.HashData(auth.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(Challenge))).ToArray());
            var signature = Key.SignHash(nonce, DSASignatureFormat.Rfc3279DerSequence);
            var writer = new CborWriter();
            writer.WriteStartMap(2); writer.WriteTextString("authenticatorData"); writer.WriteByteString(auth);
            writer.WriteTextString("signature"); writer.WriteByteString(signature); writer.WriteEndMap();
            return new("ios", Convert.ToBase64String(writer.Encode()));
        }

        public void Dispose()
        {
            leaf.Dispose(); intermediate.Dispose(); root.Dispose(); Key.Dispose(); intermediateKey.Dispose(); rootKey.Dispose();
        }
    }
}
