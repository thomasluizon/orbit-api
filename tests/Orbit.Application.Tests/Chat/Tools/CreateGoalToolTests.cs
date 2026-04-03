using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class CreateGoalToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateGoalTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateGoalToolTests()
    {
        _tool = new CreateGoalTool(_goalRepo, _unitOfWork);
    }

    [Fact]
    public async Task SuccessfulCreation_ReturnsSuccessWithTitle()
    {
        var result = await Execute("""{"title": "Read 12 books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read 12 books");
        result.EntityId.Should().NotBeNullOrEmpty();
        await _goalRepo.Received(1).AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTitle_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task EmptyTitle_ReturnsError()
    {
        var result = await Execute("""{"title": "  "}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task WithTargetAndUnit_CreatesGoalCorrectly()
    {
        var result = await Execute("""{"title": "Read books", "target_value": 12, "unit": "books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read books");
    }

    [Fact]
    public async Task WithDeadline_CreatesGoalWithDeadline()
    {
        var result = await Execute("""{"title": "Lose weight", "target_value": 5, "unit": "kg", "deadline": "2026-12-31"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Lose weight");
    }

    [Fact]
    public async Task DefaultTargetValue_UsesOne()
    {
        var result = await Execute("""{"title": "Complete goal"}""");

        result.Success.Should().BeTrue();
        // Default target_value = 1, default unit = "goal"
        result.EntityName.Should().Be("Complete goal");
    }

    [Fact]
    public async Task WithDescription_CreatesGoalWithDescription()
    {
        var result = await Execute("""{"title": "Save money", "description": "For vacation fund", "target_value": 5000, "unit": "dollars"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Save money");
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
