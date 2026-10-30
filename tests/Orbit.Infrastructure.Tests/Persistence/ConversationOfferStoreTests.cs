using Microsoft.EntityFrameworkCore;
using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ConversationOfferStoreTests
{
    [Fact]
    public async Task PersistedDismissalsAreScopedDurableAndCannotBeReissued()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);
        encryption.Encrypt(Arg.Any<string>()).Returns(call => "cipher:" + call.Arg<string>());
        encryption.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>()[7..]);
        using var factory = new SqliteOrbitDbContextFactory(encryption);
        var user = User.Create("Offer User", "offers@example.com").Value;
        var other = User.Create("Other User", "other-offers@example.com").Value;
        factory.Context.Users.AddRange(user, other);
        await factory.Context.SaveChangesAsync();
        var conversation = Guid.NewGuid();
        var key = "reminder:habit";
        var fact = new AstraOfferFact(key, "reminder", "typed evidence", new(key, "preparePreview", "Add a reminder", Prompt: "Prepare a reminder."), Guid.NewGuid(), "revision", new(2026, 10, 30));
        var store = new ConversationOfferStore(factory.Context, encryption);
        await store.IssueAsync(user.Id, conversation, fact, default);
        await store.IssueAsync(user.Id, conversation, fact, default);
        (await store.IssuedAsync(user.Id, conversation, default)).Should().ContainSingle()
            .Which.Action.OfferKey.Should().Be(key);
        (await store.FindIssuedAsync(user.Id, conversation, key, default)).Should().Be(fact);
        (await store.FindIssuedAsync(other.Id, conversation, key, default)).Should().BeNull();
        (await store.FindIssuedAsync(user.Id, Guid.NewGuid(), key, default)).Should().BeNull();
        (await store.FindIssuedAsync(user.Id, conversation, "unknown", default)).Should().BeNull();
        await store.DismissAsync(user.Id, conversation, [key], default);
        using var fresh = factory.CreateContext();
        var reloaded = new ConversationOfferStore(fresh, encryption);
        await reloaded.IssueAsync(user.Id, conversation, fact, default);
        (await reloaded.IssuedAsync(user.Id, conversation, default)).Should().BeEmpty();
        (await reloaded.FindIssuedAsync(user.Id, conversation, key, default)).Should().BeNull();
        (await reloaded.DismissedAsync(user.Id, conversation, default)).Should().ContainSingle().Which.Should().Be(key);
        (await reloaded.DismissedAsync(other.Id, conversation, default)).Should().BeEmpty();
        (await reloaded.DismissedAsync(user.Id, Guid.NewGuid(), default)).Should().BeEmpty();
        await reloaded.DismissAsync(user.Id, conversation, ["preview:field"], default);
        (await reloaded.DismissedAsync(user.Id, conversation, default)).Should().Contain("preview:field");
        await using var command = fresh.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"DefinitionJson\" FROM \"ConversationOffers\" WHERE \"OfferKey\" = 'reminder:habit'";
        ((string)(await command.ExecuteScalarAsync())!).Should().StartWith("cipher:");
    }
}
