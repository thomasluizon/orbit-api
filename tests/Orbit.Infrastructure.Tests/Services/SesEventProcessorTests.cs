using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class SesEventProcessorTests : IDisposable
{
    private const string Topic = "arn:aws:sns:us-east-2:713285551626:orbit-ses-events";
    private const string CertificateUrl = "https://sns.us-east-2.amazonaws.com/SimpleNotificationService-123abc.pem";
    private const string SubscribeUrl = "https://sns.us-east-2.amazonaws.com/?Action=ConfirmSubscription&TopicArn=arn%3Aaws%3Asns%3Aus-east-2%3A713285551626%3Aorbit-ses-events&Token=abc";
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    private readonly IGenericRepository<MarketingContact> _contacts = Substitute.For<IGenericRepository<MarketingContact>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<MarketingContact> _added = [];
    private readonly List<string> _fetchedUrls = [];
    private HttpStatusCode _certificateStatus = HttpStatusCode.OK;
    private string _certificateFailure = "none";
    private readonly SesEventProcessor _processor;

    public SesEventProcessorTests()
    {
        var request = new CertificateRequest("CN=Test SNS", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(call => new HttpClient(new StubHandler(
            _certificate.ExportCertificatePem(), _fetchedUrls, () => _certificateStatus,
            () => _certificateFailure)));
        _contacts.AddAsync(Arg.Do<MarketingContact>(_added.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => ((Func<CancellationToken, Task>)call[0]!)(CancellationToken.None));
        var settings = Options.Create(new SesSettings { TopicArn = Topic });
        var verifier = new SnsMessageVerifier(factory, settings, new MemoryCache(new MemoryCacheOptions()), _certificate);
        _processor = new SesEventProcessor(verifier, factory, _contacts, _unitOfWork,
            NullLogger<SesEventProcessor>.Instance);
    }

    [Theory]
    [InlineData("Bounce", "Permanent", true)]
    [InlineData("Complaint", "", true)]
    [InlineData("Bounce", "Transient", false)]
    public async Task SignedEventsSuppressOnlyPermanentBounceAndComplaint(string eventName, string bounceType, bool suppressed)
    {
        var content = eventName == "Bounce"
            ? JsonSerializer.Serialize(new { eventType = eventName, bounce = new { bounceType, bouncedRecipients = new[] { new { emailAddress = "Person@Example.com" } } } })
            : JsonSerializer.Serialize(new { eventType = eventName, complaint = new { complainedRecipients = new[] { new { emailAddress = "Person@Example.com" } } } });
        (await _processor.ProcessAsync(Signed("Notification", content), CancellationToken.None)).Should().BeTrue();
        if (suppressed)
        {
            var contact = _added.Should().ContainSingle().Which;
            contact.Email.Should().Be("person@example.com");
            contact.SuppressedAtUtc.Should().NotBeNull();
            contact.UnsubscribedAtUtc.Should().NotBeNull();
        }
        else
            _added.Should().BeEmpty();
    }

    [Fact]
    public async Task SignedSubscriptionConfirmationFetchesSubscribeUrl()
    {
        (await _processor.ProcessAsync(Signed("SubscriptionConfirmation", "Confirm"), CancellationToken.None)).Should().BeTrue();
        _fetchedUrls.Should().Contain(SubscribeUrl);
    }

    [Fact]
    public async Task VersionOneSignatureIsAccepted()
    {
        var content = JsonSerializer.Serialize(new { eventType = "Bounce", bounce = new { bounceType = "Permanent", bouncedRecipients = new[] { new { emailAddress = "person@example.com" } } } });
        (await _processor.ProcessAsync(Signed("Notification", content, "1"), CancellationToken.None)).Should().BeTrue();
        _added.Should().ContainSingle();
    }

    [Fact]
    public async Task WrongTopicIsRejectedBeforeFetchingCertificateOrConfirming()
    {
        var payload = Signed("SubscriptionConfirmation", "Confirm").Replace(Topic, "arn:aws:sns:us-east-2:713285551626:other");
        (await _processor.ProcessAsync(payload, CancellationToken.None)).Should().BeFalse();
        _fetchedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatedSignedEnvelopesFetchCertificateOnce()
    {
        var payload = Signed("Notification", JsonSerializer.Serialize(new { eventType = "Delivery" }));

        (await _processor.ProcessAsync(payload, CancellationToken.None)).Should().BeTrue();
        (await _processor.ProcessAsync(payload, CancellationToken.None)).Should().BeTrue();

        _fetchedUrls.Should().ContainSingle().Which.Should().Be(CertificateUrl);
    }

    [Fact]
    public async Task TamperedSignatureIsRejected()
    {
        var payload = Signed("Notification", "Message").Replace("\"Message\":\"Message\"", "\"Message\":\"Tampered\"");
        (await _processor.ProcessAsync(payload, CancellationToken.None)).Should().BeFalse();
        _added.Should().BeEmpty();
    }

    [Fact]
    public async Task CertificateOnAnotherHostIsRejectedBeforeFetch()
    {
        var payload = Signed("Notification", "Message").Replace(CertificateUrl,
            "https://sns.us-east-2.amazonaws.com.evil.example/SimpleNotificationService-123abc.pem");
        (await _processor.ProcessAsync(payload, CancellationToken.None)).Should().BeFalse();
        _fetchedUrls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task CertificateFetchFailureIsRetryable(string failure)
    {
        _certificateStatus = HttpStatusCode.ServiceUnavailable;
        _certificateFailure = failure;
        var payload = Signed("Notification", JsonSerializer.Serialize(new { eventType = "Complaint", complaint = new { complainedRecipients = new[] { new { emailAddress = "person@example.com" } } } }));

        var action = () => _processor.ProcessAsync(payload, CancellationToken.None);

        await action.Should().ThrowAsync<SnsCertificateFetchException>();
        _added.Should().BeEmpty();
    }

    private string Signed(string type, string message, string version = "2")
    {
        var fields = new Dictionary<string, string>
        {
            ["Type"] = type,
            ["MessageId"] = "165545c9-2a5c-472c-8df2-7ff2be2b3b1b",
            ["TopicArn"] = Topic,
            ["Message"] = message,
            ["Timestamp"] = "2026-09-27T00:00:00.000Z",
            ["SignatureVersion"] = version,
            ["SigningCertURL"] = CertificateUrl,
        };
        if (type == "SubscriptionConfirmation")
        {
            fields["SubscribeURL"] = SubscribeUrl;
            fields["Token"] = "abc";
        }
        var order = type == "Notification"
            ? new[] { "Message", "MessageId", "Timestamp", "TopicArn", "Type" }
            : new[] { "Message", "MessageId", "SubscribeURL", "Timestamp", "Token", "TopicArn", "Type" };
        var canonical = string.Concat(order.Select(name => $"{name}\n{fields[name]}\n"));
        fields["Signature"] = Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(canonical), version == "2" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        return JsonSerializer.Serialize(fields);
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _key.Dispose();
    }

    private sealed class StubHandler(string certificatePem, List<string> fetchedUrls,
        Func<HttpStatusCode> certificateStatus, Func<string> certificateFailure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            fetchedUrls.Add(url);
            if (url == CertificateUrl)
            {
                if (certificateFailure() == "transport")
                    throw new HttpRequestException("Certificate fetch failed");
                if (certificateFailure() == "timeout")
                    throw new TaskCanceledException("Certificate fetch timed out");
            }
            return Task.FromResult(new HttpResponseMessage(url == CertificateUrl ? certificateStatus() : HttpStatusCode.OK)
            {
                Content = new StringContent(url == CertificateUrl ? certificatePem : "confirmed")
            });
        }
    }
}
