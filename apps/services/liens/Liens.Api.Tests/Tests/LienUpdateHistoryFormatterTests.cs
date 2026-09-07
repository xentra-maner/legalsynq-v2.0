using Liens.Application.Services;

namespace Liens.Api.Tests.Tests;

public sealed class LienUpdateHistoryFormatterTests
{
    [Fact]
    public void BuildDescriptions_preserves_every_changed_field_within_storage_limit()
    {
        var changes = Enumerable.Range(1, 12)
            .Select(index => new LienFieldChange(
                $"Changed Field {index}",
                new string('a', 150),
                new string('b', 150)))
            .ToArray();

        var descriptions = LienUpdateHistoryFormatter.BuildDescriptions("Lien updated", changes);

        descriptions.Should().HaveCountGreaterThan(1);
        descriptions.Should().OnlyContain(description => description.Length <= 500);
        var combined = string.Join(' ', descriptions);
        foreach (var change in changes)
            combined.Should().Contain($"{change.Field}:");
    }

    [Fact]
    public void BuildSingleDescription_preserves_every_changed_field_in_one_row()
    {
        var changes = Enumerable.Range(1, 12)
            .Select(index => new LienFieldChange(
                $"Changed Field {index}",
                new string('a', 150),
                new string('b', 150)))
            .ToArray();

        var description = LienUpdateHistoryFormatter.BuildSingleDescription("Lien Update", changes);

        description.Should().StartWith("Lien Update. Changes:");
        description.Length.Should().BeGreaterThan(500);
        foreach (var change in changes)
            description.Should().Contain($"{change.Field}:");
    }

    [Fact]
    public void BuildCreationDescription_uses_current_values_without_blank_transitions()
    {
        var fields = new[]
        {
            new LienFieldChange("Lien Code", null, "26-10008-1"),
            new LienFieldChange("Status", null, string.Empty),
            new LienFieldChange("Purchase Date", null, new DateOnly(2026, 6, 22)),
        };

        var description = LienUpdateHistoryFormatter.BuildCreationDescription("Lien Created", fields);

        description.Should().Be(
            "Lien Created. Changes: Lien Code: 26-10008-1; Status: \"\"; Purchase Date: 06/22/2026.");
        description.Should().NotContain("blank");
        description.Should().NotContain("→");
    }
}
