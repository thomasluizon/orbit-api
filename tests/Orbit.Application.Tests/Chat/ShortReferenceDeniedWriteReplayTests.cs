using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.References;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The denied-write links of api#1111 with short references on. The model copies the 8-digit reference of a habit, so a
/// miscopy of it is a reference copy, and the link between a denied write and its resend reads it as one.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private static readonly Guid SharedPrefixFirst = Guid.Parse("aabbccdd-1111-4000-8000-000000000001");

    private static readonly Guid SharedPrefixLast = Guid.Parse("aabbccdd-2222-4000-8000-000000000002");

    private static Guid[] SharedPrefixIds() => [SharedPrefixFirst, .. Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()), SharedPrefixLast];

    /// <summary>The 8-digit reference of a habit with its first digit changed.</summary>
    private static string MiscopiedReference(Guid id) => (Reference(id)[0] == 'a' ? 'b' : 'a') + Reference(id)[1..];

    /// <summary>
    /// A rename whose habit reference is one digit off beside a prepared delete. The model sends the rename again with
    /// the right reference, so the correction binds it to the denied rename and the preview holds both changes.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_AMiscopiedReferenceLinksTheCorrectionToTheDeniedWrite(string language)
    {
        _shortReferences = true;
        var run = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(Run4Delete(children), Rename("rename-one", MiscopiedReference(children[1].Id))),
            children => [Calls(Rename("rename-one-again", Reference(children[1].Id))), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo([.. run.Children[4..].Select(child => child.Id), run.Children[1].Id]);
        run.Result.Value.PolicyDenials.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// The only write of the turn names a habit by a miscopied reference. The model reads the habits and sends the write
    /// again with the right reference, and the resend replaces the denial.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_AMiscopiedReferenceLinksTheResendOfALoneDenial(string language)
    {
        _shortReferences = true;
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(Rename("rename-one", MiscopiedReference(children[0].Id))),
            children => [Calls(ReadWater(1)), Calls(RenameFirst(children)), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(ToolFailureFor(language)).And.NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(run.Children[0].Id);
        run.Result.Value.PolicyDenials.Should().BeNullOrEmpty("the rename on the right reference replaced the denied rename");
    }

    /// <summary>
    /// Two habits share their first 8 digits, so the index shows 12 digits for both. A denied bulk rename holds the valid
    /// 12-digit reference of the first habit and a miscopied reference of the second, and the resend links to it.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_ACollisionReferenceInADeniedBulkWriteLinksTheResend(string language)
    {
        _shortReferences = true;
        var run = await RunRequestTurnAsync(language, SameRenameMessage(language),
            children => Calls(Run4Delete(children), new AiToolCall("bulk_update_habits", "rename", JsonSerializer.SerializeToElement(new
            {
                filter = new
                {
                    all = false,
                    habit_ids = new[] { Reference(children[0].Id, ReferenceCodes.CollisionLength), MiscopiedReference(children[1].Id) },
                    is_completed = false
                },
                updates = new { title = RenameTitle }
            }))),
            children => [Calls(new AiToolCall("bulk_update_habits", "rename-again", JsonSerializer.SerializeToElement(new
            {
                filter = new { all = false, habit_ids = new[] { Reference(children[0].Id, ReferenceCodes.CollisionLength), Reference(children[1].Id) }, is_completed = false },
                updates = new { title = RenameTitle }
            }))), Text(language)], childIds: SharedPrefixIds());

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo([.. run.Children[4..].Select(child => child.Id), run.Children[0].Id, run.Children[1].Id]);
    }

    /// <summary>
    /// A denied rename holds a miscopied habit reference and the valid reference of a goal. The resend holds the full id of
    /// each, so the link reads the goal reference as a copy of that goal.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_AValidGoalReferenceInADeniedWriteLinksTheResend(string language)
    {
        _shortReferences = true;
        var goal = Goal.Create(UserId, language == "en" ? "Consistent hydration" : "Hidratação constante", 10, "sessions").Value;
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(new AiToolCall("update_habit", "rename-one", JsonSerializer.SerializeToElement(new
            {
                habit_id = MiscopiedReference(children[0].Id),
                title = RenameTitle,
                goal_ids = new[] { Reference(goal.Id) }
            }))),
            children => [Calls(ReadWater(1)), Calls(new AiToolCall("update_habit", "rename-one-again", JsonSerializer.SerializeToElement(new
            {
                habit_id = children[0].Id,
                title = RenameTitle,
                goal_ids = new[] { goal.Id }
            }))), Text(language)],
            arrange: _ => _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
                Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(ToolFailureFor(language)).And.NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(run.Children[0].Id);
    }
}
