// SPDX-License-Identifier: MIT
using System.Collections;
using System.Runtime.InteropServices;
using David2Exchange.Infrastructure;
using David2Exchange.Source;
using David2Exchange.Source.David;

namespace David2Exchange.Tests.Source;

/// <summary>
/// Die echten David-Klassen über spät gebundenen Objekten. Fehlende Member und HRESULTs
/// entsprechen dem, was am echten David je Item-Typ gemessen wurde.
/// </summary>
public sealed class DavidComItemTests : IDisposable
{
    private const string FaxImage = @"\\srv\david\archive\user\1\out\M[000000000000CDD58.001";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-com-" + Guid.NewGuid().ToString("N"));

    public DavidComItemTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static COMException EFail() => new("Unbekannter Fehler", unchecked((int)0x80004005));

    private static COMException RpcFailure() => new("RPC-Server nicht verfügbar", unchecked((int)0x800706BA));

    public sealed class FieldObject
    {
        public FieldObject(object? value)
        {
            Value = value;
        }

        public object? Value { get; }
    }

    public sealed class FieldsObject
    {
        public Dictionary<string, object?> Values { get; } = new();

        public FieldObject Item(string name)
        {
            return Values.TryGetValue(name, out var value) ? new FieldObject(value) : throw EFail();
        }
    }

    /// <summary>Wie FaxItem: kein From, keine Datumsfelder, Attachments meldet E_FAIL.</summary>
    public sealed class RawFax
    {
        public object _ID => 31;

        public object Type => 3;

        public string Subject => "";

        public string To => "+49 9971 1";

        public string Image => FaxImage;

        public object Attachments => throw EFail();

        public FieldsObject Fields { get; } = new();
    }

    /// <summary>Wie VoiceItem laut Typbibliothek: Content und Caller statt Image und From.</summary>
    public sealed class RawVoice
    {
        public object _ID => 5;

        public object Type => 5;

        public string Subject => "Sprachnachricht";

        public string To => "";

        public string Content { get; set; } = @"\\srv\david\archive\user\1\in\V[0000000000A1.WAV";

        public string Caller => "+49 171 222";

        public object[] Attachments => Array.Empty<object>();

        public FieldsObject Fields { get; } = new();
    }

    public sealed class RawMail
    {
        public object _ID => 1;

        public object Type => TypeFailure is null ? 2 : throw TypeFailure;

        public Exception? TypeFailure { get; set; }

        public string Subject => "Betreff";

        public object? Attachments => AttachmentsFailure is null ? AttachmentList : throw AttachmentsFailure;

        public object[] AttachmentList { get; set; } = Array.Empty<object>();

        public Exception? AttachmentsFailure { get; set; }

        public object BodyText => BodyFailure is null ? new RawBody() : throw BodyFailure;

        public Exception? BodyFailure { get; set; }

        public object VCard => throw EFail();

        public object[] CC => new object[] { new RawAddress("a@example.com"), new RawAddress("b@example.com") };

        public object[] Recipients => new object[] { new RawAddress("c@example.com") };
    }

    public sealed class RawBody
    {
        public string PlainText => "Inhalt";
    }

    public sealed class RawAddress
    {
        public RawAddress(string address)
        {
            Address = address;
        }

        public string Address { get; }
    }

    public sealed class RawAttachment
    {
        public string FileName { get; set; } = "datei.pdf";

        public long FileSize { get; set; } = 1000;

        public string? _Path { get; set; }

        public Action<string>? Copy { get; set; }

        public void CopyOut(string destination, string name)
        {
            Copy?.Invoke(destination);
        }
    }

    public sealed class RawArchiveWithBrokenName
    {
        public string ID => @"\\srv\david\ARCHIVE\user\1\in";

        public string DisplayName => throw RpcFailure();
    }

    public sealed class ShortCollection : IEnumerable
    {
        public int Count => 3;

        public IEnumerator GetEnumerator()
        {
            yield return "einziger Eintrag";
        }
    }

    public sealed class GapCollection : IEnumerable
    {
        public IEnumerator GetEnumerator()
        {
            yield return "erster";
            yield return null;
        }
    }

    public sealed class NotEnumerable
    {
        public object _NewEnum => throw EFail();
    }

    [Fact]
    public void Fax_takes_date_and_read_state_from_fields()
    {
        var raw = new RawFax();
        raw.Fields.Values["StatusTime"] = new DateTime(2007, 3, 1, 9, 0, 0);
        raw.Fields.Values["SendTime"] = new DateTime(1970, 1, 1, 1, 0, 0);
        raw.Fields.Values["Viewed"] = true;
        var item = new DavidComItem(@"user\1\out", raw);

        Assert.Equal(new DateTime(2007, 3, 1, 9, 0, 0), item.ReceivedLocal);
        Assert.Null(item.SentLocal);
        Assert.True(item.IsRead);
    }

    [Fact]
    public void Fax_attachments_reporting_e_fail_count_as_none()
    {
        var raw = new RawFax();
        raw.Fields.Values["AttachCount"] = 0;

        Assert.Empty(new DavidComItem(@"user\1\out", raw).Attachments);
    }

    [Fact]
    public void Fax_declaring_attachments_it_cannot_read_is_an_error()
    {
        var raw = new RawFax();
        raw.Fields.Values["AttachCount"] = 2;

        var error = Assert.Throws<InvalidOperationException>(() => new DavidComItem(@"user\1\out", raw).Attachments);

        Assert.Contains("2 Anhänge", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fax_sender_falls_back_to_the_fields()
    {
        var raw = new RawFax();
        raw.Fields.Values["FullnameFrom"] = "";
        raw.Fields.Values["SRFrom"] = "ACME";

        Assert.Equal("ACME", new DavidComItem(@"user\1\in", raw).Properties["From"]);
    }

    [Fact]
    public void Voice_uses_content_and_caller()
    {
        var item = new DavidComItem(@"user\1\in", new RawVoice());

        Assert.Equal(new RawVoice().Content, item.FaxImagePath);
        Assert.Equal("+49 171 222", item.Properties["From"]);
        Assert.Equal("VA1.WAV", DavidItemIdentity.FileToken(item.FaxImagePath));
    }

    [Fact]
    public void Voice_content_delivered_as_bytes_is_used_directly()
    {
        var path = Path.Combine(_directory, "nachricht.wav");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var item = new DavidComItem(@"user\1\in", new RawVoice { Content = path });

        Assert.Equal(new byte[] { 1, 2, 3 }, item.FaxImage);
    }

    [Fact]
    public void Mail_attachment_read_error_stays_an_error()
    {
        var raw = new RawMail { AttachmentsFailure = EFail() };

        Assert.Throws<COMException>(() => new DavidComItem(@"user\1\in", raw).Attachments);
    }

    [Fact]
    public void Read_error_on_the_type_is_not_treated_as_unknown_type()
    {
        var raw = new RawMail { TypeFailure = RpcFailure() };

        var error = Assert.Throws<COMException>(() => new DavidComItem(@"user\1\in", raw).Type);

        Assert.Contains("RPC-Server", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_member_still_means_absent()
    {
        var item = new DavidComItem(@"user\1\in", new RawMail());

        Assert.Null(item.SourceFile);
        Assert.Null(item.FaxImagePath);
    }

    [Fact]
    public void Body_read_error_fails_instead_of_blanking_the_message()
    {
        var raw = new RawMail { BodyFailure = RpcFailure() };

        Assert.Throws<COMException>(() => new DavidComItem(@"user\1\in", raw).Properties);
    }

    [Fact]
    public void Vcard_e_fail_on_mail_is_tolerated_and_lists_are_resolved()
    {
        var properties = new DavidComItem(@"user\1\in", new RawMail()).Properties;

        Assert.False(properties.ContainsKey("VCard"));
        Assert.Equal("Inhalt", properties["BodyText"]);
        Assert.Equal("a@example.com; b@example.com", properties["CC"]);
        Assert.Equal("c@example.com", properties["Recipients"]);
    }

    private (DavidComItem Item, DavidAttachmentInfo Info, string Directory) Attachment(RawAttachment raw)
    {
        var directory = Path.Combine(_directory, "work");
        Directory.CreateDirectory(directory);
        var item = new DavidComItem(@"user\1\in", new RawMail { AttachmentList = new object[] { raw } });
        return (item, Assert.Single(item.Attachments), directory);
    }

    [Fact]
    public void Attachment_copy_succeeds_normally()
    {
        var raw = new RawAttachment();
        raw.Copy = destination => File.WriteAllBytes(Path.Combine(destination, "datei.pdf"), new byte[1000]);
        var (item, info, directory) = Attachment(raw);

        Assert.True(item.TryCopyAttachment(info, directory, out var copied, out var error), error);
        Assert.Equal(1000, new FileInfo(copied!).Length);
    }

    [Fact]
    public void Partial_attachment_after_a_failed_copy_is_not_accepted()
    {
        var raw = new RawAttachment();
        raw.Copy = destination =>
        {
            if (Directory.Exists(destination))
            {
                File.WriteAllBytes(Path.Combine(destination, "datei.pdf"), new byte[10]);
            }

            throw new IOException("Netzwerkfehler");
        };
        var (item, info, directory) = Attachment(raw);

        Assert.False(item.TryCopyAttachment(info, directory, out _, out var error));
        Assert.Contains("Netzwerkfehler", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "datei.pdf")));
    }

    [Fact]
    public void Partial_attachment_falls_back_to_the_complete_source_file()
    {
        var source = Path.Combine(_directory, "original.pdf");
        File.WriteAllBytes(source, new byte[1000]);
        var raw = new RawAttachment { _Path = source };
        raw.Copy = destination =>
        {
            if (Directory.Exists(destination))
            {
                File.WriteAllBytes(Path.Combine(destination, "datei.pdf"), new byte[10]);
            }

            throw new IOException("Netzwerkfehler");
        };
        var (item, info, directory) = Attachment(raw);

        Assert.True(item.TryCopyAttachment(info, directory, out var copied, out _));
        Assert.Equal(1000, new FileInfo(copied!).Length);
    }

    [Fact]
    public void Leftover_file_from_an_earlier_attempt_is_not_accepted()
    {
        var raw = new RawAttachment { Copy = _ => throw new IOException("Netzwerkfehler") };
        var (item, info, directory) = Attachment(raw);
        File.WriteAllBytes(Path.Combine(directory, "datei.pdf"), new byte[1000]);

        Assert.False(item.TryCopyAttachment(info, directory, out _, out _));
    }

    [Fact]
    public void Archive_key_read_error_does_not_misfile_the_folder()
    {
        Assert.Throws<COMException>(() => new DavidComArchive(@"user\1\in", new RawArchiveWithBrokenName()));
    }

    [Fact]
    public void Required_enumeration_reports_a_short_collection()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ComHelpers.EnumerateRequired(new ShortCollection()).ToList());

        Assert.Contains("1 von 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_enumeration_reports_an_empty_entry()
    {
        Assert.Throws<InvalidOperationException>(() => ComHelpers.EnumerateRequired(new GapCollection()).ToList());
    }

    [Fact]
    public void Required_enumeration_shows_the_real_error_of_new_enum()
    {
        Assert.Throws<COMException>(() => ComHelpers.EnumerateRequired(new NotEnumerable()).ToList());
    }

    [Fact]
    public void Lenient_enumeration_stops_at_an_empty_entry_instead_of_spinning()
    {
        Assert.Single(ComHelpers.Enumerate(new GapCollection()));
    }
}
