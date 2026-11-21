using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class HabitEffectPostgresTests
{
    private readonly List<bool> _inferenceTransactions = [];
    private readonly SaveCounter _saves = new();

    [PostgresFact]
    public async Task EmojiInferenceOnTheMcpArguments_RunsWithNoOpenTransaction()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        InferEmoji("🏃");

        AgentExecuteOperationResponse response;
        await using (var scope = app.CreateScope())
            response = await scope.ServiceProvider.GetRequiredService<AgentOperationExecutor>().ExecuteAsync(new(world.User.Id,
                "bulk_update_habit_emojis",
                JsonSerializer.SerializeToElement(new { replace_existing = false, filter = new { all = true }, infer_from_title = true }),
                AgentExecutionSurface.Mcp, AgentAuthMethod.OAuth, [AgentScopes.WriteHabits]));

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, response.Operation.PolicyReason);
        _inferenceTransactions.Should().NotBeEmpty().And.AllBeEquivalentTo(false,
            "a provider call inside an open transaction holds its connection and its locks for the whole call");
        (await app.ReadAsync(db => db.Habits.CountAsync(saved => saved.Emoji == "🏃"))).Should().Be(world.Habits.Count);
    }

    [PostgresFact]
    public async Task EmojiInferenceOfAChatPreview_RunsWithNoOpenTransactionAndTheApprovalInfersNothing()
    {
        await using var app = await StartAsync();
        var world = await SeedWorldAsync(app);
        InferEmoji("🏃");

        var held = await HoldAsync(app, world.User.Id, "bulk_update_habit_emojis", new { filter = new { all = true } });
        var previewCalls = _inferenceTransactions.Count;
        var approved = await ApproveAsync(app, world.User.Id, held.Id);

        approved.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Operation.PolicyReason);
        previewCalls.Should().BePositive();
        _inferenceTransactions.Should().HaveCount(previewCalls, "the approval executes the stored revised items")
            .And.AllBeEquivalentTo(false);
        (await app.ReadAsync(db => db.Habits.CountAsync(saved => saved.Emoji == "🏃"))).Should().Be(world.Habits.Count);
    }

    /// <summary>
    /// The tripwire for one more save on each REST log. A log with no open transaction and a streak change makes
    /// three saves: the log, gamification with the streak, and the onboarding checklist. The row of the person
    /// takes two UPDATE statements, not a third one for the streak alone.
    /// </summary>
    [PostgresFact]
    public async Task LogWithNoOpenTransaction_SendsTheStreakInTheGamificationSave()
    {
        await using var app = await StartAsync();
        var user = await SeedUserAsync(app);
        var habit = await SeedHabitAsync(app, user.Id, "Run");

        Result<LogHabitResponse> logged;
        await using (var scope = app.CreateScope())
        {
            _saves.Reset();
            logged = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LogHabitCommand(user.Id, habit.Id));
        }
        var saves = _saves.Saves;
        var userUpdates = _saves.UserUpdates;

        logged.IsSuccess.Should().BeTrue(logged.Error);
        logged.Value.CurrentStreak.Should().Be(1);
        logged.Value.PreviousStreak.Should().Be(0);
        (await app.ReadAsync(db => db.Users.SingleAsync(saved => saved.Id == user.Id))).CurrentStreak.Should().Be(1);
        (saves, userUpdates).Should().Be((3, 2), "the gamification save carries the streak change");
    }

    private void InferEmoji(string emoji) => _emojiInference
        .InferAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<HabitEmojiInferenceInput>>(), Arg.Any<CancellationToken>())
        .Returns(call => Result.Success<IReadOnlyDictionary<Guid, string>>(
            call.ArgAt<IReadOnlyList<HabitEmojiInferenceInput>>(1).ToDictionary(input => input.HabitId, _ => emoji)));

    /// <summary>Records, for each provider call, whether the unit of work of the request holds an open transaction.</summary>
    private sealed class TransactionRecordingInference(IHabitEmojiInferenceService inner, IUnitOfWork unitOfWork,
        List<bool> openTransactions) : IHabitEmojiInferenceService
    {
        public Task<Result<IReadOnlyDictionary<Guid, string>>> InferAsync(Guid userId,
            IReadOnlyList<HabitEmojiInferenceInput> habits, CancellationToken cancellationToken = default)
        {
            openTransactions.Add(unitOfWork.HasActiveTransaction);
            return inner.InferAsync(userId, habits, cancellationToken);
        }
    }

    /// <summary>Counts the saves that send a statement, and the UPDATE statements of the row of the person.</summary>
    private sealed class SaveCounter : DbCommandInterceptor
    {
        private int _saves;
        private int _userUpdates;

        public int Saves => _saves;
        public int UserUpdates => _userUpdates;

        public void Reset()
        {
            _saves = 0;
            _userUpdates = 0;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (eventData.CommandSource == CommandSource.SaveChanges)
            {
                Interlocked.Increment(ref _saves);
                Interlocked.Add(ref _userUpdates, Occurrences(command.CommandText, "UPDATE \"Users\""));
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static int Occurrences(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
                index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
