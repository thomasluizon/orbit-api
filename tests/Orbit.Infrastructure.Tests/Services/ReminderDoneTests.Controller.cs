using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Middleware;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class ReminderDoneTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("deleted")]
    [InlineData("ended")]
    public async Task Done_Controller_UsesRealHandlerAndLogCommand(string state)
    {
        var (handler, habit, token) = await Seed();
        if (state == "deleted")
        {
            habit.SoftDelete();
            await _factory.Context.SaveChangesAsync();
        }
        if (state == "ended")
            _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(habit.EndDate!.Value.AddDays(1));
        _mediator.Send(Arg.Any<CompleteReminderCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<CompleteReminderCommand>(), call.Arg<CancellationToken>()));
        var controller = new RemindersController(_mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = await controller.Done(new(token), default);
        var body = result.Should().BeAssignableTo<ObjectResult>().Subject;
        body.StatusCode.Should().Be(state == "valid" ? 200 : 400);
        if (state == "valid")
        {
            var log = body.Value.Should().BeOfType<LogHabitResponse>().Subject;
            (await _factory.Context.HabitLogs.SingleAsync()).Id.Should().Be(log.LogId);
        }
        else
        {
            body.Value.Should().BeOfType<ErrorResponse>().Subject.ErrorCode.Should().Be("INVALID_REMINDER_ACTION");
            (await _factory.Context.HabitLogs.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Done_UnusableTimeZone_UsesUtcFallbackAndWarns()
    {
        var (handler, habit, token) = await Seed();
        var db = _factory.Context;
        var user = await db.Users.SingleAsync();
        db.Entry(user).Property(u => u.TimeZone).CurrentValue = "Unavailable/Zone";
        await db.SaveChangesAsync();
        var logger = Substitute.For<ILogger<UserDateService>>();
        var dates = new UserDateService(new GenericRepository<User>(db),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), _clock,
            new UnitOfWork(db, new DatabaseConnectionSettings()), logger);
        _dates.GetUserTodayAsync(habit.UserId, Arg.Any<CancellationToken>())
            .Returns(call => dates.GetUserTodayAsync(habit.UserId, call.Arg<CancellationToken>()));
        _dates.GetUserTimeZoneAsync(habit.UserId, Arg.Any<CancellationToken>())
            .Returns(call => dates.GetUserTimeZoneAsync(habit.UserId, call.Arg<CancellationToken>()));
        (await handler.Handle(new(token), default)).IsSuccess.Should().BeTrue();
        (await db.HabitLogs.SingleAsync()).LoggedOnLocalDate.Should().Be(DateOnly.FromDateTime(_clock.Now.UtcDateTime));
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log"
            && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning).Should().BeGreaterThan(0);
    }
}
