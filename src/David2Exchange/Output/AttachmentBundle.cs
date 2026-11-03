// SPDX-License-Identifier: MIT
using MimeKit;

namespace David2Exchange.Output;

/// <summary>
/// Anhänge eines Termins, Kontakts oder einer Aufgabe zwischen David-Thread und Workern. Eine
/// MIME-Datei nutzt dieselbe Bereitstellung wie die EML-Dateien: ein Pfad, fertig erst nach dem Umbenennen.
/// </summary>
public static class AttachmentBundle
{
    public static void Write(string path, IReadOnlyCollection<SyntheticAttachment> attachments)
    {
        var body = new Multipart("mixed");
        var streams = new List<Stream>();
        try
        {
            foreach (var attachment in attachments)
            {
                var stream = File.OpenRead(attachment.Path);
                streams.Add(stream);

                // Immer als reine Daten: Eine .eml würde sonst zur eingebetteten Nachricht umgeformt.
                body.Add(new MimePart("application", "octet-stream")
                {
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    FileName = attachment.FileName,
                    Content = new MimeContent(stream),
                    ContentTransferEncoding = ContentEncoding.Base64
                });
            }

            new MimeMessage { Body = body }.WriteTo(path);
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    public static IReadOnlyList<(string Name, byte[] Content)> Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<(string, byte[])>();
        }

        var result = new List<(string, byte[])>();
        foreach (var part in MimeMessage.Load(path).Attachments.OfType<MimePart>())
        {
            using var content = new MemoryStream();
            part.Content!.DecodeTo(content);
            result.Add((part.FileName ?? "Anhang", content.ToArray()));
        }

        return result;
    }
}
