using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed record HabitCorrectionProposal(string Message, JsonElement Changes, Habit Current);

public sealed partial class HabitCorrectionProposalValidator : AbstractValidator<HabitCorrectionProposal>
{
    public HabitCorrectionProposalValidator()
    {
        RuleFor(proposal => proposal).Custom((proposal, context) =>
        {
            var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(proposal.Changes, allowHabitId: true);
            if (error is not null || changes is null)
                return;
            var current = proposal.Current;
            var resolved = HabitUpdateValidation.ResolveSingleUpdate(current, changes, current.NextDueDate);
            if (resolved.IsFailure)
            {
                if (!ScheduleEdit().IsMatch(proposal.Message))
                    context.AddFailure(new ValidationFailure("frequency_unit", HabitModelInstructions.Correction)
                    { ErrorCode = "CORRECTION_PRESERVES_SCHEDULE" });
                return;
            }
            var update = resolved.Value;
            if (OneTimeEdit().IsMatch(proposal.Message) && !HabitProposalValidator.RequestsRepetition(proposal.Message)
                && update.FrequencyUnit is not null)
            {
                context.AddFailure(new ValidationFailure("frequency_unit", "A requested one-time Task cannot become recurring.")
                { ErrorCode = "DATED_INSTRUCTION_REQUIRES_TASK" });
                return;
            }
            if (ScheduleEdit().IsMatch(proposal.Message))
                return;
            var end = update.ClearEndDate == true ? null : update.EndDate ?? current.EndDate;
            if (update.FrequencyUnit != current.FrequencyUnit || update.FrequencyQuantity != current.FrequencyQuantity
                || update.IntervalWeeks != current.IntervalWeeks || update.Schedule != current.Schedule || end != current.EndDate
                || !current.Days.ToHashSet().SetEquals(update.Days ?? [])
                || ChangesMonthDays(current, update))
                context.AddFailure(new ValidationFailure("frequency_unit", HabitModelInstructions.Correction)
                { ErrorCode = "CORRECTION_PRESERVES_SCHEDULE" });
        });
    }

    /// <summary>
    /// Returns the text that states the person's request for this turn. A reply that only confirms the previous
    /// request ("Yes, do that", "Sim, pode fazer") carries the schedule words of the previous user message, so a
    /// confirmed schedule change is not read as a correction. Any other message states its own request.
    /// </summary>
    public static string RequestText(string message, IReadOnlyList<ChatHistoryMessage>? history)
    {
        if (!Confirmation().IsMatch(HabitNameMatcher.Normalize(message)))
            return message;
        var previous = history?.LastOrDefault(item => item.Role == "user")?.Content;
        return string.IsNullOrWhiteSpace(previous) ? message : previous + "\n" + message;
    }

    private static bool ChangesMonthDays(Domain.Entities.Habit current, Domain.Entities.HabitUpdateParams update) =>
        update.MonthDays is { } monthDays
        && !current.MonthDays.SequenceEqual(Domain.Common.MonthDaySchedule.Normalize(monthDays));

    [GeneratedRegex(@"\b(?:schedule|recurr\w*|frequen\w*|daily|weekly|monthly|yearly|every|once|one.time|one.off|task|day|days|week|weeks|month|end|until|calend[aá]rio|agenda|repet\w*|di[aá]ri\w*|seman\w*|mensal|anual|todo|todos|cada|vez|vezes|tarefa|[uú]nic\w*|dias?|fim|termin\w*|at[eé])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScheduleEdit();

    [GeneratedRegex(@"^(?:yes|yeah|yep|yup|sure|ok|okay|go ahead|do (?:it|that|this)|sounds good|confirm(?:ed)?|sim|claro|isso|beleza|certo|pode (?:fazer|preparar|seguir|ser)|faca (?:isso|assim)|confirmo|manda ver)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Confirmation();

    [GeneratedRegex(@"\b(?:one.time|one.off|tasks?|tarefas?|[uú]nic\w*|uma\s+vez)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneTimeEdit();
}
