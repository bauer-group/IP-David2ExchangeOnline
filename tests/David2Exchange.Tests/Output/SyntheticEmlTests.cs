// SPDX-License-Identifier: MIT
using System.Text;
using David2Exchange.Output;
using MimeKit;

namespace David2Exchange.Tests.Output;

public sealed class SyntheticEmlTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-eml-" + Guid.NewGuid().ToString("N"));

    public SyntheticEmlTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private SyntheticEmlContent Content(
        string from = "kunde@example.com",
        string to = "ziel@example.com",
        string subject = "Betreff",
        string? text = "Text",
        string? html = null,
        params SyntheticAttachment[] attachments)
    {
        return new SyntheticEmlContent(
            from,
            to,
            null,
            subject,
            new DateTimeOffset(2024, 7, 1, 14, 30, 0, TimeSpan.FromHours(2)),
            "david-abc@mailbox.david.local",
            "sms",
            "35",
            text,
            html,
            attachments,
            "david.local");
    }

    private MimeMessage WriteAndParse(SyntheticEmlContent content)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".eml");
        SyntheticEml.Write(content, path);
        return MimeMessage.Load(path);
    }

    private string WriteAttachment(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Umlauts_in_headers_are_encoded_and_survive_a_round_trip()
    {
        var path = Path.Combine(_directory, "umlaut.eml");
        SyntheticEml.Write(Content(subject: "Rückruf für Müller – 5 €"), path);

        var firstLines = File.ReadAllText(path, Encoding.Latin1).Split("\r\n\r\n")[0];

        Assert.All(firstLines, character => Assert.True(character < 128, $"Header enthält Nicht-ASCII: {character}"));
        Assert.Equal("Rückruf für Müller – 5 €", MimeMessage.Load(path).Subject);
    }

    [Fact]
    public void Date_keeps_the_real_offset_instead_of_claiming_GMT()
    {
        var message = WriteAndParse(Content());

        Assert.Equal(new DateTimeOffset(2024, 7, 1, 12, 30, 0, TimeSpan.Zero), message.Date.ToUniversalTime());
    }

    [Theory]
    [InlineData("+49 9971 123456", "+49 9971 123456")]
    [InlineData("ACME Fax GmbH", "ACME Fax GmbH")]
    public void Phone_numbers_and_identifiers_become_valid_addresses_with_visible_original(string value, string display)
    {
        var message = WriteAndParse(Content(from: value));

        var sender = Assert.IsType<MailboxAddress>(message.From[0]);
        Assert.Equal(display, sender.Name);
        Assert.EndsWith("@david.local", sender.Address, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', sender.Address);
    }

    [Fact]
    public void Real_mail_addresses_stay_untouched()
    {
        var message = WriteAndParse(Content(from: "Max Müller <max@example.com>"));

        var sender = Assert.IsType<MailboxAddress>(message.From[0]);
        Assert.Equal("max@example.com", sender.Address);
        Assert.Equal("Max Müller", sender.Name);
    }

    [Fact]
    public void Voice_attachment_is_transferred_byte_exact_with_its_file_name()
    {
        var audio = new byte[200_000];
        new Random(7).NextBytes(audio);
        var path = WriteAttachment("nachricht.wav", audio);

        var message = WriteAndParse(Content(text: null, attachments: new SyntheticAttachment("Nachricht von Müller.wav", path)));

        var part = Assert.Single(message.Attachments.OfType<MimePart>());
        Assert.Equal("Nachricht von Müller.wav", part.FileName);
        Assert.StartsWith("audio/", part.ContentType.MimeType, StringComparison.Ordinal);
        using var decoded = new MemoryStream();
        part.Content!.DecodeTo(decoded);
        Assert.Equal(audio, decoded.ToArray());
    }

    [Fact]
    public void Fax_pdf_attachment_and_text_body_are_both_present()
    {
        var path = WriteAttachment("fax.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 test"));

        var message = WriteAndParse(Content(text: "Faxkopf", attachments: new SyntheticAttachment("fax.pdf", path)));

        Assert.Equal("Faxkopf", message.TextBody!.Trim());
        var part = Assert.Single(message.Attachments.OfType<MimePart>());
        Assert.Equal("application/pdf", part.ContentType.MimeType);
    }

    [Fact]
    public void Text_and_html_become_multipart_alternative()
    {
        var message = WriteAndParse(Content(text: "Text", html: "<p>Größe</p>"));

        Assert.Equal("Text", message.TextBody!.Trim());
        Assert.Contains("Größe", message.HtmlBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_body_lines_stay_within_the_rfc_line_limit()
    {
        var path = Path.Combine(_directory, "long.eml");
        SyntheticEml.Write(Content(text: new string('ä', 5_000)), path);

        var longest = File.ReadAllLines(path, Encoding.Latin1).Max(line => line.Length);

        Assert.True(longest <= 998, $"Zeile mit {longest} Zeichen verletzt RFC 5322.");
        Assert.Equal(new string('ä', 5_000), MimeMessage.Load(path).TextBody!.TrimEnd());
    }

    [Fact]
    public void Message_id_and_provenance_headers_are_written()
    {
        var message = WriteAndParse(Content());

        Assert.Equal("david-abc@mailbox.david.local", message.MessageId);
        Assert.Equal("sms", message.Headers["X-David-Type"]);
        Assert.Equal("35", message.Headers["X-David-Item-ID"]);
    }

    [Theory]
    [InlineData("a@b.de; c@d.de", 2)]
    [InlineData("max.mustermann@example.com;", 1)]
    [InlineData("Mueller, Max <max@example.com>", 1)]
    public void Address_lists_stay_real_recipients(string value, int expected)
    {
        var addresses = SyntheticEml.ToAddresses(value, "david.local");

        Assert.Equal(expected, addresses.Count);
        Assert.All(addresses, address => Assert.DoesNotContain("david.local", address.Address, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("+49 171 1234")]
    [InlineData("ACME, Fax GmbH")]
    public void Values_without_a_mail_address_keep_the_derived_address(string value)
    {
        var address = Assert.Single(SyntheticEml.ToAddresses(value, "david.local"));

        Assert.EndsWith("@david.local", address.Address, StringComparison.Ordinal);
        Assert.Equal(value, address.Name);
    }

    [Fact]
    public void Line_breaks_in_names_become_spaces()
    {
        Assert.Equal("Müller GmbH", SyntheticEml.ToAddress("Müller\r\nGmbH", "david.local").Name);
    }

    [Fact]
    public void Line_breaks_in_the_subject_do_not_join_words()
    {
        var path = Path.Combine(_directory, "umbruch.eml");

        SyntheticEml.Write(Content(subject: "Rechnung Januar\r\nMahnung"), path);

        Assert.Equal("Rechnung Januar Mahnung", MimeMessage.Load(path).Subject);
    }
}
