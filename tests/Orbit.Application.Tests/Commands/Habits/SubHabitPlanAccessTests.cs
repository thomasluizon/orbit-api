using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class SubHabitPlanAccessTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 3, 20);
    private readonly User _user = User.Create("Account", "subhabits@example.com").Value;
    private readonly List<Habit> _stored = [];
    private readonly List<Habit> _added = [];
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tags = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<Goal> _goals = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IAppConfigService _config = Substitute.For<IAppConfigService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly PayGateService _payGate;

    public SubHabitPlanAccessTests()
    {
        _user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _config.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<bool>(1));
        _config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(1));
        _dates.GetUserTodayAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        _payGate = new PayGateService(_habits, users, _config, _dates, Substitute.For<IFeatureFlagService>());
        _habits.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.Count(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habits.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.FirstOrDefault(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        _habits.AddAsync(Arg.Do<Habit>(habit => { _stored.Add(habit); _added.Add(habit); }),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Guid>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<Guid>>>>(0)(call.ArgAt<CancellationToken>(1)));
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(0)(call.ArgAt<CancellationToken>(1)));
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<ToolResult>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<ToolResult>>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    [Theory]
    [InlineData("dedicated", false)]
    [InlineData("dedicated", true)]
    [InlineData("inline", false)]
    [InlineData("inline", true)]
    [InlineData("bulk", false)]
    [InlineData("bulk", true)]
    [InlineData("duplicate", false)]
    [InlineData("duplicate", true)]
    [InlineData("astra", false)]
    [InlineData("astra", true)]
    [InlineData("astra-dedicated", false)]
    [InlineData("astra-dedicated", true)]
    [InlineData("astra-existing-parent-bulk", false)]
    [InlineData("astra-existing-parent-bulk", true)]
    public async Task CreateSubHabits_AllPathsPersistChildren_ForBothPlans(string path, bool pro)
    {
        if (pro)
            _user.GrantLifetimePro();
        _user.HasProAccess.Should().Be(pro);

        Guid parentId;
        switch (path)
        {
            case "dedicated":
                var existingParent = AddExisting("Parent");
                var dedicated = await CreateDedicatedHandler().Handle(
                    new CreateSubHabitCommand(_user.Id, existingParent.Id, "Child", null, IsBadHabit: true), CancellationToken.None);
                dedicated.IsSuccess.Should().BeTrue("{0}", dedicated.Error);
                _added.Should().ContainSingle().Which.Id.Should().Be(dedicated.Value);
                parentId = existingParent.Id;
                break;
            case "inline":
                var inline = await new CreateHabitCommandHandler(
                    new CreateHabitRepositories(_habits, _tags, _goals), _dates, _payGate,
                    Substitute.For<IGamificationService>(), Substitute.For<IGoalCompletionService>(),
                    _unitOfWork, _cache, Substitute.For<ILogger<CreateHabitCommandHandler>>())
                    .Handle(new CreateHabitCommand(_user.Id, "Parent", null, FrequencyUnit.Day, 1,
                        SubHabits: ["Child", "Second child"]), CancellationToken.None);
                inline.IsSuccess.Should().BeTrue("{0}", inline.Error);
                parentId = inline.Value;
                _added.Should().HaveCount(3);
                break;
            case "bulk":
                var bulk = await new BulkCreateHabitsCommandHandler(
                    new BulkCreateHabitsRepositories(_habits, Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>(), _tags),
                    _payGate, _dates, _unitOfWork, _cache, _config)
                    .Handle(new BulkCreateHabitsCommand(_user.Id,
                        [new("Parent", null, FrequencyUnit.Day, 1,
                            SubHabits: [new("Child", null, null, null, IsBadHabit: true), new("Second child", null, null, null)])]), CancellationToken.None);
                bulk.IsSuccess.Should().BeTrue("{0}", bulk.Error);
                bulk.Value.Results.Should().ContainSingle().Which.Status.Should().Be(BulkItemStatus.Success);
                parentId = _added.Single(habit => habit.ParentHabitId is null).Id;
                _added.Should().HaveCount(3);
                break;
            case "duplicate":
                var original = AddExisting("Parent");
                var originalChild = AddExisting("Child", original.Id, slipAlertEnabled: true);
                AddExisting("Grandchild", originalChild.Id);
                var duplicate = await new DuplicateHabitCommandHandler(_habits,
                    Substitute.For<IGenericRepository<HabitLog>>(), _payGate, _unitOfWork, _dates, _cache)
                    .Handle(new DuplicateHabitCommand(_user.Id, original.Id), CancellationToken.None);
                duplicate.IsSuccess.Should().BeTrue("{0}", duplicate.Error);
                parentId = duplicate.Value;
                parentId.Should().NotBe(original.Id);
                _added.Should().HaveCount(3);
                _added.Single(habit => habit.Title == "Grandchild").ParentHabitId.Should()
                    .Be(_added.Single(habit => habit.Title == "Child").Id);
                _added.Should().OnlyContain(habit => habit.Id != original.Id && habit.Id != originalChild.Id);
                originalChild.SlipAlertEnabled.Should().BeTrue();
                break;
            case "astra-dedicated":
            case "astra-existing-parent-bulk":
                var parent = AddExisting("Parent");
                var mediator = Substitute.For<IMediator>();
                mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
                    .Returns(call => CreateDedicatedHandler().Handle(call.Arg<CreateSubHabitCommand>(), call.Arg<CancellationToken>()));
                IAiTool tool = path == "astra-dedicated"
                    ? new CreateSubHabitTool(mediator)
                    : new BulkCreateHabitsTool(mediator);
                var arguments = path == "astra-dedicated"
                    ? JsonSerializer.SerializeToElement(new { parent_habit_id = parent.Id, title = "Child" })
                    : JsonSerializer.SerializeToElement(new { parent_habit_id = parent.Id, habits = new[] { new { title = "Child" } } });
                var modelArguments = arguments.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone());
                var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
                var required = schema.GetProperty("required").EnumerateArray().Select(field => field.GetString()).ToHashSet();
                foreach (var field in schema.GetProperty("properties").EnumerateObject())
                    if (!required.Contains(field.Name) && !modelArguments.ContainsKey(field.Name))
                        modelArguments[field.Name] = JsonSerializer.SerializeToElement<object?>(null);
                var normalized = OptionalToolArguments.OmitOptionalNulls(JsonSerializer.SerializeToElement(modelArguments), tool.GetParameterSchema());
                var created = await tool.ExecuteAsync(normalized, _user.Id, CancellationToken.None);
                created.Success.Should().BeTrue("{0}", created.Error);
                _added.Should().ContainSingle();
                parentId = parent.Id;
                break;
            case "astra":
                using (var args = JsonDocument.Parse("""{"title":"Parent","frequency_unit":"Day","sub_habits":[{"title":"Child","is_bad_habit":true},{"title":"Second child"}]}"""))
                {
                    var astra = await new CreateHabitTool(_dates, Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habits, _tags, _goals, _dates, _payGate, _unitOfWork))
                        .ExecuteAsync(args.RootElement, _user.Id, CancellationToken.None);
                    astra.Success.Should().BeTrue("{0}", astra.Error);
                    parentId = Guid.Parse(astra.EntityId!);
                }
                _added.Should().HaveCount(3);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }

        _added.Single(habit => habit.Title == "Child").ParentHabitId.Should().Be(parentId);
        _added.Where(habit => habit.Title == "Second child").Should()
            .OnlyContain(habit => habit.ParentHabitId == parentId);
        _added.Should().OnlyContain(habit => habit.UserId == _user.Id);
        _added.Should().OnlyContain(habit => !habit.SlipAlertEnabled);
        await _unitOfWork.Received(path == "astra" ? 3 : 1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("dedicated", false)]
    [InlineData("dedicated", true)]
    [InlineData("inline", false)]
    [InlineData("inline", true)]
    [InlineData("astra-explicit", false)]
    [InlineData("astra-explicit", true)]
    [InlineData("astra-default", false)]
    [InlineData("astra-default", true)]
    public async Task CreateWithSlipAlerts_RequiresProBeforeAnyPersistence(string path, bool pro)
    {
        if (pro)
            _user.GrantLifetimePro();
        _user.HasProAccess.Should().Be(pro);

        bool success;
        string? errorCode;
        switch (path)
        {
            case "dedicated":
                for (var index = 0; index < AppConstants.DefaultFreeMaxHabits; index++)
                    AddExisting($"Root {index}");
                var dedicated = await CreateDedicatedHandler().Handle(
                    new CreateSubHabitCommand(_user.Id, _stored[0].Id, "Bad child", null,
                        IsBadHabit: true, Options: new HabitCommandOptions(SlipAlertEnabled: true)),
                    CancellationToken.None);
                success = dedicated.IsSuccess;
                errorCode = dedicated.ErrorCode;
                break;
            case "inline":
                var inline = await new CreateHabitCommandHandler(
                    new CreateHabitRepositories(_habits, _tags, _goals), _dates, _payGate,
                    Substitute.For<IGamificationService>(), Substitute.For<IGoalCompletionService>(),
                    _unitOfWork, _cache, Substitute.For<ILogger<CreateHabitCommandHandler>>())
                    .Handle(new CreateHabitCommand(_user.Id, "Bad parent", null, FrequencyUnit.Day, 1,
                        IsBadHabit: true, SubHabits: ["Child"],
                        Options: new HabitCommandOptions(SlipAlertEnabled: true)), CancellationToken.None);
                success = inline.IsSuccess;
                errorCode = inline.ErrorCode;
                break;
            case "astra-explicit":
            case "astra-default":
                using (var args = JsonDocument.Parse(path == "astra-explicit"
                    ? """{"title":"Bad parent","frequency_unit":"Day","is_bad_habit":true,"slip_alert_enabled":true,"sub_habits":[{"title":"Child"}]}"""
                    : """{"title":"Bad parent","frequency_unit":"Day","is_bad_habit":true,"sub_habits":[{"title":"Child"}]}"""))
                {
                    var astra = await new CreateHabitTool(_dates, Orbit.Tests.Shared.HabitToolCommandMediator.Create(_habits, _tags, _goals, _dates, _payGate, _unitOfWork))
                        .ExecuteAsync(args.RootElement, _user.Id, CancellationToken.None);
                    success = astra.Success;
                    errorCode = astra.ErrorCode;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }

        success.Should().Be(pro);
        if (!pro)
        {
            errorCode.Should().Be("PAY_GATE");
            _added.Should().BeEmpty();
            await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
            return;
        }

        _added.Should().HaveCount(path == "dedicated" ? 1 : 2);
        _added.Where(habit => habit.IsBadHabit).Should().ContainSingle()
            .Which.SlipAlertEnabled.Should().BeTrue();
        _added.Where(habit => !habit.IsBadHabit).Should().OnlyContain(habit => !habit.SlipAlertEnabled);
        await _unitOfWork.Received(path.StartsWith("astra-", StringComparison.Ordinal) ? 2 : 1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAtDepthSix_RejectsWithExistingDepthError_ForBothPlans(bool pro)
    {
        if (pro)
            _user.GrantLifetimePro();
        Habit parent = AddExisting("Root");
        for (var depth = 1; depth < 5; depth++)
            parent = AddExisting($"Level {depth + 1}", parent.Id);

        var result = await CreateDedicatedHandler().Handle(
            new CreateSubHabitCommand(_user.Id, parent.Id, "Too deep", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.MaxDepthReached.Format(5).Message);
        result.ErrorCode.Should().Be(ErrorMessages.MaxDepthReached.Code);
        _added.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatingChildAtRootCeiling_PreservesRootCountAndCap()
    {
        for (var index = 0; index < AppConstants.DefaultFreeMaxHabits; index++)
            AddExisting($"Root {index}");
        var parent = _stored[0];
        (await _payGate.CanCreateHabits(_user.Id)).ErrorCode.Should().Be("HABIT_LIMIT_REACHED");

        var result = await CreateDedicatedHandler().Handle(
            new CreateSubHabitCommand(_user.Id, parent.Id, "Child", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _stored.Count(habit => habit.ParentHabitId is null).Should().Be(AppConstants.DefaultFreeMaxHabits);
        _stored.Should().HaveCount(AppConstants.DefaultFreeMaxHabits + 1);
        (await _payGate.CanCreateHabits(_user.Id)).ErrorCode.Should().Be("HABIT_LIMIT_REACHED");
        _config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>())
            .Returns(AppConstants.DefaultFreeMaxHabits + 1);
        (await _payGate.CanCreateHabits(_user.Id)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void PayGateInterface_HasNoSubHabitGate()
    {
        typeof(IPayGateService).GetMethods().Select(method => method.Name)
            .Should().NotContain("CanCreateSubHabits");
    }

    private CreateSubHabitCommandHandler CreateDedicatedHandler() =>
        new(_habits, _tags, _dates, _unitOfWork, _config, _payGate, _cache);

    private Habit AddExisting(string title, Guid? parentId = null, bool slipAlertEnabled = false)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _user.Id, title, FrequencyUnit.Day, 1,
            DueDate: Today, ParentHabitId: parentId, IsBadHabit: slipAlertEnabled,
            SlipAlertEnabled: slipAlertEnabled)).Value;
        _stored.Add(habit);
        return habit;
    }

    public void Dispose() => _cache.Dispose();
}
