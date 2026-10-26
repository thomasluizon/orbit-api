using Orbit.Application.ChecklistTemplates.Queries;

namespace Orbit.Application.Chat;

internal sealed record ChecklistTemplateModelPayload(int TotalCount,
    IReadOnlyList<ChecklistTemplateModelRow> Templates, int OmittedTemplates)
{
    public string Instruction => "Only listed templates have model facts. Names may be shortened and item texts are omitted. "
        + "The client template list has paging; complete items remain in the checklist-template application response. "
        + "Emit the read directive instead of repeating records. Do not infer omitted facts or retry with narrower filters.";

    public static ChecklistTemplateModelPayload From(IReadOnlyList<ChecklistTemplateResponse> templates)
    {
        var rows = templates.Take(RecordListCardBuilder.PageSize).Select(template => new ChecklistTemplateModelRow(
            template.Id, ShortName(template.Name), template.Items.Count)).ToList();
        return new(templates.Count, rows, templates.Count - rows.Count);
    }

    private static string ShortName(string name)
    {
        if (name.Length <= 64) return name;
        var length = char.IsHighSurrogate(name[63]) ? 63 : 64;
        return name[..length];
    }
}

internal sealed record ChecklistTemplateModelRow(Guid Id, string Name, int ItemCount);
