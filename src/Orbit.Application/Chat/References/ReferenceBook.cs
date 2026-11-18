using System.Text.RegularExpressions;

namespace Orbit.Application.Chat.References;

/// <summary>
/// The person's records of each listed type for one chat turn. It writes model text with references in place of the
/// GUIDs of listed records and resolves a reference of one type. It loads a type only when the turn needs it.
/// </summary>
public sealed partial class ReferenceBook(Func<ReferenceRecordType, CancellationToken, Task<IReadOnlyList<ReferenceRecord>>> load)
    : IDisposable
{
    /// <summary>
    /// The types that the book holds. A preview item resolves against the items of its own preview, and its GUID part
    /// is a record of one of these types.
    /// </summary>
    private static readonly ReferenceRecordType[] BookTypes =
    [
        ReferenceRecordType.Habit, ReferenceRecordType.Goal, ReferenceRecordType.Tag, ReferenceRecordType.ChecklistTemplate,
        ReferenceRecordType.Notification, ReferenceRecordType.ApiKey, ReferenceRecordType.Skip, ReferenceRecordType.PendingOperation
    ];

    private readonly Dictionary<ReferenceRecordType, IReadOnlyList<ReferenceRecord>> _records = [];
    private readonly HashSet<ReferenceRecordType> _reloaded = [];
    private readonly HashSet<ReferenceRecordType> _partial = [];
    private readonly HashSet<Guid> _unlisted = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, int> _lengths = [];

    /// <summary>
    /// Gives the book the records of one type that the turn already read. A part of the list, such as the active goals,
    /// is read in full when model text holds a GUID that the book cannot classify.
    /// </summary>
    public void Seed(ReferenceRecordType type, IEnumerable<ReferenceRecord> records, bool complete = true)
    {
        _gate.Wait();
        try
        {
            _records[type] = records.ToArray();
            if (complete)
                _partial.Remove(type);
            else
                _partial.Add(type);
            RebuildLengths();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Resolves a reference among the person's records of one type. A miss reads the type once more in the turn, so a
    /// record that the turn made after the first read resolves too.
    /// </summary>
    public async Task<ReferenceResolution> ResolveAsync(ReferenceRecordType type, string text, CancellationToken ct)
    {
        var resolution = ReferenceCodes.Resolve(await RecordsAsync(type, reload: false, ct), text);
        return resolution.IsResolved ? resolution : ReferenceCodes.Resolve(await RecordsAsync(type, reload: true, ct), text);
    }

    /// <summary>
    /// The model text with the reference of each GUID of a listed record. A GUID of no listed type stays at full length.
    /// </summary>
    public async Task<string> WriteAsync(string text, CancellationToken ct)
    {
        var ids = GuidText().Matches(text).Select(match => Guid.Parse(match.Value)).Distinct().ToArray();
        if (ids.Length == 0)
            return text;
        await _gate.WaitAsync(ct);
        try
        {
            if (ids.Any(IsUnclassified))
            {
                foreach (var type in BookTypes.Where(type => !_records.ContainsKey(type) || _partial.Contains(type)))
                {
                    _records[type] = Merge(_records.GetValueOrDefault(type), await load(type, ct));
                    _reloaded.Add(type);
                    _partial.Remove(type);
                }
                RebuildLengths();
            }
            if (ids.Any(IsUnclassified))
            {
                _records[ReferenceRecordType.PendingOperation] = Merge(_records[ReferenceRecordType.PendingOperation],
                    await load(ReferenceRecordType.PendingOperation, ct));
                RebuildLengths();
                _unlisted.UnionWith(ids.Where(IsUnclassified));
            }
        }
        finally { _gate.Release(); }
        return GuidText().Replace(text, match => _lengths.TryGetValue(Guid.Parse(match.Value), out var length)
            ? ReferenceCodes.Hex(Guid.Parse(match.Value))[..length]
            : match.Value);
    }

    private bool IsUnclassified(Guid id) => !_lengths.ContainsKey(id) && !_unlisted.Contains(id);

    private async Task<IReadOnlyList<ReferenceRecord>> RecordsAsync(ReferenceRecordType type, bool reload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_records.TryGetValue(type, out var loaded) && (!reload || !_reloaded.Add(type)))
                return loaded;
            var records = Merge(loaded, await load(type, ct));
            _records[type] = records;
            _reloaded.Add(type);
            _partial.Remove(type);
            RebuildLengths();
            return records;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A second read adds the records that the turn made after the first read. A record that the turn removed stays, and
    /// the ownership check still turns down a write on it.
    /// </summary>
    private static IReadOnlyList<ReferenceRecord> Merge(IReadOnlyList<ReferenceRecord>? earlier, IReadOnlyList<ReferenceRecord> current) =>
        earlier is null ? current : earlier.UnionBy(current, record => record.Id).ToArray();

    /// <summary>A GUID that two types hold shows the longer reference, so it selects one record in each type.</summary>
    private void RebuildLengths()
    {
        var lengths = new Dictionary<Guid, int>();
        foreach (var records in _records.Values)
            foreach (var (id, length) in ReferenceCodes.Lengths(records))
                lengths[id] = Math.Max(length, lengths.GetValueOrDefault(id));
        _lengths = lengths;
        _unlisted.ExceptWith(lengths.Keys);
    }

    public void Dispose() => _gate.Dispose();

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidText();
}
