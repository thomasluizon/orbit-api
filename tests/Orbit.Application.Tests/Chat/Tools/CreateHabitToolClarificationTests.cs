using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

/// <summary>
/// Tests the NeedsClarification heuristic: when frequency_unit is absent AND the title
/// contains habit/rotina/hábito, the tool returns a ClarificationRequest payload instead
/// of creating a one-time task.
/// </summary>
public class CreateHabitToolClarificationTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 5, 19);

    public CreateHabitToolClarificationTests()
    {
        _tool = new CreateHabitTool(_habitRepo, _tagRepo, _goalRepo, _userDateService, _payGate, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    [Theory]
    [InlineData("My morning habit")]
    [InlineData("Meditation habit")]
    [InlineData("MORNING HABIT")] // case-insensitive
    [InlineData("Daily Habit Routine")]
    [InlineData("Minha rotina matinal")] // pt-BR
    [InlineData("Rotina de leitura")]
    [InlineData("Meu hábito de meditar")] // pt-BR with accent
    [InlineData("Hábito de exercício")]
    public async Task HabitFlavoredTitle_NoFrequency_ReturnsClarification(string title)
    {
        var result = await Execute($$"""{"title": "{{title}}"}""");

        result.Success.Should().BeTrue();
        result.Payload.Should().BeOfType<NeedsClarificationPayload>();
        var payload = (NeedsClarificationPayload)result.Payload!;
        payload.MissingArgumentKey.Should().Be("frequency_unit");
        payload.QuickActions.Should().HaveCount(4);

        // Assert on the JSON merge patches (the load-bearing contract), not the i18n key labels.
        // Each patch is what gets shallow-merged into the partial args at resolve time.
        payload.QuickActions.Should().Contain(a =>
            a.Value.Contains("\"frequency_unit\":\"Day\"") && a.Value.Contains("\"frequency_quantity\":1"));
        payload.QuickActions.Should().Contain(a =>
            a.Value.Contains("\"frequency_unit\":\"Week\"")
            && a.Value.Contains("\"frequency_quantity\":1")
            && !a.Value.Contains("is_flexible"));
        payload.QuickActions.Should().Contain(a =>
            a.Value.Contains("\"frequency_unit\":\"Week\"")
            && a.Value.Contains("\"frequency_quantity\":3")
            && a.Value.Contains("\"is_flexible\":true"));
        payload.QuickActions.Should().Contain(a =>
            a.Value.Contains("\"frequency_unit\":null"));

        // Tool must NOT have created a habit
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitFlavoredTitle_WithFrequency_CreatesNormally()
    {
        var result = await Execute("""{"title": "Morning habit", "frequency_unit": "Day", "frequency_quantity": 1}""");

        result.Success.Should().BeTrue();
        result.Payload.Should().BeNull();
        result.EntityId.Should().NotBeNullOrEmpty();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonHabitFlavoredTitle_NoFrequency_CreatesOneTimeTask()
    {
        var result = await Execute("""{"title": "Call the dentist on Friday"}""");

        result.Success.Should().BeTrue();
        result.Payload.Should().BeNull();
        result.EntityId.Should().NotBeNullOrEmpty();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitFlavoredTitle_WithExplicitNullFrequency_CreatesOneTimeTask()
    {
        // After a clarification resolves with the "One-time" patch, the merged args
        // include "frequency_unit": null. The presence of the key bypasses the check.
        var result = await Execute("""{"title": "Habit thing", "frequency_unit": null}""");

        result.Success.Should().BeTrue();
        result.Payload.Should().BeNull();
        result.EntityId.Should().NotBeNullOrEmpty();
        await _habitRepo.Received(1).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTitle_StillReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
        result.Payload.Should().BeNull();
    }

    [Fact]
    public async Task ClarificationQuickActions_ContainValidJsonPatches()
    {
        var result = await Execute("""{"title": "Morning habit"}""");

        var payload = (NeedsClarificationPayload)result.Payload!;
        foreach (var action in payload.QuickActions)
        {
            var parsed = () => JsonDocument.Parse(action.Value);
            parsed.Should().NotThrow($"QuickAction '{action.Label}' value should be valid JSON");
        }
    }

    [Fact]
    public async Task ClarificationPayload_DoesNotCarryOperationId()
    {
        // OperationId is owned by the chat handler — the tool's payload type doesn't
        // even expose the field. Keeps the tool decoupled from the store's id minting.
        var result = await Execute("""{"title": "Morning habit"}""");

        result.Payload.Should().BeOfType<NeedsClarificationPayload>();
        // No OperationId property on NeedsClarificationPayload — confirmed by the type.
    }

    private async Task<ToolResult> Execute(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return await _tool.ExecuteAsync(doc.RootElement, UserId, CancellationToken.None);
    }
}
