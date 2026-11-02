using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Validators;

public class ConversationTurnCaptureValidatorTests
{
    private readonly ConversationTurnCaptureValidator _validator = new();
    private static ConversationTurnCapture Valid() => new(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
        "User message", "Assistant response", "[]", true);

    [Fact]
    public void Validate_ValidTurn_IsAccepted() => _validator.Validate(Valid()).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"parts\":[]}", true)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("42", false)]
    [InlineData("not json", false)]
    public void Validate_ResponseMustBeAJsonObject(string json, bool valid)
    {
        var result = _validator.Validate(Valid() with { ResponseJson = json });
        result.IsValid.Should().Be(valid);
        if (!valid) result.Errors.Should().Contain(error => error.PropertyName == nameof(ConversationTurnCapture.ResponseJson));
    }

    [Fact]
    public void Validate_InvalidTurns_AreRejected()
    {
        var turn = Valid();
        ConversationTurnCapture[] invalid = [turn with { UserId = Guid.Empty }, turn with { TurnId = Guid.Empty },
            turn with { CreatedAtUtc = default }, turn with { UserContent = "" }, turn with { ToolCallsJson = "{}" },
            turn with { ToolCallsJson = "not json" }];
        foreach (var item in invalid)
            _validator.Validate(item).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_LargeValidResponse_IsAcceptedWithoutADiagnosticBudget()
    {
        var turn = Valid() with { ResponseJson = System.Text.Json.JsonSerializer.Serialize(new { text = new string('a', 100_000) }) };
        _validator.Validate(turn).IsValid.Should().BeTrue();
    }
}
