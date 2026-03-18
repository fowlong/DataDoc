using System.Globalization;
using System.Text.RegularExpressions;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Validation;

/// <summary>
/// Validates a string value against a list of <see cref="ValidationRule"/> objects.
/// Returns a <see cref="ValidationState"/> containing the aggregate result and individual messages.
/// </summary>
public class ValidationService
{
    private readonly ILogger<ValidationService> _logger;

    public ValidationService(ILogger<ValidationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates <paramref name="value"/> against all enabled rules and returns the resulting state.
    /// </summary>
    public ValidationState Validate(string? value, IReadOnlyList<ValidationRule> rules)
    {
        var state = new ValidationState { IsValid = true, Messages = [] };

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            try
            {
                var message = EvaluateRule(value, rule);
                if (message != null)
                {
                    state.Messages.Add(message);
                    if (message.Severity == ValidationSeverity.Error)
                        state.IsValid = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Validation rule '{Type}' threw an exception", rule.Type);
                state.Messages.Add(new ValidationMessage
                {
                    Text = rule.Message ?? $"Validation rule '{rule.Type}' failed: {ex.Message}",
                    Severity = ValidationSeverity.Error,
                    RuleName = rule.Type
                });
                state.IsValid = false;
            }
        }

        return state;
    }

    private static ValidationMessage? EvaluateRule(string? value, ValidationRule rule)
    {
        bool passed = rule.Type switch
        {
            "Required" => !string.IsNullOrWhiteSpace(value),
            "Regex" => EvaluateRegex(value, rule.Parameter),
            "MinLength" => EvaluateMinLength(value, rule.Parameter),
            "MaxLength" => EvaluateMaxLength(value, rule.Parameter),
            "AllowedValues" => EvaluateAllowedValues(value, rule.Parameter),
            "Numeric" => EvaluateNumeric(value),
            "Date" => EvaluateDate(value, rule.Parameter),
            _ => true // unknown rule types pass by default
        };

        if (passed)
            return null;

        return new ValidationMessage
        {
            Text = rule.Message ?? GetDefaultMessage(rule.Type, rule.Parameter),
            Severity = rule.Severity,
            RuleName = rule.Type
        };
    }

    private static bool EvaluateRegex(string? value, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return true;

        // Null/empty values fail regex validation (use Required rule to allow empties).
        if (string.IsNullOrEmpty(value))
            return false;

        return Regex.IsMatch(value, pattern);
    }

    private static bool EvaluateMinLength(string? value, string? parameter)
    {
        if (!int.TryParse(parameter, out var minLen))
            return true;

        return (value?.Length ?? 0) >= minLen;
    }

    private static bool EvaluateMaxLength(string? value, string? parameter)
    {
        if (!int.TryParse(parameter, out var maxLen))
            return true;

        return (value?.Length ?? 0) <= maxLen;
    }

    private static bool EvaluateAllowedValues(string? value, string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
            return true;

        if (string.IsNullOrEmpty(value))
            return false;

        var allowed = parameter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool EvaluateNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    private static bool EvaluateDate(string? value, string? format)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!string.IsNullOrEmpty(format))
        {
            return DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static string GetDefaultMessage(string ruleType, string? parameter)
    {
        return ruleType switch
        {
            "Required" => "This field is required.",
            "Regex" => $"Value does not match pattern '{parameter}'.",
            "MinLength" => $"Value must be at least {parameter} characters.",
            "MaxLength" => $"Value must be at most {parameter} characters.",
            "AllowedValues" => $"Value must be one of: {parameter?.Replace('|', ',')}.",
            "Numeric" => "Value must be a number.",
            "Date" => string.IsNullOrEmpty(parameter)
                ? "Value must be a valid date."
                : $"Value must be a valid date in format '{parameter}'.",
            _ => $"Validation failed for rule '{ruleType}'."
        };
    }
}
