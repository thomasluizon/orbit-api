using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

internal static class ConversationOutcomeWriter
{
    internal sealed record Patch(Guid Id, string Value, string? Response = null);

    public static void Apply(OrbitDbContext db, IReadOnlyList<Patch> patches)
    {
        if (patches.Count == 0) return;
        if (!db.Database.IsRelational())
        {
            var byId = patches.ToDictionary(patch => patch.Id);
            foreach (var turn in db.ConversationTurns.Where(turn => byId.Keys.Contains(turn.Id)))
            {
                db.Entry(turn).Property(row => row.PreviewOutcomesJson).CurrentValue = byId[turn.Id].Value;
                if (byId[turn.Id].Response is { } response)
                    db.Entry(turn).Property(row => row.ResponseJson).CurrentValue = response;
                db.Entry(turn).Property(row => row.ModelContextJson).CurrentValue = null;
            }
            return;
        }
        var json = JsonSerializer.Serialize(patches.Select(patch => patch with
        {
            Value = db.EncryptConversationValue(patch.Value),
            Response = patch.Response is null ? null : db.EncryptConversationValue(patch.Response)
        }));
        if (db.Database.IsNpgsql())
            db.Database.ExecuteSqlInterpolated($"""
                UPDATE "ConversationTurns" AS turn
                SET "PreviewOutcomesJson" = patch."Value", "ResponseJson" = COALESCE(patch."Response", turn."ResponseJson"), "ModelContextJson" = NULL
                FROM jsonb_to_recordset(CAST({json} AS jsonb)) AS patch("Id" uuid, "Value" text, "Response" text)
                WHERE turn."Id" = patch."Id"
                """);
        else
            db.Database.ExecuteSqlInterpolated($"""
                UPDATE "ConversationTurns" AS turn
                SET "PreviewOutcomesJson" = json_extract(patch.value, '$.Value'),
                    "ResponseJson" = COALESCE(json_extract(patch.value, '$.Response'), turn."ResponseJson"), "ModelContextJson" = NULL
                FROM json_each({json}) AS patch
                WHERE UPPER(turn."Id") = UPPER(json_extract(patch.value, '$.Id'))
                """);
    }
}
