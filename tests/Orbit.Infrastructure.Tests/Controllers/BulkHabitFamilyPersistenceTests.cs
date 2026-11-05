using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class BulkHabitFamilyPersistenceTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, HabitSchedule, int> SelectionOrders()
    {
        var cases = new TheoryData<string, HabitSchedule, int>();
        foreach (var operation in new[] { "skip", "log" })
            foreach (var schedule in new[] { HabitSchedule.Recurring, HabitSchedule.Flexible })
                for (var parentIndex = 0; parentIndex < 5; parentIndex++)
                    cases.Add(operation, schedule, parentIndex);
        return cases;
    }

    [Theory]
    [MemberData(nameof(SelectionOrders))]
    public async Task ParentAndFourChildren_AllSucceedAndPersistRegardlessOfSelectionOrder(
        string operation, HabitSchedule parentSchedule, int parentIndex)
    {
        using var fixture = new Fixture(parentSchedule);
        var ids = fixture.Children.Select(habit => habit.Id).ToList();
        ids.Insert(parentIndex, fixture.Parent.Id);

        var results = await fixture.Send(operation, ids);

        results.Should().HaveCount(5).And.OnlyContain(item => item.Status == BulkItemStatus.Success);
        results.Select(item => item.Index).Should().Equal(Enumerable.Range(0, 5));
        results.Select(item => item.HabitId).Should().Equal(ids);
        results.Should().OnlyContain(item => item.Error == null && item.ErrorCode == null);
        using var readback = fixture.Database.CreateContext();
        var saved = await readback.Habits.Include(habit => habit.Logs).ToListAsync();
        saved.Should().HaveCount(5);
        foreach (var habit in saved)
        {
            habit.ParentHabitId.Should().Be(habit.Id == fixture.Parent.Id ? null : fixture.Parent.Id);
            if (habit.Schedule == HabitSchedule.Flexible)
            {
                habit.NextDueDate.Should().Be(Today);
                habit.Logs.Should().HaveCount(3);
                habit.Logs.Count(log => log.Value == 0).Should().Be(operation == "skip" ? 1 : 0);
            }
            else
            {
                habit.NextDueDate.Should().Be(Today.AddDays(1));
                habit.Logs.Should().HaveCount(operation == "log" ? 1 : 0);
            }
            habit.Logs.Should().OnlyContain(log => log.Date == Today);
        }
    }

    [Theory]
    [InlineData("skip", 0)]
    [InlineData("skip", 4)]
    [InlineData("log", 0)]
    [InlineData("log", 4)]
    public async Task IneligibleParent_RejectsOnlyParentWithSpecificCodeAndPersistsChildren(string operation, int parentIndex)
    {
        using var fixture = new Fixture(HabitSchedule.Recurring, parentDueTomorrow: true);
        var ids = fixture.Children.Select(habit => habit.Id).ToList();
        ids.Insert(parentIndex, fixture.Parent.Id);

        var results = await fixture.Send(operation, ids);

        results.Should().HaveCount(5);
        var rejected = results.Single(item => item.HabitId == fixture.Parent.Id);
        rejected.Index.Should().Be(parentIndex);
        rejected.Status.Should().Be(BulkItemStatus.Failed);
        rejected.ErrorCode.Should().Be(operation == "skip"
            ? ErrorMessages.HabitNotYetDue.Code : ErrorMessages.NotScheduledOnDate.Code);
        rejected.Error.Should().NotBeNullOrWhiteSpace();
        results.Where(item => item.HabitId != fixture.Parent.Id).Should()
            .OnlyContain(item => item.Status == BulkItemStatus.Success && item.ErrorCode == null);
        using var readback = fixture.Database.CreateContext();
        var parent = await readback.Habits.Include(habit => habit.Logs).SingleAsync(habit => habit.Id == fixture.Parent.Id);
        parent.NextDueDate.Should().Be(Today.AddDays(1));
        parent.Logs.Should().BeEmpty();
        var children = await readback.Habits.Include(habit => habit.Logs)
            .Where(habit => habit.ParentHabitId == fixture.Parent.Id).ToListAsync();
        children.Should().HaveCount(4).And.OnlyContain(habit => habit.NextDueDate == Today.AddDays(1));
        children.Should().OnlyContain(habit => habit.Logs.Count == (operation == "log" ? 1 : 0));
    }

    private sealed record ItemResult(int Index, BulkItemStatus Status, Guid HabitId, string? Error, string? ErrorCode);

    private sealed class Fixture : IDisposable
    {
        public SqliteOrbitDbContextFactory Database { get; } = new();
        public Habit Parent { get; }
        public Habit[] Children { get; }
        private readonly Guid _userId;
        private readonly ServiceProvider _provider;

        public Fixture(HabitSchedule parentSchedule, bool parentDueTomorrow = false)
        {
            var user = User.Create("Family user", "bulk-family@example.test").Value;
            _userId = user.Id;
            Parent = Habit.Create(new HabitCreateParams(parentSchedule, _userId, "Water", FrequencyUnit.Day,
                parentSchedule == HabitSchedule.Flexible ? 6 : 1, parentDueTomorrow ? Today.AddDays(1) : Today,
                DueTime: new TimeOnly(20, 47))).Value;
            Children = Enumerable.Range(0, 4).Select(index => Habit.Create(new HabitCreateParams(
                HabitSchedule.Recurring, _userId, $"Water - 710ml {index}", FrequencyUnit.Day, 1, Today,
                DueTime: new TimeOnly(20, 47), ParentHabitId: Parent.Id)).Value).ToArray();
            Database.Context.Users.Add(user);
            Database.Context.Habits.Add(Parent);
            Database.Context.Habits.AddRange(Children);
            if (parentSchedule == HabitSchedule.Flexible)
                for (var index = 0; index < 2; index++)
                    Database.Context.HabitLogs.Add(Parent.Log(Today, loggedOnLocalDate: Today).Value);
            Database.Context.SaveChanges();
            Database.Context.ChangeTracker.Clear();

            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
            dates.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
            var services = new ServiceCollection().AddLogging().AddMemoryCache();
            services.AddScoped(_ => Database.CreateContext());
            services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
            services.AddSingleton(new DatabaseConnectionSettings());
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddSingleton(dates);
            services.AddSingleton(Substitute.For<IUserStreakService>());
            services.AddSingleton(Substitute.For<IGamificationService>());
            services.AddSingleton(Substitute.For<IGoalCompletionService>());
            services.AddScoped<BulkLogServices>();
            services.AddTransient<IValidator<BulkSkipHabitsCommand>, BulkSkipHabitsCommandValidator>();
            services.AddTransient<IValidator<BulkLogHabitsCommand>, BulkLogHabitsCommandValidator>();
            services.AddMediatR(options =>
            {
                options.RegisterServicesFromAssemblyContaining<BulkSkipHabitsCommand>();
                options.AddOpenBehavior(typeof(ValidationBehavior<,>));
            });
            _provider = services.BuildServiceProvider();
        }

        public async Task<IReadOnlyList<ItemResult>> Send(string operation, IReadOnlyList<Guid> ids)
        {
            using var scope = _provider.CreateScope();
            var controller = ActivatorUtilities.CreateInstance<HabitsController>(scope.ServiceProvider);
            controller.ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                }
            };
            var json = JsonSerializer.Serialize(new { items = ids.Select(id => new { habitId = id, date = Today }) }, Json);
            var action = operation == "skip"
                ? await controller.BulkSkip(JsonSerializer.Deserialize<HabitsController.BulkSkipHabitsRequest>(json, Json)!, default)
                : await controller.BulkLog(JsonSerializer.Deserialize<HabitsController.BulkLogHabitsRequest>(json, Json)!, default);
            var response = action.Should().BeOfType<OkObjectResult>().Subject;
            response.StatusCode.Should().Be(StatusCodes.Status200OK);
            return operation == "skip"
                ? response.Value.Should().BeOfType<BulkSkipResult>().Subject.Results
                    .Select(item => new ItemResult(item.Index, item.Status, item.HabitId, item.Error, item.ErrorCode)).ToList()
                : response.Value.Should().BeOfType<BulkLogResult>().Subject.Results
                    .Select(item => new ItemResult(item.Index, item.Status, item.HabitId, item.Error, item.ErrorCode)).ToList();
        }

        public void Dispose()
        {
            _provider.Dispose();
            Database.Dispose();
        }
    }
}
