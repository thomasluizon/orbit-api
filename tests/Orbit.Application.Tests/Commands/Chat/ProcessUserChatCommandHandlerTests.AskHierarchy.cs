using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("child", false)]
    [InlineData("child", true)]
    [InlineData("grandchild", false)]
    [InlineData("grandchild", true)]
    [InlineData("parent", false)]
    [InlineData("parent", true)]
    [InlineData("ancestor", false)]
    [InlineData("ancestor", true)]
    [InlineData("disjoint", false)]
    [InlineData("disjoint", true)]
    public async Task Ask_HabitDeleteChecksCandidateHierarchyBeforePreparingPreview(string candidateRelation, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var fitness = CreateHabit("Fitness");
        var exercise = CreateHabit("Exercise");
        exercise.SetParentHabitId(fitness.Id);
        var walks = new[] { CreateHabit("Walk"), CreateHabit("Walk") };
        walks[0].SetParentHabitId(exercise.Id);
        var warmup = CreateHabit("Warmup");
        warmup.SetParentHabitId(walks[0].Id);
        var stretch = CreateHabit("Stretch");
        stretch.SetParentHabitId(warmup.Id);
        var music = CreateHabit("Music");
        var piano = CreateHabit("Piano");
        piano.SetParentHabitId(music.Id);
        Habit[] habits = [fitness, exercise, .. walks, warmup, stretch, music, piano];
        SetupSelectionHabits(habits);
        var target = candidateRelation switch
        {
            "child" => exercise,
            "grandchild" => fitness,
            "parent" => warmup,
            "ancestor" => stretch,
            "disjoint" => music,
            _ => throw new ArgumentOutOfRangeException(nameof(candidateRelation))
        };
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new DeleteHabitTool(_mediator, _habitRepo);
        var handler = CreateHandler(RealAskTool(), query, delete);
        var held = HoldAskRerunWrites(query, delete);
        const string question = "Which Walk would you like to skip?";
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
            [
                new(delete.Name, "delete", JsonSerializer.SerializeToElement(new { habit_id = target.Id })),
                new("ask", "question", JsonSerializer.SerializeToElement(new
                {
                    form = "choice",
                    question,
                    options = walks.Select((walk, index) => new
                    {
                        id = walk.Id.ToString(),
                        label = index == 0 ? "Morning walk" : "Evening walk"
                    })
                }))
            ]
        });

        var result = await handler.Handle(new(UserId, $"Delete {target.Title} ({target.Id}) and skip Walk today.",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (candidateRelation == "disjoint")
        {
            held.Should().ContainSingle().Which.OperationId.Should().Be(delete.Name);
            result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
                .Which.EntityId.Should().Be(target.Id);
        }
        else
        {
            held.Should().BeEmpty("a write that affects an unresolved candidate's hierarchy must wait for clarification");
            result.Value.PendingOperations.Should().BeNullOrEmpty();
            await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        }
        var choice = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraChoiceBlock>().Subject;
        choice.Question.Should().Be(question);
        choice.Options.Select(option => option.Id).Should().Equal(walks.Select(walk => walk.Id.ToString()));
        await _blockStore.Received(1).AddAsync(Arg.Any<IssuedAstraBlock>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(DeleteHabitCommand)!, default);
        habits.Should().OnlyContain(habit => !habit.IsDeleted);
    }
}
