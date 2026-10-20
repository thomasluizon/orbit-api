using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Middleware;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class HabitLogRangeContractTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("2025-01-01", "2025-01-31")]
    [InlineData("2024-01-01", "2024-12-31")]
    [InlineData("2025-01-01", "2025-01-01")]
    public async Task QueryBindingAndValidation_AcceptValidDates(string? from, string? to)
    {
        var query = await BindRangeAsync(from, to);

        await ValidateThroughPipelineAsync(query);

        query.From.Should().Be(from is null ? null : DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        query.To.Should().Be(to is null ? null : DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("2025-01-31", "2025-01-01", "To", ValidationErrorCodes.DateRangeOrder)]
    [InlineData("2024-01-01", "2025-01-01", "To", ValidationErrorCodes.DateRangeLimit)]
    [InlineData("invalid", "2025-01-31", "from", ValidationErrorCodes.CalendarDateValid)]
    [InlineData("2025-01-01", "2025-02-30", "to", ValidationErrorCodes.CalendarDateValid)]
    [InlineData("01/01/2025", "2025-01-31", "from", ValidationErrorCodes.CalendarDateValid)]
    [InlineData("2025-1-1", "2025-01-31", "from", ValidationErrorCodes.CalendarDateValid)]
    [InlineData("2025-01-01T00:00:00Z", "2025-01-31", "from", ValidationErrorCodes.CalendarDateValid)]
    [InlineData("", "2025-01-31", "from", ValidationErrorCodes.CalendarDateValid)]
    [InlineData("2025-01-01", null, "To", ValidationErrorCodes.DateToRequired)]
    [InlineData(null, "2025-01-31", "From", ValidationErrorCodes.DateFromRequired)]
    public async Task InvalidRange_UsesExistingValidationEnvelope(string? from, string? to, string field, string code)
    {
        var act = async () => await ValidateThroughPipelineAsync(await BindRangeAsync(from, to));
        var exception = (await act.Should().ThrowAsync<ValidationException>()).Which;
        var context = new DefaultHttpContext { TraceIdentifier = "habit-log-range" };
        context.Response.Body = new MemoryStream();
        using var services = new ServiceCollection()
            .AddSingleton<IRequestLanguageResolver>(
                new RequestLanguageResolver(Substitute.For<IGenericRepository<User>>()))
            .BuildServiceProvider();
        context.RequestServices = services;
        var handler = new ValidationExceptionHandler(NullLogger<ValidationExceptionHandler>.Instance);

        (await handler.TryHandleAsync(context, exception, CancellationToken.None)).Should().BeTrue();

        context.Response.StatusCode.Should().Be(400);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        var root = json.RootElement;
        root.GetProperty("type").GetString().Should().Be("ValidationFailure");
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("requestId").GetString().Should().Be("habit-log-range");
        root.GetProperty("errors").GetProperty(field).GetArrayLength().Should().Be(1);
        root.GetProperty("errorDetails").GetProperty(field)[0].GetProperty("code").GetString().Should().Be(code);
    }

    private static Task<Result<IReadOnlyList<HabitLogResponse>>> ValidateThroughPipelineAsync(GetHabitLogsQuery query)
    {
        var pipeline = new ValidationBehavior<GetHabitLogsQuery, Result<IReadOnlyList<HabitLogResponse>>>(
            [new GetHabitLogsQueryValidator()]);
        return pipeline.Handle(query,
            _ => Task.FromResult(Result.Success<IReadOnlyList<HabitLogResponse>>([])), CancellationToken.None);
    }

    private static async Task<GetHabitLogsQuery> BindRangeAsync(string? from, string? to)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(HabitsController).Assembly);
        using var provider = services.BuildServiceProvider();
        var action = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Single(action => action.ControllerTypeInfo.AsType() == typeof(HabitsController)
                && action.ActionName == nameof(HabitsController.GetLogs));
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var query = new Dictionary<string, string?>();
        if (from is not null) query["from"] = from;
        if (to is not null) query["to"] = to;
        httpContext.Request.QueryString = QueryString.Create(query);
        var context = new ActionContext(httpContext, new RouteData(), action);
        var metadataProvider = (ModelMetadataProvider)provider.GetRequiredService<IModelMetadataProvider>();
        var binderFactory = provider.GetRequiredService<IModelBinderFactory>();
        var parameterBinder = provider.GetRequiredService<ParameterBinder>();
        var values = new QueryStringValueProvider(BindingSource.Query, httpContext.Request.Query, CultureInfo.InvariantCulture);
        var dates = new Dictionary<string, DateOnly?>();
        foreach (var parameter in action.Parameters.OfType<ControllerParameterDescriptor>()
                     .Where(parameter => parameter.Name is "from" or "to"))
        {
            var metadata = metadataProvider.GetMetadataForParameter(parameter.ParameterInfo);
            var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                BindingInfo = parameter.BindingInfo,
                CacheToken = parameter
            });
            var result = await parameterBinder.BindModelAsync(context, binder, values, parameter, metadata, null);
            dates[parameter.Name] = (DateOnly?)result.Model;
        }

        context.ModelState.IsValid.Should().BeTrue();
        return new GetHabitLogsQuery(Guid.NewGuid(), Guid.NewGuid(), dates["from"], dates["to"]);
    }
}
