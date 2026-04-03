using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public class ProcessUserChatCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserFact> _userFactRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IAiIntentService _aiIntentService = Substitute.For<IAiIntentService>();
    private readonly ISystemPromptBuilder _promptBuilder = Substitute.For<ISystemPromptBuilder>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly ILogger<ProcessUserChatCommandHandler> _logger = Substitute.For<ILogger<ProcessUserChatCommandHandler>>();
    private readonly ProcessUserChatCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public ProcessUserChatCommandHandlerTests()
    {
        var toolRegistry = new AiToolRegistry([]);
        var aiDeps = new ChatAiDependencies(_aiIntentService, toolRegistry, _promptBuilder);
        var dataDeps = new ChatDataDependencies(_habitRepo, _userRepo, _userFactRepo, _tagRepo);

        _handler = new ProcessUserChatCommandHandler(
            dataDeps, aiDeps, _userDateService, _payGate, _unitOfWork, _scopeFactory, _logger);

        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _promptBuilder.Build(Arg.Any<PromptBuildRequest>()).Returns("system prompt");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _userFactRepo.FindAsync(
            Arg.Any<Expression<Func<UserFact, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserFact>().AsReadOnly());

        _tagRepo.FindAsync(
            Arg.Any<Expression<Func<Tag, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Tag>().AsReadOnly());
    }

    [Fact]
    public async Task Handle_PayGateBlocks_ReturnsPayGateError()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("AI message limit reached."));

        var command = new ProcessUserChatCommand(UserId, "Hello AI");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_AiServiceFails_ReturnsFailure()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>("AI service unavailable"));

        var command = new ProcessUserChatCommand(UserId, "Hello AI");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AI service unavailable");
    }

    [Fact]
    public async Task Handle_SuccessfulResponse_ReturnsChatResponse()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var aiResponse = new AiResponse { TextMessage = "Hello! How can I help?", ToolCalls = null };
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(aiResponse));

        var command = new ProcessUserChatCommand(UserId, "Hello AI");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Hello! How can I help?");
        result.Value.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_AiResponseWithJsonWrapper_StripsWrapper()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var wrappedMessage = "{\"aiMessage\": \"Unwrapped content\"}";
        var aiResponse = new AiResponse { TextMessage = wrappedMessage, ToolCalls = null };
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(aiResponse));

        var command = new ProcessUserChatCommand(UserId, "Tell me something");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Unwrapped content");
    }

    [Fact]
    public async Task Handle_NullUser_StillSucceeds()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var aiResponse = new AiResponse { TextMessage = "Response", ToolCalls = null };
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(aiResponse));

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Response");
    }

    [Fact]
    public async Task Handle_PlainTextResponse_NotStripped()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanSendAiMessage(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var aiResponse = new AiResponse { TextMessage = "Just a plain message", ToolCalls = null };
        _aiIntentService.SendWithToolsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(),
            Arg.Any<byte[]?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<ChatHistoryMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(aiResponse));

        var command = new ProcessUserChatCommand(UserId, "Hello");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Be("Just a plain message");
    }
}
