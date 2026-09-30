using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetProfileTool(IMediator mediator) : IAiTool
{
    public string Name => "get_profile";
    public string Description => "Read the user's profile, plan, AI settings, timezone, clock format, light or dark mode, and calendar sync status. Orbit uses one accent.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetProfileQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}

public class UpdateProfilePreferencesTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "update_profile_preferences";
    public string Description => "Update profile preferences such as timezone, language, week start day, clock format, light or dark mode, onboarding completion, or tour state. Orbit uses one accent.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new
            {
                type = JsonSchemaTypes.String,
                @enum = new[]
                {
                    "set_timezone",
                    "set_language",
                    "set_week_start_day",
                    "set_clock_format",
                    "set_theme_preference",
                    "complete_onboarding",
                    "complete_tour",
                    "reset_tour"
                }
            },
            timezone = new { type = JsonSchemaTypes.String, nullable = true },
            language = new { type = JsonSchemaTypes.String, nullable = true },
            week_start_day = new { type = JsonSchemaTypes.Integer, nullable = true },
            uses_24_hour_clock = new { type = JsonSchemaTypes.Boolean, nullable = true },
            theme_preference = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "set_timezone" => await SetTimezoneAsync(args, userId, ct, checkOnly),
            "set_language" => await SetLanguageAsync(args, userId, ct, checkOnly),
            "set_week_start_day" => await SetWeekStartDayAsync(args, userId, ct, checkOnly),
            "set_clock_format" => await SetClockFormatAsync(args, userId, ct, checkOnly),
            "set_theme_preference" => await SetThemePreferenceAsync(args, userId, ct, checkOnly),
            "complete_onboarding" => await ExecuteAsync(new CompleteOnboardingCommand(userId), userId, "Onboarding completed", ct, checkOnly),
            "complete_tour" => await ExecuteAsync(new CompleteTourCommand(userId), userId, "Tour completed", ct, checkOnly),
            "reset_tour" => await ExecuteAsync(new ResetTourCommand(userId), userId, "Tour reset", ct, checkOnly),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> SetTimezoneAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var timezone = JsonArgumentParser.GetOptionalString(args, "timezone");
        if (string.IsNullOrWhiteSpace(timezone))
            return new ToolResult(false, Error: "timezone is required.");

        return await ExecuteAsync(new SetTimezoneCommand(userId, timezone), userId, $"Timezone set to {timezone}", ct, checkOnly);
    }

    private async Task<ToolResult> SetLanguageAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var language = JsonArgumentParser.GetOptionalString(args, "language");
        if (string.IsNullOrWhiteSpace(language))
            return new ToolResult(false, Error: "language is required.");

        return await ExecuteAsync(new SetLanguageCommand(userId, language), userId, $"Language set to {language}", ct, checkOnly);
    }

    private async Task<ToolResult> SetWeekStartDayAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var weekStartDay = JsonArgumentParser.GetOptionalInt(args, "week_start_day");
        if (!weekStartDay.HasValue)
            return new ToolResult(false, Error: "week_start_day is required.");

        var label = weekStartDay == 0 ? "Sunday" : "Monday";
        return await ExecuteAsync(new SetWeekStartDayCommand(userId, weekStartDay.Value), userId, $"Week start day set to {label}", ct, checkOnly);
    }

    private async Task<ToolResult> SetClockFormatAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var uses24HourClock = JsonArgumentParser.GetOptionalBool(args, "uses_24_hour_clock");
        if (!uses24HourClock.HasValue)
            return new ToolResult(false, Error: "uses_24_hour_clock is required.");

        var label = uses24HourClock.Value ? "24-hour" : "12-hour";
        return await ExecuteAsync(new SetClockFormatCommand(userId, uses24HourClock.Value), userId, $"Clock set to {label}", ct, checkOnly);
    }

    private async Task<ToolResult> SetThemePreferenceAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!JsonArgumentParser.PropertyExists(args, "theme_preference"))
            return new ToolResult(false, Error: "theme_preference is required.");

        var themePreference = JsonArgumentParser.GetNullableString(args, "theme_preference");
        return await ExecuteAsync(new SetThemePreferenceCommand(userId, themePreference), userId, "Theme preference updated", ct, checkOnly);
    }

    private async Task<ToolResult> ExecuteAsync(IRequest<Orbit.Domain.Common.Result> command, Guid userId, string entityName, CancellationToken ct, bool checkOnly)
    {
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: entityName, Payload: new { success = true })
            : ToolResult.FromFailure(result, userId.ToString());
    }
}

public abstract class ToggleProfileSettingTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    private const string EnabledState = "enabled";
    private const string DisabledState = "disabled";

    public abstract string Name { get; }
    public abstract string Description { get; }
    protected abstract IRequest<Orbit.Domain.Common.Result> CreateCommand(Guid userId, bool enabled);
    protected abstract string SettingLabel { get; }

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            enabled = new { type = JsonSchemaTypes.Boolean }
        },
        required = new[] { "enabled" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        var enabled = JsonArgumentParser.GetOptionalBool(args, "enabled");
        if (!enabled.HasValue)
            return new ToolResult(false, Error: "enabled is required.");

        var command = CreateCommand(userId, enabled.Value);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);

        var entityName = $"{SettingLabel} {(enabled.Value ? EnabledState : DisabledState)}";
        return result.IsSuccess
            ? new ToolResult(
                true,
                EntityId: userId.ToString(),
                EntityName: entityName,
                Payload: new { enabled })
            : ToolResult.FromFailure(result, userId.ToString());
    }
}

public class SetAiMemoryTool(IMediator mediator) : ToggleProfileSettingTool(mediator)
{
    public override string Name => "set_ai_memory";
    public override string Description => "Enable or disable AI memory.";
    protected override string SettingLabel => "AI memory";

    protected override IRequest<Orbit.Domain.Common.Result> CreateCommand(Guid userId, bool enabled) =>
        new SetAiMemoryCommand(userId, enabled);
}

public class SetAiSummaryTool(IMediator mediator) : ToggleProfileSettingTool(mediator)
{
    public override string Name => "set_ai_summary";
    public override string Description => "Enable or disable the premium AI summary setting.";
    protected override string SettingLabel => "AI summary";

    protected override IRequest<Orbit.Domain.Common.Result> CreateCommand(Guid userId, bool enabled) =>
        new SetAiSummaryCommand(userId, enabled);
}
