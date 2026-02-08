namespace Orbit.Domain.Entities;

public class HabitTag
{
    public Guid HabitId { get; private set; }
    public Guid TagId { get; private set; }

    public HabitTag(Guid habitId, Guid tagId)
    {
        HabitId = habitId;
        TagId = tagId;
    }

    private HabitTag() { }
}
