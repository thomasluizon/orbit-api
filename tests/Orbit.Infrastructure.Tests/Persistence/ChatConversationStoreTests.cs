using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ChatConversationStoreTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("edited")]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task Reload_RetainsEncryptedImageAndPreviewWithCurrentOutcome(string outcome)
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.IsConfigured.Returns(true);
        encryption.Encrypt(Arg.Any<string>()).Returns(call => "cipher:" + call.Arg<string>());
        encryption.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>()[7..]);
        using var factory = new SqliteOrbitDbContextFactory(encryption);
        var user = User.Create("Context", "context@example.com").Value;
        var foreign = User.Create("Foreign", "foreign-context@example.com").Value;
        factory.Context.Users.AddRange(user, foreign);
        await factory.Context.SaveChangesAsync();
        var capability = new AgentCatalogService().GetCapabilityByChatTool("create_habit")!;
        var pendingStore = new PendingAgentOperationStore(factory.Context, Options.Create(new AgentPlatformSettings()));
        const string arguments = """{"title":"Terraço Itália","frequency_unit":"OneTime","due_date":"2026-12-31","due_time":"20:30"}""";
        var pending = pendingStore.Create(user.Id, capability, "create_habit", arguments, "Reservation",
            AgentOperationFingerprint.Compute("create_habit", arguments), AgentExecutionSurface.Chat);
        var conversation = Guid.NewGuid();
        var capture = new ChatConversationCapture(user.Id, conversation, Guid.NewGuid(), DateTime.UtcNow,
            "cria essa tarefa unica", null, [137, 80, 78, 71], "image/png", [pending]);
        var store = new ChatConversationStore(factory.Context);
        await store.SaveAsync(capture, default);
        await store.SaveAsync(capture, default);
        (await factory.Context.ChatConversationTurns.CountAsync()).Should().Be(1);
        var state = await factory.Context.PendingAgentOperations.SingleAsync();
        if (outcome == "edited")
        {
            var changed = arguments.Replace("20:30", "21:00", StringComparison.Ordinal);
            state.Revise(changed, AgentOperationFingerprint.Compute("create_habit", changed), "preview", DateTime.UtcNow).Should().BeTrue();
        }
        if (outcome == "approved") { state.SetConfirmationTokenHash("approval"); state.MarkConsumed(); }
        if (outcome == "rejected") state.Cancel(DateTime.UtcNow).Should().BeTrue();
        await factory.Context.SaveChangesAsync();
        using var fresh = factory.CreateContext();
        var reloaded = new ChatConversationStore(fresh);
        var memory = await reloaded.LoadAsync(user.Id, conversation, default);
        memory.Attachments.Should().ContainSingle().Which.ImageData.Should().Equal(capture.ImageData!);
        memory.History.Should().ContainSingle().Which.Content.Should().Be(capture.UserMessage);
        var preview = memory.LastPreviews.Should().ContainSingle().Which;
        preview.Outcome.Should().Be(outcome);
        preview.WasEdited.Should().Be(outcome == "edited");
        preview.CurrentArguments!.Value.GetProperty("due_date").GetString().Should().Be("2026-12-31");
        preview.CurrentArguments.Value.GetProperty("due_time").GetString().Should().Be(outcome == "edited" ? "21:00" : "20:30");
        (await reloaded.LoadAsync(foreign.Id, conversation, default)).Should().BeEquivalentTo(new ChatConversationMemory([], [], []));
        (await reloaded.LoadAsync(user.Id, Guid.NewGuid(), default)).Should().BeEquivalentTo(new ChatConversationMemory([], [], []));
        await using var command = fresh.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"DataJson\" FROM \"ChatConversationTurns\"";
        ((string)(await command.ExecuteScalarAsync())!).Should().StartWith("cipher:");
    }
}
