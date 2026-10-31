// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using David2Exchange.Runner;

namespace David2Exchange.Config;

public sealed record MailboxMatch(
    MailboxTarget? Target,
    string Method,
    int Score);

public static partial class MailboxMatcher
{
    /// <summary>
    /// Sicherer Match ausschließlich über E-Mail-Adressen (primär oder Alias).
    /// Anzeigenamen sind für eine Migration kein belastbarer Schlüssel.
    /// </summary>
    public static MailboxMatch MatchByEmail(
        string sourceEmail,
        IReadOnlyList<MailboxTarget> targets,
        ISet<string> usedUpns)
    {
        var email = NormalizeEmail(sourceEmail);
        if (string.IsNullOrWhiteSpace(email))
        {
            return new MailboxMatch(null, "missing-source-email", 0);
        }

        foreach (var target in targets)
        {
            if (usedUpns.Contains(target.PrimarySmtpAddress))
            {
                continue;
            }

            if (NormalizeEmail(target.PrimarySmtpAddress) == email)
            {
                return new MailboxMatch(target, "email-primary", 100);
            }

            if ((target.EmailAddresses ?? new List<string>()).Any(address => NormalizeEmail(address) == email))
            {
                return new MailboxMatch(target, "email-alias", 95);
            }
        }

        return new MailboxMatch(null, "unmatched", 0);
    }

    public static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "";
        }

        var value = email.Trim();
        if (value.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[5..];
        }

        return value.Trim().ToLowerInvariant();
    }

    public static string LocalPart(string upn)
    {
        var index = upn.IndexOf('@', StringComparison.Ordinal);
        return (index > 0 ? upn[..index] : upn).Trim().ToLowerInvariant();
    }

    public static string DomainOf(string upn)
    {
        var index = upn.IndexOf('@', StringComparison.Ordinal);
        return index > 0 ? upn[(index + 1)..].Trim().ToLowerInvariant() : "";
    }

    public static string Slug(string value)
    {
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0)
        {
            value = value[..comma];
        }

        var ascii = value
            .Replace("ä", "ae", StringComparison.OrdinalIgnoreCase)
            .Replace("ö", "oe", StringComparison.OrdinalIgnoreCase)
            .Replace("ü", "ue", StringComparison.OrdinalIgnoreCase)
            .Replace("ß", "ss", StringComparison.OrdinalIgnoreCase);

        var normalized = Regex.Replace(ascii.ToLowerInvariant(), "[^a-z0-9]+", ".")
            .Trim('.');
        return string.IsNullOrWhiteSpace(normalized) ? "mailbox" : normalized.Replace('.', '-');
    }
}
