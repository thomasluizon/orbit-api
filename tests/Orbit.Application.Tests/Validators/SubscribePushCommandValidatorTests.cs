using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Validators;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Validators;

public class SubscribePushCommandValidatorTests
{
    private readonly SubscribePushCommandValidator _validator = new();

    private static SubscribePushCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Endpoint: "https://fcm.googleapis.com/sub/abc123",
        P256dh: "test-p256dh-key-value",
        Auth: "test-auth-value");

    /// <summary>
    /// Sources: https://web.dev/articles/codelab-notifications-push-server,
    /// https://mozilla-services.github.io/autopush-rs/http.html,
    /// https://github.com/andreinwald/webpush-ios-example#subscription-and-saving-token, and
    /// WNS: https://learn.microsoft.com/en-us/windows/apps/develop/notifications/push-notifications/wns-overview; Edge: https://learn.microsoft.com/en-us/answers/questions/5593523/why-notifications-fail-in-azure-notification-hub-a.
    /// </summary>
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/cpqAgzGzkzQ:APA9...")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v1/...")]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMH...")]
    [InlineData("https://cloud.notify.windows.com/?token=test-channel")]
    [InlineData("https://wns2-sg2p.notify.windows.com/w/?token=...")]
    public void Validate_DocumentedProviderEndpoint_NoErrors(string endpoint)
    {
        _validator.TestValidate(ValidCommand() with { Endpoint = endpoint }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NativeFcmToken_NoErrors()
    {
        var command = ValidCommand() with { Endpoint = "native-device-token", P256dh = PushSubscription.FcmSentinel };

        _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ValidInput_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_UnsupportedHttpsEndpoint_HasError()
    {
        var command = ValidCommand() with { Endpoint = "https://example.com/push" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Endpoint)
            .WithErrorCode(ErrorCodes.PushEndpointInvalid)
            .WithErrorMessage(ErrorMessages.PushEndpointInvalid.Message);
    }

    [Fact]
    public void Validate_EmptyEndpoint_HasError()
    {
        var command = ValidCommand() with { Endpoint = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_EndpointOver2000Chars_HasError()
    {
        var command = ValidCommand() with { Endpoint = new string('a', 2001) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_EmptyP256dh_HasError()
    {
        var command = ValidCommand() with { P256dh = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.P256dh);
    }

    [Fact]
    public void Validate_EmptyAuth_HasError()
    {
        var command = ValidCommand() with { Auth = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Auth);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
    [Theory]
    [InlineData("https://dm3p.notify.windows.com/?token=channel", true)]
    [InlineData("https://notify.windows.com.attacker.org/", false)]
    [InlineData("https://fcm.googleapis.com/channel", false)]
    public void Validate_Wns_EnforcesProviderPolicy(string endpoint, bool allowed)
    {
        var command = ValidCommand() with { Endpoint = endpoint, P256dh = "wns", DeviceId = Guid.NewGuid() };
        var result = _validator.TestValidate(command);
        if (allowed)
            result.ShouldNotHaveAnyValidationErrors();
        else
            result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_Wns_RequiresDeviceId()
    {
        var command = ValidCommand() with { Endpoint = "https://dm3p.notify.windows.com/?token=channel", P256dh = "wns" };
        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.DeviceId);
        _validator.TestValidate(command with { DeviceId = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.DeviceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("org.useorbit.desktop")]
    [InlineData("org.useorbit.desktop.staging")]
    public void Validate_Apns_AcceptsExistingIosOrDesktopTopic(string? topic)
    {
        _validator.TestValidate(ValidCommand() with { Endpoint = "token", P256dh = "apns", ApnsTopic = topic })
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_Topic_RejectsOtherAppsAndTransports()
    {
        _validator.TestValidate(ValidCommand() with { Endpoint = "token", P256dh = "apns", ApnsTopic = "org.attacker.app" })
            .ShouldHaveValidationErrorFor(x => x.ApnsTopic);
        _validator.TestValidate(ValidCommand() with { ApnsTopic = "org.useorbit.desktop" })
            .ShouldHaveValidationErrorFor(x => x.ApnsTopic);
    }
}
