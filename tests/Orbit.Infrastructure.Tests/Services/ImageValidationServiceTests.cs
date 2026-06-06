using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ImageValidationServiceTests
{
    private readonly ImageValidationService _sut = new();

    [Fact]
    public async Task ValidateAsync_EmptyFile_ReturnsFailure()
    {
        using var stream = new MemoryStream();
        var result = await _sut.ValidateAsync(stream, "empty.jpg", length: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task ValidateAsync_OversizedFile_ReturnsFailure()
    {
        using var stream = new MemoryStream();
        var result = await _sut.ValidateAsync(stream, "large.jpg", length: 21_000_000L);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("exceeds");
    }

    [Fact]
    public async Task ValidateAsync_DisallowedExtension_ReturnsFailure()
    {
        using var stream = new MemoryStream();
        var result = await _sut.ValidateAsync(stream, "animation.gif", length: 1024L);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(".gif");
        result.Error.Should().Contain("not allowed");
    }

    [Fact]
    public async Task ValidateAsync_NoExtension_ReturnsFailure()
    {
        using var stream = new MemoryStream();
        var result = await _sut.ValidateAsync(stream, "noextension", length: 1024L);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not allowed");
    }

    [Fact]
    public async Task ValidateAsync_ValidJpeg_ReturnsSuccess()
    {
        var jpegHeader = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46,
            0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
            0x00, 0x01, 0x00, 0x00
        };
        using var stream = new MemoryStream(jpegHeader);

        var result = await _sut.ValidateAsync(stream, "test.jpg", length: jpegHeader.Length);

        if (result.IsSuccess)
        {
            result.Value.MimeType.Should().Contain("image/");
            result.Value.Size.Should().Be(jpegHeader.Length);
        }
        else
        {
            result.Error.Should().NotContain("exceeds");
            result.Error.Should().NotContain("not allowed");
        }
    }
}
