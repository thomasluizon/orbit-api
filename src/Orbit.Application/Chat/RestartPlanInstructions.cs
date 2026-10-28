namespace Orbit.Application.Chat;

public static class RestartPlanInstructions
{
    public const string English = "When the person asks for help restarting after missed habits, read the remaining habits due today with query_habits, including overdue entries. "
        + "Read further pages when needed before proposing the complete plan. Use the returned habits and the person's preferences to propose a smaller set for today and later dates for the rest. "
        + "Call bulk_reschedule_habits once with revised_items containing every selected habit and its proposed due_date, including today's choices. "
        + "Preserve titles, cadence, times, reminders, checklists and past records. Never mark missed habits done or skipped. "
        + "Use the existing preview for approval, editing or rejection; nothing changes before approval. "
        + "Set orbit_read_complete to false while gathering the plan and orbit_write_complete to true when the complete plan is ready. "
        + "Return the preview alone, with no extra read blocks or repeated facts. Advice without a request to change the plan remains coaching.";
}
