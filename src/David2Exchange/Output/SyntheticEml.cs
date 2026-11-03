// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace David2Exchange.Output;

public sealed record SyntheticAttachment(string FileName, string Path);

/// <summary>Inhalt einer Nachricht für Items ohne native David-EML (Fax, SMS, Voice, Chat).</summary>
public sealed record SyntheticEmlContent(
    string From,
    string To,
    string? Cc,
    string Subject,
    DateTimeOffset Date,
    string MessageId,
    string Kind,
    string DavidId,
    string? BodyText,
    string? BodyHtml,
    IReadOnlyList<SyntheticAttachment> Attachments,
    string AddressDomain);

public static partial class SyntheticEml
{
    /// <summary>
    /// Schreibt die Nachricht als RFC-5322-Datei. Anhänge werden gestreamt, weil der Prozess
    /// x86 ist und Voice-Dateien groß sein können.
    /// </summary>
    public static void Write(SyntheticEmlContent content, string path)
    {
        var message = new MimeMessage
        {
            Subject = OneLine(content.Subject),
            Date = content.Date,
            MessageId = content.MessageId,
            MimeVersion = new Version(1, 0)
        };

        message.From.Add(ToAddress(content.From, content.AddressDomain));
        message.To.AddRange(ToAddresses(content.To, content.AddressDomain));
        if (!string.IsNullOrWhiteSpace(content.Cc))
        {
            message.Cc.AddRange(ToAddresses(content.Cc, content.AddressDomain));
        }

        message.Headers.Add("X-David-Type", content.Kind);
        message.Headers.Add("X-David-Item-ID", content.DavidId);

        var streams = new List<Stream>();
        try
        {
            message.Body = BuildBody(content, streams);

            // Wählt für Texte eine 7-Bit-Kodierung mit gültiger Zeilenlänge.
            message.Prepare(EncodingConstraint.SevenBit);

            using var output = File.Create(path);
            message.WriteTo(output);
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// David liefert Adressen, Rufnummern oder freie Kennungen. Alles ohne gültige Mailadresse
    /// wird Anzeigename einer abgeleiteten Adresse, damit der Header gültig bleibt und die
    /// Originalangabe sichtbar ist.
    /// </summary>
    public static MailboxAddress ToAddress(string value, string domain)
    {
        var text = OneLine(value);
        if (text.Contains('@', StringComparison.Ordinal) &&
            MailboxAddress.TryParse(text, out var parsed) &&
            parsed.Address.Contains('@', StringComparison.Ordinal))
        {
            return parsed;
        }

        var local = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                local.Append(character);
            }
        }

        return new MailboxAddress(text, $"{(local.Length == 0 ? "david" : local)}@{domain}");
    }

    /// <summary>
    /// Empfängerlisten bleiben einzelne Empfänger, statt zu einer erfundenen Adresse zu verschmelzen.
    /// Eine Liste gilt nur, wenn jeder Eintrag eine echte Mailadresse ist.
    /// </summary>
    public static IReadOnlyList<MailboxAddress> ToAddresses(string value, string domain)
    {
        var text = OneLine(value);
        if (text.Contains('@', StringComparison.Ordinal) &&
            !MailboxAddress.TryParse(text, out _) &&
            InternetAddressList.TryParse(text.Replace(';', ','), out var list) &&
            list.Count > 0 &&
            list.All(address => address is MailboxAddress mailbox && mailbox.Address.Contains('@', StringComparison.Ordinal)))
        {
            return list.Cast<MailboxAddress>().ToList();
        }

        // Das Semikolon ist der Trenner aus DavidComItem: Jeder Eintrag zählt einzeln, echte Adressen bleiben echt.
        var entries = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Length > 0
            ? entries.Select(entry => ToAddress(entry, domain)).ToList()
            : new[] { ToAddress(text, domain) };
    }

    /// <summary>Ein Zeilenumbruch in Betreff oder Name würde sonst Wörter zusammenziehen.</summary>
    private static string OneLine(string value)
    {
        return LineBreaks().Replace(value, " ").Trim();
    }

    private static MimeEntity BuildBody(SyntheticEmlContent content, List<Stream> streams)
    {
        MimeEntity? text = string.IsNullOrWhiteSpace(content.BodyText)
            ? null
            : new TextPart("plain") { Text = content.BodyText };
        MimeEntity? html = string.IsNullOrWhiteSpace(content.BodyHtml)
            ? null
            : new TextPart("html") { Text = content.BodyHtml };

        MimeEntity body = (text, html) switch
        {
            (not null, not null) => new MultipartAlternative { text, html },
            (not null, null) => text,
            (null, not null) => html,
            _ => new TextPart("plain") { Text = "" }
        };

        if (content.Attachments.Count == 0)
        {
            return body;
        }

        var mixed = new Multipart("mixed") { body };
        foreach (var attachment in content.Attachments)
        {
            var stream = File.OpenRead(attachment.Path);
            streams.Add(stream);
            mixed.Add(new MimePart(MimeTypes.GetMimeType(attachment.FileName))
            {
                Content = new MimeContent(stream),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = attachment.FileName
            });
        }

        return mixed;
    }

    [GeneratedRegex(@"[\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreaks();
}
