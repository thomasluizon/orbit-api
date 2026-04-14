using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetProfileTool(IMediator mediator) : IAiTool
{
    public string Name => "get_profile";
    public string Description => "Read the user's profile, plan, AI settings, timezone, language, theme, and calendar sync status.";
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
            : new ToolResult(false, Error: result.Error);
    }
}

public class UpdateProfilePreferencesTool(IMediator mediator) : IAiTool
{
    public string Name => "update_profile_preferences";
    public string Description => "Update profile preferences such as timezone, language, week start day, theme, color scheme, onboarding completion, or tour state.";

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
                    "set_theme_preference",
                    "set_color_scheme",
                    "complete_onboarding",
                    "complete_tour",
                    "reset_tour"
                }
            },
            timezone = new { type = JsonSchemaTypes.String, nullable = true },
            language = new { type = JsonSchemaTypes.String, nullable = true },
            week_start_day = new { type = JsonSchemaTypes.Integer, nullable = true },
            theme_preference = new { type = JsonSchemaTypes.String, nullable = true },
            color_scheme = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "set_timezone" => await SetTimezoneAsync(args, userId, ct),
            "set_language" => await SetLanguageAsync(args, userId, ct),
            "set_week_start_day" => await SetWeekStartDayAsync(args, userId, ct),
            "set_theme_preference" => await SetThemePreferenceAsync(args, userId, ct),
            "set_color_scheme" => await SetColorSchemeAsync(args, userId, ct),
            "complete_onboarding" => await ExecuteAsync(new CompleteOnboardingCommand(userId), userId, "Onboarding completed", ct),
            "complete_tour" => await ExecuteAsync(new CompleteTourCommand(userId), userId, "Tour completed", ct),
            "reset_tour" => await ExecuteAsync(new ResetTourCommand(userId), userId, "Tour reset", ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> SetTimezoneAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var timezone = JsonArgumentParser.GetOptionalString(args, "timezone");
        if (string.IsNullOrWhiteSpace(timezone))
            return new ToolResult(false, Error: "timezone is required.");

        return await ExecuteAsync(new SetTimezoneCommand(userId, timezone), userId, $"Timezone set to {timezone}", ct);
    }

    private async Task<ToolResult> SetLanguageAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var language = JsonArgumentParser.GetOptionalString(args, "language");
        if (string.IsNullOrWhiteSpace(language))
            return new ToolResult(false, Error: "language is required.");

        return await ExecuteAsync(new SetLanguageCommand(userId, language), userId, $"Language set to {language}", ct);
    }

    private async Task<ToolResult> SetWeekStartDayAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var weekStartDay = JsonArgumentParser.GetOptionalInt(args, "week_start_day");
        if (!weekStartDay.HasValue)
            return new ToolResult(false, Error: "week_start_day is required.");

        var label = weekStartDay == 0 ? "Sunday" : "Monday";
        return await ExecuteAsync(new SetWeekStartDayCommand(userId, weekStartDay.Value), userId, $"Week start day set to {label}", ct);
    }

    private async Task<ToolResult> SetThemePreferenceAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!JsonArgumentParser.PropertyExists(args, "theme_preference"))
            return new ToolResult(false, Error: "theme_preference is required.");

        var themePreference = JsonArgumentParser.GetNullableString(args, "theme_preference");
        return await ExecuteAsync(new SetThemePreferenceCommand(userId, themePreference), userId, "Theme preference updated", ct);
    }

    private async Task<ToolResult> SetColorSchemeAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!JsonArgumentParser.PropertyExists(args, "color_scheme"))
            return new ToolResult(false, Error: "color_scheme is required.");

        var colorScheme = JsonArgumentParser.GetNullableString(args, "color_scheme");
        return await ExecuteAsync(new SetColorSchemeCommand(userId, colorScheme), userId, "Color scheme updated", ct);
    }

    private async Task<ToolResult> ExecuteAsync(IRequest<Orbit.Domain.Common.Result> command, Guid userId, string entityName, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: entityName, Payload: new { success = true })
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }
}

public class UpdateAiSettingsTool(IMediator mediator) : IAiTool
{
    public string Name => "update_ai_settings";
    public string Description => "Enable or disable AI memory and daily AI summary settings.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new
            {
                type = JsonSchemaTypes.String,
                @enum = new[] { "set_ai_memory", "set_ai_summary" }
            },
            enabled = new { type = JsonSchemaTypes.Boolean }
        },
        required = new[] { "action", "enabled" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        var enabled = JsonArgumentParser.GetOptionalBool(args, "enabled");
        if (string.IsNullOrWhiteSpace(action) || !enabled.HasValue)
            return new ToolResult(false, Error: "action and enabled are required.");

        Orbit.Domain.Common.Result result = action switch
        {
            "set_ai_memory" => await mediator.Send(new SetAiMemoryCommand(userId, enabled.Value), ct),
            "set_ai_summary" => await mediator.Send(new SetAiSummaryCommand(userId, enabled.Value), ct),
            _ => Orbit.Domain.Common.Result.Failure($"Unsupported action '{action}'.")
        };

        return result.IsSuccess
            ? new ToolResult(
                true,
                EntityId: userId.ToString(),
                EntityName: action == "set_ai_memory"
                    ? $"AI memory {(enabled.Value ? "enabled" : "disabled")}"
                    : $"AI summary {(enabled.Value ? "enabled" : "disabled")}",
                Payload: new { action, enabled })
            : new ToolResult(false, EntityId: userId.ToString(), Error: result.Error);
    }
}
