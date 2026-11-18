using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.References;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// Short references at the chat boundary (thomasluizon/orbit-tickets#2154). The model reads a short reference for each
/// record and the normalizer gives each guard, tool and the executor the full GUID.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    /// <summary>
    /// Run 4 of #2129 with no executor stub that refuses the cut id. The cut id resolves by its first 8 digits, so the
    /// rename reaches the executor with two full GUIDs of the person's habits.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ShortReference_Run4CutIdResolvesByItsFirstEightDigits(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, _ => [Text(language)], denyUnowned: false);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var rename = run.Held.Should().ContainSingle(request => request.OperationId == "bulk_update_habits").Subject;
        rename.Arguments.GetProperty("filter").GetProperty("habit_ids").EnumerateArray().Select(id => id.GetString())
            .Should().Equal(run.Children[0].Id.ToString(), run.Children[1].Id.ToString());
        run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The person owns these records of a type that the turn context does not read, so a reference to one resolves at the
    /// chat boundary. The repository answers the projection that the handler reads.
    /// </summary>
    private static void OwnRecords<T>(IGenericRepository<T> repository, Func<T, ReferenceRecord> record, params T[] records) where T : Entity =>
        repository.ProjectAsync(Arg.Any<Expression<Func<T, bool>>>(), Arg.Any<Func<IQueryable<T>, IQueryable<ReferenceRecord>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<ReferenceRecord>)records.Where(call.ArgAt<Expression<Func<T, bool>>>(0).Compile())
                .Select(record).ToArray());
}
