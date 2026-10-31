using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Validators;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("bulk_reschedule_habits", "END_DATE_BEFORE_START", "en")]
    [InlineData("update_checklist", "CHECKLIST_ITEM_TOO_LONG", "en")]
    [InlineData("update_checklist_template", ValidationErrorCodes.TemplateItemLength, "en")]
    [InlineData("update_profile_preferences", ValidationErrorCodes.ProfileNameLength, "en")]
    [InlineData("manage_calendar_sync", ValidationErrorCodes.CalendarSelectionLimit, "en")]
    [InlineData("bulk_reschedule_habits", "END_DATE_BEFORE_START", "pt-BR")]
    [InlineData("update_checklist", "CHECKLIST_ITEM_TOO_LONG", "pt-BR")]
    [InlineData("update_checklist_template", ValidationErrorCodes.TemplateItemLength, "pt-BR")]
    [InlineData("update_profile_preferences", ValidationErrorCodes.ProfileNameLength, "pt-BR")]
    [InlineData("manage_calendar_sync", ValidationErrorCodes.CalendarSelectionLimit, "pt-BR")]
    public async Task PreconfirmationArgumentFailure_RepairsOnceBeforeApproval(string operation, string code, string locale)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var (tool, invalid, corrected) = PreconfirmationRepairCase(operation);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        using var checks = PreconfirmationChecks();
        SetupAiResponse(new AiResponse { ToolCalls = [invalid], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var error = call.Arg<IReadOnlyList<AiToolCallResult>>().Single();
                error.Success.Should().BeFalse();
                outcomes.Single().Operation.Status.Should().Be(AgentOperationStatus.Failed);
                outcomes.Single().Operation.ErrorCode.Should().Be(code);
                JsonSerializer.SerializeToElement(error.Payload).GetProperty("correction_allowed").GetBoolean().Should().BeTrue();
                return Result.Success(new AiResponse { ToolCalls = [corrected], ConversationContext = TestConversationContext });
            });

        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Select(response => response.Operation.Status).Should().Equal(AgentOperationStatus.Failed, AgentOperationStatus.PendingConfirmation);
        outcomes[0].Should().BeOfType<RepairableArgumentResponse>();
        outcomes[0].Operation.ErrorCode.Should().Be(code);
        result.Value.PendingOperations.Should().ContainSingle();
        result.Value.PolicyDenials.Should().BeNullOrEmpty();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        _mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
    }

    [Theory]
    [InlineData("bulk_reschedule_habits")]
    [InlineData("update_checklist")]
    [InlineData("update_checklist_template")]
    [InlineData("update_profile_preferences")]
    [InlineData("manage_calendar_sync")]
    public async Task PreconfirmationArgumentFailure_DoesNotAllowASecondRepair(string operation)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var (tool, invalid, _) = PreconfirmationRepairCase(operation);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        using var checks = PreconfirmationChecks();
        SetupAiResponse(new AiResponse { ToolCalls = [invalid], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            { ToolCalls = [invalid with { Id = "still-invalid" }], ConversationContext = TestConversationContext }));

        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient("en")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Should().HaveCount(2).And.OnlyContain(response => response.Operation.Status == AgentOperationStatus.Failed);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("bulk_reschedule_habits", "habit_id")]
    [InlineData("update_checklist", "habit_id")]
    [InlineData("update_checklist_template", "template_id")]
    [InlineData("update_profile_preferences", "action")]
    [InlineData("manage_calendar_sync", "action")]
    public async Task PreconfirmationArgumentFailure_CannotSwitchTargetOrAction(string operation, string field)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var (tool, invalid, corrected) = PreconfirmationRepairCase(operation);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        using var checks = PreconfirmationChecks();
        var arguments = JsonNode.Parse(corrected.Args.GetRawText())!.AsObject();
        if (operation == "bulk_reschedule_habits") arguments["revised_items"]![0]![field] = Guid.NewGuid().ToString();
        else arguments[field] = field == "action" ? operation == "manage_calendar_sync" ? "run_sync" : "complete_tour" : Guid.NewGuid().ToString();
        SetupAiResponse(new AiResponse { ToolCalls = [invalid], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            { ToolCalls = [corrected with { Args = JsonSerializer.SerializeToElement(arguments) }], ConversationContext = TestConversationContext }));

        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient("en")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        outcomes.Should().ContainSingle().Which.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("bulk_reschedule_habits", ErrorCodes.HabitNotFound)]
    [InlineData("update_checklist_template", ErrorCodes.TemplateNotFound)]
    public async Task PreconfirmationArgumentCheck_OwnershipScopedMissingTargetRemainsDenied(string operation, string code)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var (tool, _, corrected) = PreconfirmationRepairCase(operation);
        var handler = CreateHandler(tool);
        var outcomes = R9fWriteExecutor(tool);
        using var checks = PreconfirmationChecks();
        SetupOfferHabits();
        _checklistTemplateRepo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ChecklistTemplate>());
        SetupAiResponse(new AiResponse { ToolCalls = [corrected], ConversationContext = TestConversationContext });

        var result = await handler.Handle(new(UserId, "Prepare the requested preview.", ClientContext: OfferClient("en")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var rejection = outcomes.Should().ContainSingle().Which;
        rejection.Should().NotBeOfType<RepairableArgumentResponse>();
        rejection.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        rejection.Operation.PolicyReason.Should().Be(code);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    private (IAiTool Tool, AiToolCall Invalid, AiToolCall Corrected) PreconfirmationRepairCase(string operation)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Water", FrequencyUnit.Day, 1,
            Today, EndDate: Today.AddDays(1))).Value;
        SetupOfferHabits(habit);
        _habitRepo.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { habit }.FirstOrDefault(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()));
        _habitRepo.AnyAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { habit }.Any(call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()));
        var template = ChecklistTemplate.Create(UserId, "Morning", ["Water"]).Value;
        _checklistTemplateRepo.FindAsync(Arg.Any<Expression<Func<ChecklistTemplate, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { template }.Where(call.ArgAt<Expression<Func<ChecklistTemplate, bool>>>(0).Compile()).ToArray());
        IAiTool tool = operation switch
        {
            "bulk_reschedule_habits" => new BulkRescheduleHabitsTool(_mediator),
            "update_checklist" => new UpdateChecklistTool(_mediator, _habitRepo),
            "update_checklist_template" => new UpdateChecklistTemplateTool(_mediator),
            "update_profile_preferences" => new UpdateProfilePreferencesTool(_mediator),
            _ => new ManageCalendarSyncTool(_mediator)
        };
        AiToolCall Call(bool valid)
        {
            object values = operation switch
            {
                "bulk_reschedule_habits" => new { restart_plan = false, revised_items = new[] { new { habit_id = habit.Id, updates = new { due_date = Today.AddDays(valid ? 1 : 2).ToString("yyyy-MM-dd") } } } },
                "update_checklist" => new { habit_id = habit.Id, item_to_add = valid ? "Open the bottle" : new string('a', DomainConstants.MaxChecklistItemTextLength + 1) },
                "update_checklist_template" => new { template_id = template.Id, items = new[] { valid ? "Read" : new string('a', AppConstants.MaxChecklistItemTextLength + 1) } },
                "update_profile_preferences" => new { action = "set_name", name = valid ? "Ana" : new string('a', AppConstants.MaxUserNameLength + 1) },
                _ => new { action = "set_selected_calendars", calendar_ids = valid ? ["work"] : Enumerable.Range(0, AppConstants.MaxSelectedCalendars + 1).Select(i => $"calendar-{i}").ToArray() }
            };
            var arguments = JsonSerializer.SerializeToNode(values)!.AsObject();
            arguments["orbit_write_complete"] = true;
            return new(tool.Name, valid ? "corrected" : "invalid", JsonSerializer.SerializeToElement(arguments));
        }
        return (tool, Call(false), Call(true));
    }

    private ServiceProvider PreconfirmationChecks()
    {
        var services = new ServiceCollection()
            .AddSingleton(_habitRepo).AddSingleton(_checklistTemplateRepo).AddSingleton(_userDateService)
            .AddSingleton<IValidator<BulkUpdateHabitsCommand>>(new BulkUpdateHabitsCommandValidator())
            .AddSingleton<IValidator<AppendChecklistItemCommand>>(new AppendChecklistItemCommandValidator())
            .AddSingleton<IValidator<UpdateChecklistTemplateCommand>>(new UpdateChecklistTemplateCommandValidator())
            .AddSingleton<IValidator<SetNameCommand>>(new SetNameCommandValidator())
            .AddSingleton<IValidator<SetSelectedCalendarsCommand>>(new SetSelectedCalendarsCommandValidator())
            .BuildServiceProvider();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => new CheckChatCommandQueryHandler(services).Handle(call.Arg<CheckChatCommandQuery>(), call.Arg<CancellationToken>()));
        return services;
    }
}
