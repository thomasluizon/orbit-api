using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SupabaseObjectStorageServiceRetryTests
{
    private const string SignedUrlBody = "{\"url\":\"/object/upload/sign/uploads/key?token=signed\"}";

    private static (SupabaseObjectStorageService Sut, SequencedHttpMessageHandler Handler) Build(SequencedHttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://project.supabase.co") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(SupabaseObjectStorageService.HttpClientName).Returns(client);

        var settings = Options.Create(new SupabaseStorageSettings
        {
            Url = "https://project.supabase.co",
            SecretKey = "secret",
        });

        var sut = new SupabaseObjectStorageService(factory, settings, NullLogger<SupabaseObjectStorageService>.Instance);
        return (sut, handler);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1001)]
    public async Task DeleteUserObjectsAsync_DrainsAllPagesIncludingNestedKeys(int count)
    {
        var userId = Guid.NewGuid();
        var prefix = $"{userId}/";
        var other = $"{Guid.NewGuid()}/keep.png";
        var keys = Enumerable.Range(0, count).Select(i => $"{prefix}nested/{i}.png").ToHashSet();
        keys.Add(other);
        using var handler = new ObjectStoreHandler(keys);
        var sut = BuildDeletionService(handler);

        await sut.DeleteUserObjectsAsync(userId);

        keys.Should().BeEquivalentTo([other]);
        handler.Listings.Should().Be(count == 0 ? 1 : count > 1000 ? 3 : 2);
        handler.Deletions.Should().Be(count == 0 ? 0 : count > 1000 ? 2 : 1);
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_RetriesFailedDeleteAndVerifiesEmptyPrefix()
    {
        var userId = Guid.NewGuid();
        using var handler = new ObjectStoreHandler([$"{userId}/one.png"]) { FailuresRemaining = 1 };

        await BuildDeletionService(handler).DeleteUserObjectsAsync(userId);

        handler.Deletions.Should().Be(2);
        handler.Keys.Should().BeEmpty();
        handler.Listings.Should().Be(2);
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_PersistentFailureDoesNotReportSuccess()
    {
        var userId = Guid.NewGuid();
        using var handler = new ObjectStoreHandler([$"{userId}/one.png"]) { FailuresRemaining = 3 };
        var act = () => BuildDeletionService(handler).DeleteUserObjectsAsync(userId);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Deletions.Should().Be(3);
        handler.Keys.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_NoProgressDoesNotReportSuccess()
    {
        var userId = Guid.NewGuid();
        using var handler = new ObjectStoreHandler([$"{userId}/one.png"]) { KeepObjects = true };
        var act = () => BuildDeletionService(handler).DeleteUserObjectsAsync(userId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*same objects*");
    }

    [Fact]
    public async Task DeleteUserObjectsAsync_RejectsEmptyOwner()
    {
        using var handler = new ObjectStoreHandler([]);
        var act = () => BuildDeletionService(handler).DeleteUserObjectsAsync(Guid.Empty);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Listings.Should().Be(0);
    }

    private static SupabaseObjectStorageService BuildDeletionService(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(SupabaseObjectStorageService.HttpClientName)
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://project.supabase.co") });
        return new SupabaseObjectStorageService(factory, Options.Create(new SupabaseStorageSettings
        {
            Url = "https://project.supabase.co",
            SecretKey = "secret",
        }), NullLogger<SupabaseObjectStorageService>.Instance);
    }

    private sealed class ObjectStoreHandler(HashSet<string> keys) : HttpMessageHandler
    {
        public HashSet<string> Keys => keys;
        public int Listings { get; private set; }
        public int Deletions { get; private set; }
        public int FailuresRemaining { get; set; }
        public bool KeepObjects { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.Method == HttpMethod.Post)
            {
                request.RequestUri!.AbsolutePath.Should().Be("/storage/v1/object/list-v2/uploads");
                Listings++;
                var prefix = body.RootElement.GetProperty("prefix").GetString()!;
                body.RootElement.GetProperty("with_delimiter").GetBoolean().Should().BeFalse();
                var objects = keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Take(body.RootElement.GetProperty("limit").GetInt32()).Select(name => new { name }).ToArray();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { objects, folders = Array.Empty<object>(), hasNext = false }),
                };
            }

            request.Method.Should().Be(HttpMethod.Delete);
            request.RequestUri!.AbsolutePath.Should().Be("/storage/v1/object/uploads");
            Deletions++;
            if (FailuresRemaining-- > 0)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (!KeepObjects)
                foreach (var key in body.RootElement.GetProperty("prefixes").EnumerateArray())
                    keys.Remove(key.GetString()!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };
        }
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransient5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadGateway),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK, SignedUrlBody)));

        var result = await sut.CreateSignedUploadAsync("key", "image/png", 1024);

        handler.CallCount.Should().Be(2);
        result.SignedUrl.Should().Contain("token=signed");
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransportException()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK, SignedUrlBody)));

        var result = await sut.CreateSignedUploadAsync("key", "image/png", 1024);

        handler.CallCount.Should().Be(2);
        result.SignedUrl.Should().Contain("token=signed");
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_OnPersistent5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable)));

        var act = () => sut.CreateSignedUploadAsync("key", "image/png", 1024);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task DoesNotRetry_OnNonTransient4xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadRequest)));

        var act = () => sut.CreateSignedUploadAsync("key", "image/png", 1024);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.CallCount.Should().Be(1);
    }
}
