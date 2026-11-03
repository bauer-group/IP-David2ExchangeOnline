// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;

namespace David2Exchange.Runner;

/// <summary>
/// Bildet ein Manifest-Item auf seine Staging-Pfade ab. Der Name leitet sich aus dem
/// vollständigen Manifest-Schlüssel ab, weil David-IDs nur je Archiv eindeutig sind:
/// Eingang\35 und Ausgang\35 sind verschiedene Nachrichten.
/// </summary>
internal static class StagingLayout
{
    public static string Token(string itemKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(itemKey.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public static string EmlPath(string mailboxDirectory, string itemKey)
    {
        return Path.Combine(mailboxDirectory, Token(itemKey) + ".eml");
    }

    public static string WorkDirectory(string mailboxDirectory, string itemKey)
    {
        return Path.Combine(mailboxDirectory, "work", Token(itemKey));
    }
}
