namespace Orbit.Application.Chat;

public static class RestartPlanInstructions
{
    public const string ProposalBoundary = "A request for a restart plan after missed habits is already actionable: propose the plan yourself. Never call ask or request a choice, pick list, count or scheduling preference before the restart preview. The person can approve, edit or reject that preview.";

    public const string FreshLookup = "Before every fresh restart proposal, call query_habits in this turn even when the account index already contains the habit IDs. The index does not establish which occurrences remain due or overdue; build revised_items only from the complete fresh query result.";

    public const string English = ProposalBoundary + " " + FreshLookup + " "
        + "Read the remaining habits due today with query_habits, including overdue entries, is_completed=false, for_selection=false and orbit_read_complete=false. "
        + "Read further pages when needed before proposing the complete plan. Keep strictly fewer of those habits today than the complete missed and due set. "
        + "When the person gives a count for today, keep exactly that count; otherwise choose a smaller manageable set from the returned habits. "
        + "Assign every other habit a concrete due_date after today and within the next seven days, using the person's local today. Keeping the whole set today is not a restart plan. "
        + "Call bulk_reschedule_habits once with revised_items containing every habit in the complete set and its proposed due_date, including today's choices. "
        + "Check the today count and every deferred date before calling the tool; never use one shared due_date for a restart plan. "
        + "Preserve titles, cadence, times, reminders, checklists and past records. Never mark missed habits done or skipped. "
        + "Use the existing preview for approval, editing or rejection; nothing changes before approval. "
        + "If the person rejects a plan and wants another, propose different per-habit dates in a held preview, never a plain habit list. "
        + "After cancellation call bulk_reschedule_habits with the complete revised_items; for edits to a still-pending preview use revise_pending_changes and retain unmentioned items. "
        + "Set orbit_write_complete to true when the complete plan is ready. "
        + "Return the preview alone, with no extra read blocks or repeated facts. Advice without a request to change the plan remains coaching.";
}
