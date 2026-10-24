using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class HabitParentUpdateContractTests
{
    public static IEnumerable<object[]> ParentInputs =>
        from child in new[] { false, true }
        from input in new[]
        {
            "", ",\"parentId\":null", ",\"clearParent\":null", ",\"clearParent\":false",
            ",\"parentId\":null,\"clearParent\":null", ",\"parentId\":null,\"clearParent\":false",
            ",\"parentId\":\"$parent\"", ",\"parentId\":\"$parent\",\"clearParent\":null",
            ",\"parentId\":\"$parent\",\"clearParent\":false", ",\"clearParent\":true",
            ",\"parentId\":null,\"clearParent\":true", ",\"parentId\":\"$parent\",\"clearParent\":true",
            ",\"clearParent\":true,\"checklistItems\":[{\"text\":\"$checklist\",\"isChecked\":false}]"
        }
        select new object[] { child, input };

    [Theory]
    [MemberData(nameof(ParentInputs))]
    public async Task ActualMvcRequest_ControllerAndHandler_PersistExplicitClearMatrix(bool child, string input)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var today = new DateOnly(2026, 4, 3);
        var user = User.Create("Alex", "parent-contract@example.test").Value;
        var oldParent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Old parent", FrequencyUnit.Day, 1, today)).Value;
        var target = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Target", FrequencyUnit.Day, 1, today)).Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Original", FrequencyUnit.Day, 1, today,
            ParentHabitId: child ? oldParent.Id : null)).Value;
        db.Users.Add(user);
        db.Habits.AddRange(oldParent, target, habit);
        await db.SaveChangesAsync();
        await using var mutation = factory.CreateContext();
        var repository = new GenericRepository<Habit>(mutation);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(5);
        var gate = Substitute.For<IPayGateService>();
        gate.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new UpdateHabitCommandHandler(repository, new GenericRepository<SentReminder>(mutation),
            new GenericRepository<Goal>(mutation), gate, Substitute.For<IGoalCompletionService>(), dates,
            new UnitOfWork(mutation, new DatabaseConnectionSettings()), cache, new GenericRepository<Tag>(mutation), config);
        var sender = Substitute.For<IMediator>();
        UpdateHabitCommand? mapped = null;
        sender.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            mapped = call.Arg<UpdateHabitCommand>();
            var ct = call.Arg<CancellationToken>();
            await new UpdateHabitCommandValidator(repository).ValidateAndThrowAsync(mapped, ct);
            return await handler.Handle(mapped, ct);
        });
        var controller = new HabitsController(sender, NullLogger<HabitsController>.Instance, dates)
        {
            ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            }
        };
        var json = "{\"title\":\"Changed\",\"frequencyUnit\":\"Day\",\"frequencyQuantity\":1"
            + input.Replace("$parent", target.Id.ToString(), StringComparison.Ordinal)
                .Replace("$checklist", new string('x', AppConstants.MaxChecklistItemTextLength + 1), StringComparison.Ordinal) + "}";
        var request = JsonSerializer.Deserialize<HabitsController.UpdateHabitRequest>(json, ReminderInputContractTests.MvcOptions())!;
        var conflict = request.ClearParent == true && request.ParentId.HasValue;
        var invalidChecklist = request.ChecklistItems?.Any(item => item.Text.Length > AppConstants.MaxChecklistItemTextLength) == true;
        var refused = conflict || invalidChecklist;
        var act = () => controller.UpdateHabit(habit.Id, request, default);
        if (refused)
        {
            var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
            if (conflict)
                error.Errors.Should().ContainSingle(e => e.PropertyName == "ParentId" && e.ErrorCode == ErrorCodes.ValidationError);
            else
                error.Errors.Should().Contain(e => e.PropertyName.Contains("ChecklistItems", StringComparison.Ordinal));
        }
        else
            (await act()).Should().BeOfType<NoContentResult>().Which.StatusCode.Should().Be(204);
        mapped.Should().NotBeNull();
        mapped!.ParentId.Should().Be(request.ParentId);
        mapped.ClearParent.Should().Be(request.ClearParent);
        await using var readback = factory.CreateContext();
        var persisted = await readback.Habits.SingleAsync(h => h.Id == habit.Id);
        persisted.Title.Should().Be(refused ? "Original" : "Changed");
        var originalParent = child ? oldParent.Id : (Guid?)null;
        persisted.ParentHabitId.Should().Be(refused ? originalParent
            : request.ClearParent == true ? null : request.ParentId ?? originalParent);
    }

    [Theory]
    [InlineData("\"malformed\"")]
    [InlineData("42")]
    [InlineData("{}")]
    public void ActualMvcRequest_RejectsMalformedParentBeforeDispatch(string parent)
    {
        var deserialize = () => JsonSerializer.Deserialize<HabitsController.UpdateHabitRequest>(
            "{\"title\":\"Read\",\"parentId\":" + parent + "}", ReminderInputContractTests.MvcOptions());
        deserialize.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void PublishedCatalog_AppendsOptionalParentInputsAndNullableReadback(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var request = schemas.GetProperty("UpdateHabitRequest");
        request.GetProperty("required").EnumerateArray().Select(v => v.GetString())
            .Should().NotContain("parentId").And.NotContain("clearParent");
        foreach (var schema in new[] { request, schemas.GetProperty("HabitDetailResponse") })
        {
            var parent = schema.GetProperty("properties").GetProperty("parentId");
            parent.GetProperty("type").EnumerateArray().Select(v => v.GetString()).Should().BeEquivalentTo("string", "null");
            parent.GetProperty("format").GetString().Should().Be("uuid");
        }
        request.GetProperty("properties").GetProperty("clearParent").GetProperty("type").EnumerateArray()
            .Select(v => v.GetString()).Should().BeEquivalentTo("boolean", "null");
        var update = document.RootElement.GetProperty("paths").GetProperty("/api/Habits/{id}").GetProperty("put");
        update.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _).Should().BeFalse();
    }
}
