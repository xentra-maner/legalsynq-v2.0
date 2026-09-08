using System.Globalization;
using System.Text;
using Liens.Application.Services;
using Liens.Domain.Enums;

namespace Liens.Infrastructure.Persistence;

internal static class RootEntityHistoryFormatter
{
    private const string LegacyMetadataMarker = "[legacy-meta]";

    internal static string BuildDescription(string activity, IReadOnlyCollection<LienFieldChange> changes) =>
        LienUpdateHistoryFormatter.BuildSingleDescription(activity, changes);

    internal static string BuildCreationDescription(string activity, IReadOnlyCollection<LienFieldChange> fields) =>
        LienUpdateHistoryFormatter.BuildCreationDescription(activity, fields);

    internal static string ExtractActivity(string description)
    {
        var changesIndex = description.IndexOf(" Changes:", StringComparison.Ordinal);
        var activity = changesIndex >= 0 ? description[..changesIndex] : description;
        return activity.Trim().TrimEnd('.');
    }

    internal static IReadOnlyList<LienFieldChange> ExpandCaseNotes(object? previousValue, object? currentValue)
    {
        var previous = ParseCaseNotes(previousValue as string);
        var current = ParseCaseNotes(currentValue as string);
        var changes = new List<LienFieldChange>();

        AddIfChanged(changes, "Notes", previous.NoteBody, current.NoteBody);
        foreach (var key in previous.Metadata.Keys.Union(current.Metadata.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            if (key is "currentMedicalStatus" or "accidentState" or "trackingFollowUpDate" or "minorComp" or "caseDropped")
                continue;

            previous.Metadata.TryGetValue(key, out var oldValue);
            current.Metadata.TryGetValue(key, out var newValue);
            AddIfChanged(changes, DisplayMetadataField(key), oldValue, newValue);
        }

        return changes;
    }

    internal static bool ValuesEqual(object? previousValue, object? currentValue)
    {
        if (previousValue is string || currentValue is string)
        {
            var left = (previousValue as string)?.Trim() ?? string.Empty;
            var right = (currentValue as string)?.Trim() ?? string.Empty;
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        return Equals(previousValue, currentValue);
    }

    /// <summary>
    /// Compares the legacy yes/no flag columns (<c>IsBulk</c>, <c>IsServicing</c>) semantically so a
    /// cosmetic re-encoding such as "N" → "No" or "Y" → "Yes" is not logged as a real change.
    /// </summary>
    internal static bool FlagValuesEqual(object? previousValue, object? currentValue) =>
        string.Equals(
            NormalizeFlag(previousValue as string),
            NormalizeFlag(currentValue as string),
            StringComparison.Ordinal);

    private static string NormalizeFlag(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" or "1" => "YES",
            _ => "NO",
        };

    internal static bool CaseStatusesEqual(object? previousValue, object? currentValue) =>
        string.Equals(
            NormalizeCaseStatus(previousValue as string),
            NormalizeCaseStatus(currentValue as string),
            StringComparison.Ordinal);

    internal static object? DisplayLienStatus(object? value)
    {
        if (value is not string status)
            return value;

        return status switch
        {
            LienStatus.Cancelled or LienStatus.Declined => "Rejected",
            LienStatus.Settled or LienStatus.Withdrawn => "Closed",
            _ => "Open",
        };
    }

    private static string NormalizeCaseStatus(string? value)
    {
        var normalized = value?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized?.ToUpperInvariant() switch
        {
            "NEGOTIATIONS" or "INNEGOTIATION" => "INNEGOTIATION",
            "PREDEMAND" => "PREDEMAND",
            "DEMANDSENT" => "DEMANDSENT",
            "CASESETTLED" or "SETTLED" => "CASESETTLED",
            "CLOSED" => "CLOSED",
            _ => normalized?.ToUpperInvariant() ?? string.Empty,
        };
    }

    private static void AddIfChanged(
        ICollection<LienFieldChange> changes,
        string field,
        object? previousValue,
        object? currentValue)
    {
        if (!ValuesEqual(previousValue, currentValue))
            changes.Add(new LienFieldChange(field, previousValue, currentValue));
    }

    private static (string? NoteBody, Dictionary<string, string> Metadata) ParseCaseNotes(string? notes)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(notes))
            return (null, metadata);

        var markerIndex = notes.IndexOf(LegacyMetadataMarker, StringComparison.Ordinal);
        var hasMarker = markerIndex >= 0;
        var noteBody = hasMarker ? Normalize(notes[..markerIndex]) : null;
        var rawMetadata = hasMarker
            ? notes[(markerIndex + LegacyMetadataMarker.Length)..].Trim()
            : notes;

        foreach (var segment in rawMetadata.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (key.Length > 0)
                metadata[key] = value;
        }

        // A value without the marker is a note unless it actually parsed as legacy metadata.
        if (!hasMarker && metadata.Count == 0)
            noteBody = Normalize(notes);

        return (noteBody, metadata);
    }

    private static string DisplayMetadataField(string key)
    {
        var known = key switch
        {
            "gender" => "Gender",
            "accidentType" => "Case Type",
            "accidentTypeId" => "Accident Type ID",
            "currentMedicalStatus" => "Current Medical Status",
            "accidentState" => "State of Incident",
            "trackingFollowUpDate" => "Tracking Follow Up Date",
            "leadId" => "Lead ID",
            "shareCase" => "Share Case",
            "minorComp" => "Minor Comp",
            "caseDropped" => "Case Dropped",
            "childSupportLiens" => "Child Support Liens",
            "isUccFiled" => "UCC Filed",
            "lawFirmId" => "Law Firm ID",
            "pendingLawFirmId" => "Pending Law Firm ID",
            "caseManagerId" => "Case Manager ID",
            "statusLabel" => "Status Label",
            "switchedDate" => "Law Firm Switch Date",
            _ => Humanize(key),
        };
        return known;
    }

    private static string Humanize(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(value[index - 1]))
                builder.Append(' ');
            builder.Append(index == 0 ? char.ToUpper(current, CultureInfo.InvariantCulture) : current);
        }
        return builder.ToString();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
