using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkUpdateHabitEmojisTool(
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "bulk_update_habit_emojis";

    public string Description =>
        "Update emojis for many habits in one operation. Use this when the user asks to change all habit emojis to sensible ones, or to set the same requested emoji on multiple habits.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = "Optional habit IDs to update. Omit to update all active habits owned by the user."
            },
            emoji = new
            {
                type = JsonSchemaTypes.String,
                description = "Optional emoji to apply to every selected habit. Set to null to clear. Omit when infer_from_title is true.",
                nullable = true
            },
            infer_from_title = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "When true, choose a sensible emoji from each habit title and description. Defaults to true when emoji is omitted."
            },
            include_completed = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "When true, also update completed habits. Defaults to false."
            }
        },
        required = Array.Empty<string>()
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var habitIds = JsonArgumentParser.ParseGuidArray(args, "habit_ids") ?? [];
        var includeCompleted = JsonArgumentParser.GetOptionalBool(args, "include_completed") ?? false;
        var hasEmojiArgument = JsonArgumentParser.PropertyExists(args, "emoji");
        var requestedEmoji = hasEmojiArgument ? JsonArgumentParser.GetNullableString(args, "emoji") : null;
        var inferFromTitle = JsonArgumentParser.GetOptionalBool(args, "infer_from_title") ?? !hasEmojiArgument;

        if (!inferFromTitle && !hasEmojiArgument)
            return new ToolResult(false, Error: "Provide emoji or set infer_from_title to true.");

        var habits = await habitRepository.FindTrackedAsync(
            habit => habit.UserId == userId
                && (habitIds.Count == 0 || habitIds.Contains(habit.Id))
                && (includeCompleted || !habit.IsCompleted),
            ct);

        if (habits.Count == 0)
            return new ToolResult(false, Error: "No matching habits found to update.");

        return UpdateHabitEmojis(habits, inferFromTitle, requestedEmoji);
    }

    private static ToolResult UpdateHabitEmojis(
        IReadOnlyList<Habit> habits,
        bool inferFromTitle,
        string? requestedEmoji)
    {
        var updated = new List<string>();
        foreach (var habit in habits.OrderBy(habit => habit.Position ?? int.MaxValue).ThenBy(habit => habit.Title))
        {
            var nextEmoji = inferFromTitle ? InferEmoji(habit) : requestedEmoji;
            var updateResult = ApplyEmoji(habit, nextEmoji);
            if (updateResult.IsSuccess)
                updated.Add($"{habit.Title} {nextEmoji ?? "cleared"}");
        }

        if (updated.Count == 0)
            return new ToolResult(false, Error: "No habit emojis were updated.");

        var preview = string.Join(", ", updated.Take(12));
        var suffix = updated.Count > 12 ? $", and {updated.Count - 12} more" : string.Empty;
        return new ToolResult(
            true,
            EntityName: $"Updated emojis for {updated.Count} habit(s): {preview}{suffix}",
            Payload: new { updated_count = updated.Count });
    }

    private static Result ApplyEmoji(Habit habit, string? emoji)
    {
        return habit.Update(new HabitUpdateParams(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.DueDate,
            DueTime: habit.DueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: habit.ReminderEnabled,
            ReminderTimes: habit.ReminderTimes,
            SlipAlertEnabled: habit.SlipAlertEnabled,
            ChecklistItems: habit.ChecklistItems,
            IsGeneral: habit.IsGeneral,
            IsFlexible: habit.IsFlexible,
            EndDate: habit.EndDate,
            ScheduledReminders: habit.ScheduledReminders,
            Emoji: emoji));
    }

    private static string InferEmoji(Habit habit)
    {
        var text = $"{habit.Title} {habit.Description}".ToLowerInvariant();
        foreach (var rule in InferenceRules)
        {
            if (rule.Keywords.Any(text.Contains))
                return rule.Emoji;
        }

        return "✨";
    }

    private static readonly IReadOnlyList<EmojiInferenceRule> InferenceRules =
    [
        new("🏋️", ["gym", "academia", "workout", "treino", "lift", "weights", "musculação", "exercise", "exercício"]),
        new("💪", ["strength", "push-up", "pushup", "muscle", "força", "flexão"]),
        new("🏃", ["run", "running", "corrida", "correr", "cardio"]),
        new("🚶", ["walk", "walking", "caminhada", "andar", "steps", "passos"]),
        new("🧘", ["meditate", "meditation", "mindfulness", "yoga", "meditar", "meditação"]),
        new("💧", ["water", "hydrate", "hydration", "água", "beber água", "hidratar"]),
        new("🥗", ["salad", "diet", "nutrition", "healthy", "nutrição", "dieta", "saudável"]),
        new("🍳", ["cook", "cooking", "meal", "cozinhar", "refeição"]),
        new("☕️", ["coffee", "café"]),
        new("😴", ["sleep", "bed", "sono", "dormir", "bedtime"]),
        new("📚", ["read", "book", "study", "learn", "ler", "livro", "estudar", "aprender"]),
        new("✍️", ["write", "journal", "diary", "escrever", "diário", "journaling"]),
        new("💻", ["code", "program", "work", "computer", "coding", "trabalho", "programar"]),
        new("💊", ["medicine", "medication", "pill", "remédio", "medicamento", "vitamin"]),
        new("🦷", ["teeth", "tooth", "floss", "brush", "dente", "escovar", "fio dental"]),
        new("🧹", ["clean", "tidy", "chores", "limpar", "faxina", "arrumar"]),
        new("🛒", ["shopping", "groceries", "market", "compras", "supermercado", "mercado"]),
        new("💰", ["money", "budget", "finance", "dinheiro", "finanças", "orçamento"]),
        new("🙏", ["pray", "prayer", "oração", "rezar"]),
        new("🌱", ["plant", "garden", "nature", "planta", "jardim", "natureza"]),
    ];

    private sealed record EmojiInferenceRule(string Emoji, IReadOnlyList<string> Keywords);
}
