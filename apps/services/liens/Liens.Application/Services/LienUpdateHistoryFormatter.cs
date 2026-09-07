using System.Globalization;
using System.Text;

namespace Liens.Application.Services;

public sealed record LienFieldChange(string Field, object? PreviousValue, object? NewValue);

public static class LienUpdateHistoryFormatter
{
    private const int MaxDescriptionLength = 500;
    private const int MaxActivityLength = 150;
    private const int MaxValueLength = 120;

    public static IReadOnlyList<string> BuildDescriptions(
        string activity,
        IReadOnlyCollection<LienFieldChange> changes)
    {
        var normalizedActivity = NormalizeActivity(activity);
        if (changes.Count == 0)
            return [normalizedActivity];

        var descriptions = new List<string>();
        var header = $"{normalizedActivity} Changes: ";
        var current = header;

        foreach (var change in changes)
        {
            var segment = FormatChange(change);
            var separator = current.Length == header.Length ? string.Empty : "; ";
            if (current.Length + separator.Length + segment.Length + 1 > MaxDescriptionLength)
            {
                descriptions.Add(EnsurePeriod(current));
                header = "Additional lien changes: ";
                current = header;
                separator = string.Empty;
            }

            current += separator + segment;
        }

        descriptions.Add(EnsurePeriod(current));
        return descriptions;
    }

    public static string BuildSingleDescription(
        string activity,
        IReadOnlyCollection<LienFieldChange> changes)
    {
        var normalizedActivity = NormalizeActivity(activity);
        if (changes.Count == 0)
            return normalizedActivity;

        return EnsurePeriod(
            $"{normalizedActivity} Changes: {string.Join("; ", changes.Select(FormatChange))}");
    }

    public static string BuildCreationDescription(
        string activity,
        IReadOnlyCollection<LienFieldChange> fields)
    {
        var normalizedActivity = NormalizeActivity(activity);
        if (fields.Count == 0)
            return normalizedActivity;

        return EnsurePeriod(
            $"{normalizedActivity} Changes: {string.Join("; ", fields.Select(FormatCreatedField))}");
    }

    public static string DisplayFieldName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return "Field";

        var builder = new StringBuilder(propertyName.Length + 8);
        for (var index = 0; index < propertyName.Length; index++)
        {
            var current = propertyName[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(propertyName[index - 1]))
                builder.Append(' ');
            builder.Append(current);
        }

        return builder.ToString()
            .Replace(" Id", " ID", StringComparison.Ordinal)
            .Replace(" Utc", " UTC", StringComparison.Ordinal);
    }

    private static string FormatChange(LienFieldChange change) =>
        $"{change.Field}: {FormatValue(change.PreviousValue)} → {FormatValue(change.NewValue)}";

    private static string FormatCreatedField(LienFieldChange field) =>
        $"{field.Field}: {FormatCreatedValue(field.NewValue)}";

    private static string FormatCreatedValue(object? value) =>
        value is null || value is string text && string.IsNullOrWhiteSpace(text)
            ? "\"\""
            : FormatValue(value);

    private static string FormatValue(object? value)
    {
        var formatted = value switch
        {
            null => "blank",
            string text => NormalizeText(text),
            DateOnly date => date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            decimal amount => amount.ToString("0.00", CultureInfo.InvariantCulture),
            bool flag => flag ? "Yes" : "No",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "blank",
        };

        return formatted.Length <= MaxValueLength
            ? formatted
            : $"{formatted[..(MaxValueLength - 1)]}…";
    }

    private static string NormalizeActivity(string activity)
    {
        var normalized = NormalizeText(activity);
        if (normalized.Length > MaxActivityLength)
            normalized = $"{normalized[..(MaxActivityLength - 1)]}…";
        return EnsurePeriod(normalized);
    }

    private static string NormalizeText(string value)
    {
        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return string.IsNullOrEmpty(normalized) ? "blank" : normalized;
    }

    private static string EnsurePeriod(string value)
    {
        var trimmed = value.TrimEnd();
        return trimmed.EndsWith(".", StringComparison.Ordinal) ? trimmed : $"{trimmed}.";
    }
}
