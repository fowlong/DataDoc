using System.Globalization;
using System.Text.RegularExpressions;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Transforms;

/// <summary>
/// Applies an ordered list of <see cref="TransformRule"/> objects to a string value.
/// Each rule is applied in sequence; disabled rules are skipped.
/// </summary>
public class TransformService
{
    private readonly ILogger<TransformService> _logger;

    public TransformService(ILogger<TransformService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies all enabled transform rules to <paramref name="value"/> in order and returns the result.
    /// </summary>
    public string Apply(string? value, IReadOnlyList<TransformRule> rules)
    {
        var result = value ?? string.Empty;

        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Order))
        {
            try
            {
                result = ApplyRule(result, rule);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transform rule '{Type}' failed on value '{Value}'", rule.Type, result);
            }
        }

        return result;
    }

    private static string ApplyRule(string value, TransformRule rule)
    {
        return rule.Type switch
        {
            "Trim" => value.Trim(),

            "CollapseSpaces" => CollapseSpaces(value),

            "RemoveLineBreaks" => value
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " "),

            "RegexExtract" => RegexExtract(value, rule.Parameter),

            "RegexReplace" => RegexReplace(value, rule.Parameter, rule.Parameter2),

            "SplitTake" => SplitTake(value, rule.Parameter, rule.Parameter2),

            "DateNormalize" => DateNormalize(value, rule.Parameter, rule.Parameter2),

            "NumberCleanup" => NumberCleanup(value),

            "Uppercase" => value.ToUpperInvariant(),

            "Lowercase" => value.ToLowerInvariant(),

            "TitleCase" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),

            "Prefix" => (rule.Parameter ?? "") + value,

            "Suffix" => value + (rule.Parameter ?? ""),

            "JoinLines" => JoinLines(value, rule.Parameter),

            "DefaultValue" => string.IsNullOrWhiteSpace(value) ? (rule.Parameter ?? "") : value,

            _ => value
        };
    }

    private static string CollapseSpaces(string value)
    {
        return Regex.Replace(value, @"\s{2,}", " ");
    }

    private static string RegexExtract(string value, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return value;

        var match = Regex.Match(value, pattern);
        if (!match.Success)
            return value;

        // Return the first capture group if available, otherwise the full match.
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

    private static string RegexReplace(string value, string? pattern, string? replacement)
    {
        if (string.IsNullOrEmpty(pattern))
            return value;

        return Regex.Replace(value, pattern, replacement ?? "");
    }

    private static string SplitTake(string value, string? separator, string? indexStr)
    {
        var sep = separator ?? " ";
        if (!int.TryParse(indexStr, out var index))
            index = 0;

        var parts = value.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        if (index < 0)
            index = parts.Length + index; // support negative indexing

        return index >= 0 && index < parts.Length ? parts[index] : value;
    }

    private static string DateNormalize(string value, string? inputFormat, string? outputFormat)
    {
        var outFmt = string.IsNullOrEmpty(outputFormat) ? "yyyy-MM-dd" : outputFormat;

        if (!string.IsNullOrEmpty(inputFormat))
        {
            if (DateTime.TryParseExact(value, inputFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var exactDate))
                return exactDate.ToString(outFmt, CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
            return parsedDate.ToString(outFmt, CultureInfo.InvariantCulture);

        return value;
    }

    private static string NumberCleanup(string value)
    {
        // Remove common non-numeric noise characters (currency symbols, spaces, commas used as
        // thousands separators) while preserving digits, decimal points, and minus signs.
        var cleaned = Regex.Replace(value, @"[^\d.\-]", "");

        if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return number.ToString(CultureInfo.InvariantCulture);

        return cleaned;
    }

    private static string JoinLines(string value, string? separator)
    {
        var sep = separator ?? " ";
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(sep, lines.Select(l => l.Trim()));
    }
}
