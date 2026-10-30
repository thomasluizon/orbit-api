namespace Orbit.Application.Chat;

public static class AnswerCompositionInstructions
{
    public const string ReadPlanning = """
        A reminder asking what remains today uses query_habits with date=today and is_completed=false; it does not require choosing a reminder or a habit.
        An identified activity that the person wants to make easier to start uses suggest_breakdown with one_small_step=true, without asking them to select an existing habit. Account resolution is needed only for a requested save.
        Every option presented as an existing account record must come from the account index or a successful read; never invent an alternative record.
        For a named habit's completion rate or completion total, resolve its ID with query_habits and orbit_read_complete=false, then call get_habit_metrics. include_metrics on query_habits does not replace get_habit_metrics for a completion question.
        A single discrete selection always uses ask form choice with top-level question and options, including when the question also requests a free-text change. Reserve form question for multiple separate input fields.
        A current streak total does not establish historical missed days. Never label days before a habit's creation as not logged or missed, or infer those states from a current streak alone.
        """;
}
