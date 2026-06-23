using FluentAssertions;
using NSubstitute;
using Orbit.Application.Uploads.Commands;
using Orbit.Application.Uploads.Common;
using Orbit.Application.Uploads.Validators;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Uploads;

public class SignUploadCommandHandlerTests
{
    private readonly IObjectStorageService _objectStorage = Substitute.For<IObjectStorageService>();
    private readonly SignUploadCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SignUploadCommandHandlerTests()
    {
        _handler = new SignUploadCommandHandler(_objectStorage);

        _objectStorage
            .CreateSignedUploadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new SignedUpload(
                call.Arg<string>(),
                $"https://project.supabase.co/storage/v1/object/upload/sign/uploads/{call.Arg<string>()}?token=jwt",
                $"https://project.supabase.co/storage/v1/object/public/uploads/{call.Arg<string>()}"));
    }

    [Fact]
    public async Task Handle_ValidCommand_ReturnsSignedUploadScopedToUser()
    {
        var command = new SignUploadCommand(UserId, "avatar.png", "image/png", 1024);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Key.Should().StartWith($"{UserId}/");
        result.Value.Key.Should().EndWith(".png");
        result.Value.SignedUrl.Should().Contain("/object/upload/sign/uploads/");
        result.Value.PublicUrl.Should().Contain("/object/public/uploads/");
    }

    [Fact]
    public async Task Handle_DerivesExtensionFromContentTypeNotFilename()
    {
        var command = new SignUploadCommand(UserId, "photo.heic", "image/jpeg", 2048);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Key.Should().EndWith(".jpg");
    }

    [Fact]
    public async Task Handle_DelegatesUserScopedKeyToStorage()
    {
        var command = new SignUploadCommand(UserId, "avatar.webp", "image/webp", 4096);

        await _handler.Handle(command, CancellationToken.None);

        await _objectStorage.Received(1).CreateSignedUploadAsync(
            Arg.Is<string>(key => key.StartsWith($"{UserId}/") && key.EndsWith(".webp")),
            Arg.Any<CancellationToken>());
    }
}

public class SignUploadValidatorTests
{
    private readonly SignUploadValidator _validator = new();

    private static SignUploadCommand Valid() =>
        new(Guid.NewGuid(), "avatar.png", "image/png", 1024);

    [Fact]
    public void Validate_ValidCommand_Passes()
    {
        _validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DisallowedContentType_Fails()
    {
        var command = Valid() with { ContentType = "application/pdf" };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OversizeFile_Fails()
    {
        var command = Valid() with { SizeBytes = UploadContentTypes.MaxSizeBytes + 1 };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ZeroSize_Fails()
    {
        var command = Valid() with { SizeBytes = 0 };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyFilename_Fails()
    {
        var command = Valid() with { Filename = "" };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }
}
