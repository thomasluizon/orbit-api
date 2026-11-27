using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.References;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The model reads each offer key with the GUID of its record as a reference, and it copies the key into
/// <c>orbit_dismissed_offer_keys</c>. The server dismisses the issued key, on a tool call and in the terminal answer.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private static readonly (string Family, ReferenceRecordType Type, bool Dated)[] OfferFamilies =
    [
        ("log", ReferenceRecordType.Habit, true),
        ("skip", ReferenceRecordType.Habit, true),
        ("reminder", ReferenceRecordType.Habit, false),
        ("reviewHabit", ReferenceRecordType.Habit, false),
        ("goalLink", ReferenceRecordType.Goal, false),
        ("goalTarget", ReferenceRecordType.Goal, false),
        ("notification", ReferenceRecordType.Notification, false),
        ("reuse", ReferenceRecordType.Tag, false)
    ];

    public static TheoryData<string, string, bool> OfferDismissalCases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var (family, _, _) in OfferFamilies)
            foreach (var locale in new[] { "en", "pt-BR" })
                foreach (var toolCall in new[] { true, false })
                    data.Add(family, locale, toolCall);
        return data;
    }

    [Theory]
    [MemberData(nameof(OfferDismissalCases))]
    public async Task ShortReference_OfferKeyTheModelCopiesDismissesTheIssuedOffer(string family, string locale, bool toolCall)
    {
        var (_, type, dated) = OfferFamilies.Single(entry => entry.Family == family);
        SetupUserAndPayGate();
        EnableComposition();
        _shortReferences = true;
        var recordId = SeedOfferRecord(type);
        var key = family + ":" + recordId + (dated ? ":" + Today.ToString("yyyy-MM-dd") : "");
        var shortKey = key.Replace(recordId.ToString(), Reference(recordId));
        var issued = new List<AiOfferFact>
        {
            new(key, family, "Evidence", new AiNextAction(key, ProactiveOfferActionKinds.PreparePreview, "Label", null, "Prompt", recordId,
                dated ? Today : null), Guid.NewGuid(), "revision", Today)
        };
        var dismissed = new HashSet<string>(StringComparer.Ordinal);
        _offers.DismissedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => dismissed);
        _offers.IssuedAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => issued);
        _offers.DismissAsync(UserId, Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => { dismissed.UnionWith(call.Arg<IReadOnlyList<string>>()); return Task.CompletedTask; });
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(request => sent = request), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(toolCall
                ? new AiResponse
                {
                    ConversationContext = TestConversationContext,
                    ToolCalls = [new("get_habit_metrics", "dismiss", JsonSerializer.SerializeToElement(
                        new { orbit_dismissed_offer_keys = new[] { shortKey, "unissued" } }))]
                }
                : new AiResponse
                {
                    ConversationContext = TestConversationContext,
                    TextMessage = JsonSerializer.Serialize(new
                    {
                        kind = "answer",
                        aiMessage = locale == "en" ? "Okay." : "Tudo bem.",
                        orbit_dismissed_offer_keys = new[] { shortKey, "unissued" }
                    })
                }));
        CaptureModelResults(locale == "en" ? "Okay." : "Tudo bem.");
        var handler = CreateHandler(ReadTool("get_habit_metrics", new ToolResult(true)));

        var result = await handler.Handle(new(UserId, locale == "en" ? "Dismiss the offered action." : "Dispense a ação oferecida.",
            ClientContext: OfferClient(locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        sent!.DynamicContext.Should().Contain(shortKey).And.NotContain(key);
        dismissed.Should().ContainSingle().Which.Should().Be(key);
    }

    private Guid SeedOfferRecord(ReferenceRecordType type)
    {
        switch (type)
        {
            case ReferenceRecordType.Habit:
                var habit = CreateHabit("Water");
                SetupSelectionHabits(habit);
                return habit.Id;
            case ReferenceRecordType.Goal:
                var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Mobility", 30, "sessions")).Value;
                _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
                    Arg.Any<CancellationToken>()).Returns(new List<Goal> { goal }.AsReadOnly());
                return goal.Id;
            case ReferenceRecordType.Tag:
                var tag = Tag.Create(UserId, "Health", "#123456").Value;
                _tagRepo.FindAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
                    .Returns(new List<Tag> { tag }.AsReadOnly());
                return tag.Id;
            default:
                var notification = Notification.Create(UserId, "Alert", "Alert");
                _notificationRepo.ProjectAsync(Arg.Any<Expression<Func<Notification, bool>>>(),
                        Arg.Any<Func<IQueryable<Notification>, IQueryable<ReferenceRecord>>>(), Arg.Any<CancellationToken>())
                    .Returns(new[] { new ReferenceRecord(notification.Id, "Alert") });
                return notification.Id;
        }
    }
}
