using Orbit.Application.ChecklistTemplates.Queries;

namespace Orbit.Application.Chat;

internal sealed record ChecklistTemplateModelPayload(int TotalCount,
    IReadOnlyList<ChecklistTemplateModelRow> Templates, int OmittedTemplates)
{
    public string Instruction => "Every template identity is listed. Names may be shortened. Item texts may be incomplete; compare items length with itemCount. "
        + "Before replacing or appending items, call get_checklist_templates with template_id to read the complete template if any items are missing. "
        + "Do not infer missing items. Complete items remain in the checklist-template application response. Emit the read directive instead of repeating records.";

    public static ChecklistTemplateModelPayload From(IReadOnlyList<ChecklistTemplateResponse> templates)
    {
        var nameLength = 64;
        var rows = templates.Select(template => new ChecklistTemplateModelRow(
            template.Id, ModelPayloadBudget.ShortName(template.Name), template.Items.Count, [])).ToList();
        var payload = new ChecklistTemplateModelPayload(templates.Count, rows, 0);
        while (!ModelPayloadBudget.Fits(payload, reservedCharacters: 1) && nameLength > 0)
        {
            nameLength /= 2;
            for (var index = 0; index < rows.Count; index++)
                rows[index] = rows[index] with { Name = ModelPayloadBudget.ShortName(templates[index].Name, nameLength) };
        }
        for (var index = 0; index < rows.Count; index++)
        {
            foreach (var item in templates[index].Items)
            {
                rows[index].Items.Add(item);
                if (ModelPayloadBudget.Fits(payload, reservedCharacters: 1)) continue;
                rows[index].Items.RemoveAt(rows[index].Items.Count - 1);
                break;
            }
        }
        return payload;
    }
}

internal sealed record ChecklistTemplateModelRow(Guid Id, string Name, int ItemCount, List<string> Items);
