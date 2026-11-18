using System.Globalization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

/// <summary>
/// The place of a habit among the habits of the account that share its title, in the app list order, and the title
/// of its parent. The selection card and the preview read this one source, so they give a record one place.
/// </summary>
public sealed record SameTitlePlace(int Place, int Total, string? ParentTitle)
{
    /// <summary>The place in the group: "5 of 6", or "5º de 6" in Portuguese.</summary>
    public string PlaceText(bool portuguese) => string.Format(CultureInfo.InvariantCulture,
        ErrorCopy.Resolve("chat.preview.sameTitlePlace", portuguese), Place, Total);

    /// <summary>The parent title and the place: "Morning, 5 of 6". A record with no parent has the place only.</summary>
    public string Detail(bool portuguese) => ParentTitle is null ? PlaceText(portuguese)
        : string.Format(CultureInfo.InvariantCulture, ErrorCopy.Resolve("chat.preview.sameTitleDetail", portuguese),
            ParentTitle, PlaceText(portuguese));

    /// <summary>The title with the detail: "Water (Morning, 5 of 6)".</summary>
    public string Name(string title, bool portuguese) => string.Format(CultureInfo.InvariantCulture,
        ErrorCopy.Resolve("chat.preview.sameTitleName", portuguese), title, Detail(portuguese));

    /// <summary>The line of a renamed record: "Water (Morning, 5 of 6) → Agua".</summary>
    public string Rename(string title, string newTitle, bool portuguese) => string.Format(CultureInfo.InvariantCulture,
        ErrorCopy.Resolve("chat.preview.sameTitleRename", portuguese), Name(title, portuguese), newTitle);
}

/// <summary>
/// The same-title groups of an account. A group holds each habit of the account with one
/// <see cref="HabitNameMatcher.TitleKey"/>, in the order of <see cref="HabitAppListOrder"/>. Only a habit in a group
/// of two or more has a place.
/// </summary>
public sealed class SameTitlePlaces
{
    public static readonly SameTitlePlaces None = new([]);

    private readonly Dictionary<Guid, SameTitlePlace> _places = [];
    private readonly Dictionary<Guid, int> _listIndex = [];

    public SameTitlePlaces(IEnumerable<Habit> habits)
    {
        var ordered = HabitAppListOrder.Order(habits);
        var titles = ordered.ToDictionary(habit => habit.Id, habit => habit.Title);
        for (var index = 0; index < ordered.Count; index++)
            _listIndex[ordered[index].Id] = index;
        foreach (var group in ordered.GroupBy(habit => HabitNameMatcher.TitleKey(habit.Title)).Select(group => group.ToArray()))
        {
            if (group.Length < 2) continue;
            for (var index = 0; index < group.Length; index++)
                _places[group[index].Id] = new(index + 1, group.Length,
                    group[index].ParentHabitId is { } parent ? titles.GetValueOrDefault(parent) : null);
        }
    }

    /// <summary>The place of the habit, or null when no other habit of the account shares its title.</summary>
    public SameTitlePlace? Find(Guid habitId) => _places.GetValueOrDefault(habitId);

    /// <summary>The index of the habit in the app list. A habit that the account does not hold comes last.</summary>
    public int ListIndex(Guid habitId) => _listIndex.GetValueOrDefault(habitId, int.MaxValue);
}
