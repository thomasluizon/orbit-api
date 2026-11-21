using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed partial class PreviewGoldenTests
{
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static PendingOperationItem Item(PendingOperationChange? field = null, Guid? entity = null, bool? removesData = false,
        PendingOperationPlacement? placement = null) => new("0", entity ?? Target, "Water",
        [field ?? new PendingOperationChange(Target, "Water", "title", "Water", "Agua", "text", JsonSerializer.SerializeToElement("Agua"))],
        "state", removesData, Placement: placement);

    private static PendingOperationChange Title => Item().Fields[0];

    public static TheoryData<string> DisplayOnlyDifferences() => ["provenance", "removal"];

    [Theory]
    [MemberData(nameof(DisplayOnlyDifferences))]
    public void HashView_LeavesOutTheOfferAnnotations(string difference)
    {
        var annotated = difference == "provenance"
            ? Item(Title with { Provenance = "suggested" })
            : Item(Title with { Removal = new PendingProposalRemoval("preview:key", "title", ["title"]) });

        PreviewHashView.Compute("update_habit", [annotated]).Should().Be(PreviewHashView.Compute("update_habit", [Item()]));
    }

    public static TheoryData<string> ExecutedDifferences() => ["value", "field", "target", "removes_data", "placement"];

    [Theory]
    [MemberData(nameof(ExecutedDifferences))]
    public void HashView_KeepsEveryValueThatDecidesWhatExecutes(string difference)
    {
        var changed = difference switch
        {
            "value" => Item(Title with { NewValue = "Água" }),
            "field" => Item(Title with { Field = "description" }),
            "target" => Item(entity: Guid.Parse("00000000-0000-0000-0000-000000000002")),
            "removes_data" => Item(removesData: true),
            _ => Item(placement: new PendingOperationPlacement("sub_habit", "sub_habit", "chat.placement.destination",
                new Dictionary<string, string>(), ["new_parent"], Target, "Routine"))
        };

        PreviewHashView.Compute("update_habit", [changed]).Should().NotBe(PreviewHashView.Compute("update_habit", [Item()]));
    }

    [Fact]
    public async Task HashView_ChangeSetMemberOfferMetadataGivesOneFingerprint()
    {
        var (_, operationId, arguments, _) = Cases().Single(item => item.Name == "14-set-members");
        var element = JsonDocument.Parse(arguments).RootElement.Clone();

        var plain = await _previewer.PreviewAsync(UserId, operationId, element, default, Client("en", offers: false));
        var offered = await _previewer.PreviewAsync(UserId, operationId, element, default, Client("en", offers: true));

        offered.Value!.ExecutionArguments!.Value.GetRawText().Should().Contain("orbit_offer_metadata");
        plain.Value!.ExecutionArguments!.Value.GetRawText().Should().NotContain("orbit_offer_metadata");
        offered.Value.PreviewFingerprint.Should().Be(plain.Value.PreviewFingerprint);
    }
}
