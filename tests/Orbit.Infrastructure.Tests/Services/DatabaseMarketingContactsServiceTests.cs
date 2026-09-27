using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class DatabaseMarketingContactsServiceTests
{
    [Fact]
    public async Task AddContactAsync_SecondConfirmationKeepsOneNormalizedContact()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var context = factory.Context;
        var service = new DatabaseMarketingContactsService(
            new GenericRepository<MarketingContact>(context),
            new UnitOfWork(context, new DatabaseConnectionSettings()));

        await service.AddContactAsync(" Person@Example.com ", "en");
        await service.AddContactAsync("person@example.com", "pt-BR");

        var contacts = context.MarketingContacts.ToList();
        contacts.Should().ContainSingle();
        contacts[0].Email.Should().Be("person@example.com");
        contacts[0].Language.Should().Be("en");
    }

    [Fact]
    public async Task AddContactAsync_AfterUnsubscribeAndSuppression_PreservesBoth()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var context = factory.Context;
        var service = new DatabaseMarketingContactsService(
            new GenericRepository<MarketingContact>(context),
            new UnitOfWork(context, new DatabaseConnectionSettings()));

        await service.AddContactAsync("person@example.com", "en");
        var contact = context.MarketingContacts.Single();
        contact.Unsubscribe();
        contact.Suppress();
        await context.SaveChangesAsync();

        await service.AddContactAsync("PERSON@example.com", "en");

        context.MarketingContacts.Should().ContainSingle();
        contact.UnsubscribedAtUtc.Should().NotBeNull();
        contact.SuppressedAtUtc.Should().NotBeNull();
    }
}
