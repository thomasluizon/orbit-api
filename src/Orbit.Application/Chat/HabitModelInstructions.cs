namespace Orbit.Application.Chat;

public static class HabitModelInstructions
{
    public const string Schedule = "A Task is one-time: frequency_unit=OneTime in create tools, stored as null, with due_date and requested due_time. A dated event or single dated instruction is a Task unless repetition was requested. A Habit has Day, Week, Month or Year and a positive frequency_quantity. Daily weekdays require Day/1; is_flexible tracks a quantity within a window and cannot have weekdays. General items have no schedule, recurrence or avoid type. end_date is inclusive and only recurring items use it. A temporary instruction for N days before an event uses one Task per day or a recurring Habit with end_date equal to the event date. Never leave temporary preparation recurring indefinitely.";

    public const string Polarity = "Use is_bad_habit=true only for ongoing reduction or slip tracking, named for the behaviour itself: Fumar, Tomar multivitamínico, Smoking, Drinking soda. For quero parar de fumar or I want to drink less soda, choose that avoid form. A temporary or dated abstinence is a positive commitment, is_bad_habit=false, named Não tomar multivitamínico or Don't take multivitamins, once or within its explicit date range. Never combine an avoid type with a negated name: não, sem, parar de, evitar, don't, stop, avoid.";

    public const string Correction = "A correction is a patch: send only fields the person requested changing. Preserve every other field, including one-time status, dates, times, recurrence, end date, reminders, tags and checklist. Changing polarity never changes recurrence. Revise a pending preview instead of creating a duplicate.";

    public const string DisplayDates = "Use ISO YYYY-MM-DD only in machine date fields. Dates inside titles and descriptions use the person's locale, DD/MM/YYYY in pt-BR and MM/DD/YYYY in en-US. Do not repeat the same date in ISO and local form.";

    public const string Batch = "For several creates in one request, call bulk_create_habits once with the entire set, including dated events and independent preparation commitments. Use its existing batch preview. Preparation on different dates needs separate trackable items, rather than only the exam description. A failed item keeps its name and reason and remains editable and retryable.";

    public const string Routine = "Recommend routine structure from the destination's checklist and sub-habits, then the account's structure pattern. Honor an explicit placement choice. For an addition to an existing routine, keep parent_habit_id and omit placement for the server recommendation; the preview offers checklist_item, sub_habit and new_parent with a reason. A brand-new routine on an account with no structure defaults to one parent with sub_habits. A morning or evening routine implies Day/1 unless another cadence was requested; weekday wording uses Day/1 with the requested days. Keep the parent's requested time and each child's own time, including lights out by 11 in an evening routine as 23:00. Durations belong to their activities. A missing hour does not block a preview: omit due_time when no hour was given. Propose the inferred cadence in the approval preview so the person can edit it. Only ask when no usable preview can be proposed. These rules apply equally to rotina da manhã, rotina da noite and dias úteis.";

    public static string All => string.Join("\n\n", Schedule, Polarity, Correction, DisplayDates, Batch, Routine);
}
