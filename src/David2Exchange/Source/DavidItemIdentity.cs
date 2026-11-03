// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace David2Exchange.Source;

/// <summary>
/// Stabile Kennung eines David-Items innerhalb seines Archivs.
///
/// <para>
/// <c>_ID</c> ist nur die Position im Archiv (lückenlos 0..n-1, auch im laufend geleerten
/// COMMON). Nach Löschungen oder einer David-Bereinigung trüge ein anderes Item dieselbe ID.
/// Stabil ist dagegen die Datei des Items, die <c>TextSource</c> bzw. <c>Image</c> nennen.
/// </para>
/// </summary>
public static partial class DavidItemIdentity
{
    /// <summary>Präfix inhaltsbasierter Kennungen; nur diese können mehrfach vorkommen.</summary>
    public const string ContentPrefix = "h:";

    public static string Of(IDavidItem item)
    {
        return FileToken(item.SourceFile)
               ?? FileToken(item.FaxImagePath)
               ?? ContentToken(item);
    }

    /// <summary>
    /// Vereinheitlicht die Schreibweisen I[0000000000000F93E.001 und I00F93E.001, damit ein
    /// Entpacken des Archivs die Kennung nicht ändert.
    /// </summary>
    public static string? FileToken(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var name = path[(path.LastIndexOf('\\') + 1)..].Trim().ToUpperInvariant();
        if (name.Length == 0)
        {
            return null;
        }

        var match = DavidFileName().Match(name);
        return match.Success
            ? $"{match.Groups["prefix"].Value}{match.Groups["number"].Value.TrimStart('0')}.{match.Groups["extension"].Value}"
            : name;
    }

    private static string ContentToken(IDavidItem item)
    {
        var parts = new[]
        {
            item.Type,
            // Bei Kontakten ist StatusTime nur der Änderungsstempel und keine Identität.
            item.Type == "1" ? null : item.ReceivedLocal?.ToString("O"),
            item.Subject,
            Get(item, "From"),
            Get(item, "To"),
            Get(item, "DisplayName"),
            Get(item, "EMail"),
            Get(item, "FirstName"),
            Get(item, "LastName"),
            Get(item, "StartTime")
        };
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return ContentPrefix + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private static string? Get(IDavidItem item, string name)
    {
        return item.Properties.TryGetValue(name, out var value) ? value : null;
    }

    // Lazy: Die führenden Hex-Buchstaben einer Nummer (I0CD58, ICD058) gehören nicht zum Präfix.
    [GeneratedRegex(@"^(?<prefix>[A-Z]+?)\[?(?<number>[0-9A-F]+)\.(?<extension>[0-9A-Z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DavidFileName();
}
