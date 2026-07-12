using FluentAssertions;
using Orbit.Api.Observability;
using Sentry;
using Sentry.Protocol;

namespace Orbit.Infrastructure.Tests.Observability;

public class SentryEventScrubberTests
{
    [Fact]
    public void Scrub_RemovesResponseBodyHeadersAndCookies_WhenResponseContextPresent()
    {
        var sentryEvent = new SentryEvent();
        var response = sentryEvent.Contexts.Response;
        response.Data = "{\"token\":\"live-secret\"}";
        response.Cookies = "session=abc";
        response.Headers["Set-Cookie"] = "session=abc";

        SentryEventScrubber.Scrub(sentryEvent, new SentryHint());

        var scrubbed = sentryEvent.Contexts.Response;
        scrubbed.Data.Should().BeNull();
        scrubbed.Cookies.Should().BeNull();
        scrubbed.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Scrub_DoesNotAddResponseContext_WhenNonePresent()
    {
        var sentryEvent = new SentryEvent();

        SentryEventScrubber.Scrub(sentryEvent, new SentryHint());

        sentryEvent.Contexts.ContainsKey(Response.Type).Should().BeFalse();
    }

    [Fact]
    public void Scrub_ClearsUserPii_IncludingOtherDictionary()
    {
        var sentryEvent = new SentryEvent
        {
            User = new SentryUser
            {
                Email = "alice@example.com",
                Username = "alice",
                IpAddress = "203.0.113.7",
            },
        };
        sentryEvent.User.Other["ssn"] = "123-45-6789";

        SentryEventScrubber.Scrub(sentryEvent, new SentryHint());

        sentryEvent.User.Email.Should().BeNull();
        sentryEvent.User.Username.Should().BeNull();
        sentryEvent.User.IpAddress.Should().BeNull();
        sentryEvent.User.Other.Should().BeEmpty();
    }

    [Fact]
    public void Scrub_ClearsRequestBodyCookiesHeadersAndQueryString()
    {
        var sentryEvent = new SentryEvent
        {
            Request = new SentryRequest
            {
                Data = "{\"password\":\"hunter2\"}",
                Cookies = "session=abc",
                QueryString = "token=live-secret&email=alice@example.com",
            },
        };
        sentryEvent.Request.Headers["Authorization"] = "Bearer live-secret";

        SentryEventScrubber.Scrub(sentryEvent, new SentryHint());

        sentryEvent.Request.Data.Should().BeNull();
        sentryEvent.Request.Cookies.Should().BeNull();
        sentryEvent.Request.QueryString.Should().BeNull();
        sentryEvent.Request.Headers.Should().BeEmpty();
    }

    [Fact]
    public void Scrub_RedactsKnownPiiKeysInExtra_ButKeepsBenignKeys()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.SetExtra("authToken", "live-secret");
        sentryEvent.SetExtra("userEmail", "alice@example.com");
        sentryEvent.SetExtra("habitCount", 7);

        SentryEventScrubber.Scrub(sentryEvent, new SentryHint());

        sentryEvent.Extra["authToken"].Should().Be("[Filtered]");
        sentryEvent.Extra["userEmail"].Should().Be("[Filtered]");
        sentryEvent.Extra["habitCount"].Should().Be(7);
    }

    [Fact]
    public void Scrub_ReturnsSameEvent_WhenNoSensitiveDataPresent()
    {
        var sentryEvent = new SentryEvent();

        var result = SentryEventScrubber.Scrub(sentryEvent, new SentryHint());

        result.Should().BeSameAs(sentryEvent);
    }
}
