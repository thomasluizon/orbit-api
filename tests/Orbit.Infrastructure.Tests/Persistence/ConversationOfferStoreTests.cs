using Microsoft.Extensions.DependencyInjection;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DismissOffers_UnknownOrForeignConversation_Returns204AndWritesNothing(bool foreign)
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.Encrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());
        using var factory = new SqliteOrbitDbContextFactory(encryption);
        var user = User.Create("Caller", "caller@example.com").Value;
        var other = User.Create("Other", "foreign@example.com").Value;
        factory.Context.Users.AddRange(user, other);
        var conversation = Guid.NewGuid();
        if (foreign)
        {
            factory.Context.ConversationOffers.Add(ConversationOffer.Create(other.Id, conversation, "foreign").Value);
            factory.Context.ConversationTurns.Add(ConversationTurn.Create(other.Id, Guid.NewGuid(), "user", DateTime.UtcNow,
                null, "Hello", "[]", conversationId: conversation, turnOrder: 1).Value);
        }
        await factory.Context.SaveChangesAsync();
        var store = new ConversationOfferStore(factory.Context, encryption);
        using var services = OfferServices(store);
        var controller = OfferController(services, user.Id);
        var response = await controller.DismissOffers(new(conversation, ["dismissed"]), default);
        response.Should().BeOfType<Microsoft.AspNetCore.Mvc.NoContentResult>();
        (await factory.Context.ConversationOffers.CountAsync()).Should().Be(foreign ? 1 : 0);
        (await factory.Context.ConversationOffers.AnyAsync(offer => offer.UserId == user.Id)).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nkey")]
    public async Task DismissOffers_MalformedKey_Returns400WithoutWriting(string key)
    {
        var store = Substitute.For<IConversationOfferStore>();
        using var services = OfferServices(store);
        var controller = OfferController(services, Guid.NewGuid());
        var call = () => controller.DismissOffers(new(Guid.NewGuid(), [key]), default);
        var failure = (await call.Should().ThrowAsync<FluentValidation.ValidationException>()).Which;
        controller.Response.Body = new MemoryStream();
        await new Orbit.Api.Middleware.ValidationExceptionHandler(Microsoft.Extensions.Logging.Abstractions.NullLogger<Orbit.Api.Middleware.ValidationExceptionHandler>.Instance)
            .TryHandleAsync(controller.HttpContext, failure, default);
        controller.Response.StatusCode.Should().Be(400);
        await store.DidNotReceive().DismissAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider OfferServices(IConversationOfferStore store)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<FluentValidation.IValidator<Orbit.Application.Chat.Commands.DismissConversationOffersCommand>, Orbit.Application.Chat.Commands.DismissConversationOffersCommandValidator>();
        services.AddSingleton<Orbit.Api.Middleware.IRequestLanguageResolver>(
            new Orbit.Api.Middleware.RequestLanguageResolver(Substitute.For<IGenericRepository<User>>()));
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(Orbit.Application.Chat.Commands.DismissConversationOffersCommand).Assembly);
            configuration.AddOpenBehavior(typeof(Orbit.Application.Behaviors.ValidationBehavior<,>));
        });
        return services.BuildServiceProvider();
    }

    private static Orbit.Api.Controllers.ChatController OfferController(IServiceProvider services, Guid userId)
    {
        var controller = new Orbit.Api.Controllers.ChatController(services.GetRequiredService<MediatR.IMediator>(),
            Substitute.For<IImageValidationService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Orbit.Api.Controllers.ChatController>.Instance);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = services,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
            }
        };
        return controller;
    }

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
