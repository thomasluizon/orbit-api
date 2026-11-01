using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Validators;

public sealed class ChatConversationCaptureValidatorTests
{
    [Fact]
    public void StorageRequiresConversationIdentityAndTypedImage()
    {
        var valid = new ChatConversationCapture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
            "Create this task", null, [137, 80, 78, 71], "image/png", []);
        var validator = new ChatConversationCaptureValidator();
        validator.Validate(valid).IsValid.Should().BeTrue();
        validator.Validate(valid with { ConversationId = Guid.Empty }).IsValid.Should().BeFalse();
        validator.Validate(valid with { ImageMimeType = "text/html" }).IsValid.Should().BeFalse();
        validator.Validate(valid with { ImageData = null, ImageMimeType = null }).IsValid.Should().BeTrue();
    }
}
