using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class PendingClarificationStoreTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly Guid _userId;

    public PendingClarificationStoreTests()
    {
        var user = User.Create("Alex", "alex@example.com").Value;
        _userId = user.Id;
        _factory.Context.Users.Add(user);
        _factory.Context.SaveChanges();
    }

    [Fact]
    public void ClientContextColumn_IsNullableJson()
    {
        var property = _factory.Context.Model.FindEntityType(typeof(PendingClarification))!
            .FindProperty(nameof(PendingClarification.ClientContextJson))!;

        property.IsNullable.Should().BeTrue();
    }

    [Fact]
    public async Task Stash_KeepsTheChatClientContextForTheResolve()
    {
        var context = new AgentClientContext(Locale: "pt-BR", SupportsParts: true, SupportsPendingOperationChanges: true,
            SupportsPendingOperationPlacement: true, SupportsProactiveOffers: true, ConversationId: Guid.NewGuid(),
            DismissedOfferKeys: ["preview:offer"]);
        var store = new PendingClarificationStore(_factory.Context);

        var id = await store.CreateAsync(_userId, "create_habit", """{"title":"Read habit"}""", "frequency_unit", "How often?",
            """[{"label":"daily","value":"{\"frequency_unit\":\"Day\"}"}]""", clientContext: context);
        _factory.Context.ChangeTracker.Clear();
        var resolved = await store.GetForResolutionAsync(id, _userId);

        resolved!.ClientContext.Should().BeEquivalentTo(context);
    }

    [Fact]
    public async Task RowWithoutAStoredContext_ReadsBackAndResolvesWithNoContext()
    {
        var row = PendingClarification.Create(_userId, "create_habit", """{"title":"Read habit"}""", "frequency_unit",
            "How often?", """[{"label":"daily","value":"{\"frequency_unit\":\"Day\"}"}]""", DateTime.UtcNow.AddMinutes(30));
        _factory.Context.PendingClarifications.Add(row);
        await _factory.Context.SaveChangesAsync();
        _factory.Context.ChangeTracker.Clear();

        var resolved = await new PendingClarificationStore(_factory.Context).GetForResolutionAsync(row.Id, _userId);

        resolved.Should().NotBeNull();
        resolved!.ClientContext.Should().BeNull();
        resolved.AllowedValues.Should().Equal("""{"frequency_unit":"Day"}""");
        (await _factory.Context.PendingClarifications.AsNoTracking().SingleAsync()).ClientContextJson.Should().BeNull();
    }

    public void Dispose() => _factory.Dispose();
}
