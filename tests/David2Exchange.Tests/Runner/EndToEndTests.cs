// SPDX-License-Identifier: MIT
using System.Text;
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using David2Exchange.Manifest;
using David2Exchange.Output;
using David2Exchange.Runner;
using David2Exchange.Source;
using David2Exchange.Tests.Output;
using MimeKit;

namespace David2Exchange.Tests.Runner;

/// <summary>
/// Die ganze Kette: Fake-David auf dem STA, Queue, echte EWS Managed API, Fake-Server.
/// Prüft das Zusammenspiel, das die Einzeltests nicht sehen.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-e2e-" + Guid.NewGuid().ToString("N"));

    private readonly FakeEwsServer _server = new();
    private readonly FakeDavid _david = new();

    public EndToEndTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private MigrationConfig Config(params string[] mailboxes)
    {
        var config = new MigrationConfig
        {
            ConfigPath = Path.Combine(_directory, "migration.yaml"),
            Staging = new StagingConfig
            {
                Root = Path.Combine(_directory, "staging"),
                Manifest = Path.Combine(_directory, "manifest.db")
            },
            Limits = new LimitsConfig { Limit = 0, Retries = 1, Workers = 8, BatchSize = 20 }
        };

        foreach (var mailbox in mailboxes)
        {
            config.Mailboxes.Add(new MailboxMapping
            {
                Id = mailbox,
                Source = new SourceConfig { ArchivePath = $@"user\{mailbox}", Email = $"{mailbox}@example.com" },
                Target = new TargetConfig { Upn = $"{mailbox}@example.com" },
                Include = new List<string> { "all" }
            });
        }

        return config;
    }

    private int Run(MigrationConfig config)
    {
        return StaTaskRunner.Run(() => MigrationRunner.RunCoreAsync(
            config,
            new RunOptions { Limit = 0 },
            _david,
            mailbox => new EwsOutputTarget(
                new EwsOutputOptions(mailbox.Target.Upn, _server.Url, "", "", "", "", "Europe/Berlin", 8),
                (_, _) => Task.FromResult("a.token.c"),
                (_, _) => Task.CompletedTask),
            TimeSpan.Zero,
            CancellationToken.None));
    }

    private ManifestSummary Summary(MigrationConfig config)
    {
        using var manifest = new LiteDbManifest(config.Staging.Manifest);
        return manifest.GetSummary();
    }

    private static string FolderOf(StoredItem item) => item.FolderId;

    private static string SubjectOf(StoredItem item) =>
        MimeMessage.Load(new MemoryStream(item.Mime!)).Subject!;

    [Fact]
    public void Mailbox_with_overlapping_ids_arrives_complete_and_correctly_filed()
    {
        var root = _david.AddRoot(@"user\anna");
        root.Sub("in", "Eingang").AddMails("IN", 130);
        root.Sub("out", "Ausgang").AddMails("OUT", 90);
        root.Sub("todo", "Aufgaben").Add("12", "T", "Aufgabe", item => item.Set("_ID", "0"));
        root.Sub("cal", "Kalender").Add("7", "C", "Termin", item => item.Set("_ID", "0"));
        var config = Config("anna");

        Assert.Equal(0, Run(config));

        var mime = _server.Items.Where(item => item.Mime is not null).ToList();
        Assert.Equal(220, mime.Count);
        Assert.All(mime, item =>
        {
            var expected = SubjectOf(item).StartsWith("IN-", StringComparison.Ordinal) ? "root-inbox" : "root-sentitems";
            Assert.Equal(expected, FolderOf(item));
        });
        Assert.Equal(220, mime.Select(SubjectOf).Distinct().Count());
        Assert.Equal(222, Summary(config).Imported);
    }

    [Fact]
    public void Rerun_after_everything_arrived_sends_nothing_again()
    {
        var root = _david.AddRoot(@"user\anna");
        root.Sub("in", "Eingang").AddMails("IN", 45);
        var config = Config("anna");
        Assert.Equal(0, Run(config));
        var before = _server.Items.Count;

        Assert.Equal(0, Run(config));

        Assert.Equal(before, _server.Items.Count);
    }

    [Fact]
    public void Crash_between_saving_and_recording_is_reconciled_without_duplicates()
    {
        var root = _david.AddRoot(@"user\anna");
        root.Sub("in", "Eingang").AddMails("IN", 25);
        var config = Config("anna");

        // Erster Lauf: Der Server speichert jeden Batch, die Antwort geht verloren, und
        // die folgende Suche scheitert ebenfalls, als wäre die Verbindung weg.
        _server.Intercept = request => request.Operation switch
        {
            "CreateItem" => new FakeReply(DropConnection: true, StoreFirst: true),
            "FindItem" => new FakeReply(DropConnection: true),
            _ => null
        };
        Assert.Equal(1, Run(config));
        var stored = _server.Items.Count;
        Assert.True(stored > 0);

        _server.Intercept = null;
        Assert.Equal(0, Run(config));

        Assert.Equal(25, Summary(config).Imported);
        Assert.Equal(25, _server.Items.Select(item => item.ItemKey).Distinct().Count());
        Assert.Equal(_server.Items.Count, _server.Items.Select(item => item.ItemKey).Distinct().Count());
    }

    [Fact]
    public void Poison_item_only_fails_itself_and_all_others_count_as_imported()
    {
        var root = _david.AddRoot(@"user\anna");
        var inbox = root.Sub("in", "Eingang");
        inbox.AddMails("IN", 10);
        inbox.Add("2", "GIFT", "gift", item => item.NativeExport = path =>
        {
            File.WriteAllText(path, "Subject: gift\r\n\r\nx");
            return true;
        });
        var config = Config("anna");
        var poison = Convert.ToBase64String(Encoding.ASCII.GetBytes("Subject: gift\r\n\r\nx"));
        _server.Intercept = request =>
            request.Operation == "CreateItem" && request.Element.ToString().Contains(poison, StringComparison.Ordinal)
                ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(1, "ErrorInvalidRequest")))
                : null;

        Assert.Equal(1, Run(config));

        var summary = Summary(config);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(10, summary.Imported);
    }

    [Fact]
    public void David_stays_on_its_sta_thread_across_several_mailboxes()
    {
        foreach (var name in new[] { "anna", "bert", "cora" })
        {
            var root = _david.AddRoot($@"user\{name}");
            root.Sub("in", "Eingang").AddMails(name.ToUpperInvariant(), 30);
        }

        var config = Config("anna", "bert", "cora");

        Assert.Equal(0, Run(config));

        var thread = Assert.Single(_david.Threads);
        Assert.Equal(ApartmentState.STA, thread.Value);
        Assert.Equal(90, _server.Items.Count);
    }

    [Fact]
    public void Tenant_block_stops_the_run_with_a_clear_exit_code()
    {
        var root = _david.AddRoot(@"user\anna");
        root.Sub("in", "Eingang").AddMails("IN", 3);
        _david.AddRoot(@"user\bert").Sub("in", "Eingang").AddMails("BERT", 3);
        var config = Config("anna", "bert");
        _server.Intercept = request => request.Operation == "GetFolder"
            ? new FakeReply(StatusCode: 403, Body: "")
            : null;

        Assert.Equal(2, Run(config));

        Assert.Empty(_server.Items);
        Assert.Equal(0, Summary(config).Total);
    }

    [Fact]
    public void Full_target_mailbox_does_not_stop_the_next_mailbox()
    {
        _david.AddRoot(@"user\anna").Sub("in", "Eingang").AddMails("VOLL", 3);
        _david.AddRoot(@"user\bert").Sub("in", "Eingang").AddMails("OK", 3);
        var config = Config("anna", "bert");
        _server.Intercept = request =>
            request.Operation == "CreateItem" &&
            request.Element.ToString().Contains(Convert.ToBase64String(Encoding.ASCII.GetBytes("Message-ID: <VOLL"))[..20], StringComparison.Ordinal)
                ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(3, "ErrorQuotaExceeded")))
                : null;

        Assert.Equal(1, Run(config));

        using var manifest = new LiteDbManifest(config.Staging.Manifest);
        Assert.Equal(3, manifest.GetSummary("bert").Imported);
        Assert.Equal(0, manifest.GetSummary("anna").Imported);
    }

    [Fact]
    public void Fax_sms_and_voice_travel_the_whole_way_with_correct_direction()
    {
        var root = _david.AddRoot(@"user\anna");
        var inbox = root.Sub("in", "Eingang");
        var sent = root.Sub("out", "Ausgang");
        inbox.Add("4", "S1", "", item =>
        {
            item.Subject = null;
            item.Set("From", "+49 171 111");
            item.Set("BodyText", "Rückruf");
        });
        inbox.Add("5", "V1", "Sprachnachricht", item =>
        {
            item.Set("From", "+49 171 222");
            item.AttachmentList.Add(new DavidAttachmentInfo("nachricht.wav", "audio/wav", null, 2));
        });
        sent.Add("3", "F1", "", item =>
        {
            item.Subject = null;
            item.Set("To", "+49 9971 333");
            item.AttachmentList.Add(new DavidAttachmentInfo("fax.tif", "image/tiff", null, 2));
        });
        var config = Config("anna");

        Assert.Equal(0, Run(config));

        var messages = _server.Items
            .Select(item => (item.FolderId, Message: MimeMessage.Load(new MemoryStream(item.Mime!))))
            .ToList();
        var sms = messages.Single(m => m.Message.Subject == "[SMS] +49 171 111");
        Assert.Equal("root-inbox", sms.FolderId);
        Assert.Equal("Rückruf", sms.Message.TextBody!.Trim());
        var voice = messages.Single(m => m.Message.Subject == "Sprachnachricht");
        Assert.Equal("root-inbox", voice.FolderId);
        Assert.Single(voice.Message.Attachments);
        var fax = messages.Single(m => m.Message.Subject == "[FAX] +49 9971 333");
        Assert.Equal("root-sentitems", fax.FolderId);
        Assert.Equal("anna@example.com", ((MailboxAddress)fax.Message.From[0]).Address);
    }

    [Fact]
    public void Illegal_character_in_a_mail_key_is_not_retried_and_blocks_nothing()
    {
        var inbox = _david.AddRoot(@"user\anna").Sub("in", "Eingang");
        inbox.AddMails("IN", 3);
        inbox.Add("2", "X", "x", item => item.Set("TextSource", "\\\\srv\\david\\ITEM\v.001"));
        var config = Config("anna");
        config.Limits.Retries = 3;

        Assert.Equal(1, Run(config));

        using var manifest = new LiteDbManifest(config.Staging.Manifest);
        var failed = Assert.Single(manifest.GetByState(ItemState.Failed));
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("Item-Kennung", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(3, manifest.GetSummary().Imported);
    }

    [Fact]
    public void Lone_surrogate_in_david_text_does_not_stop_the_item()
    {
        var high = (char)0xD83D;
        var low = (char)0xDE00;
        static bool NativeEml(string path)
        {
            File.WriteAllText(path, "Subject: nativ\r\n\r\nInhalt");
            return true;
        }

        var root = _david.AddRoot(@"user\anna");
        root.Sub("cal", "Kalender").Add("7", "C", "Termin", item => item.Set("BodyText", $"Text {high} mehr"));
        root.Sub("todo", "Aufgaben").Add("12", "T", $"Auf{low}gabe");
        var inbox = root.Sub("in", "Eingang");
        inbox.Add("2", "M", $"Betreff {high}", item => item.NativeExport = NativeEml);
        inbox.Add("2", "G", $"Ersatz {high}", item =>
        {
            item.NativeExport = _ => false;
            item.Set("BodyText", $"Inhalt {high} mehr");
        });
        inbox.Add("4", "S", $"Kurz {low}", item => item.Set("BodyText", $"Inhalt {low} mehr"));
        root.Sub("address", "Adressen").Add("1", "K", "Kontakt", item =>
        {
            // Ohne Datei hängt die Kennung am Inhalt und muss trotz Ersatzzeichen stabil bleiben.
            item.Set("TextSource", null);
            item.Set("DisplayName", $"Anna {high}");
        });
        root.Sub("p", $"Pro{high}jekt").Add("2", "F", "ok", item => item.NativeExport = NativeEml);
        root.Sub($"Ar{high}chiv", "Archiv").Add("2", "A", "ok", item => item.NativeExport = NativeEml);
        var config = Config("anna");

        Assert.Equal(0, Run(config));

        Assert.Equal(8, Summary(config).Imported);
        Assert.Equal(8, _server.Items.Count);
        Assert.Equal(
            "Text � mehr",
            _server.Items.Single(item => item.ElementName == "CalendarItem").Request!.Element(FakeEwsServer.T + "Body")!.Value);
        Assert.Equal(
            "Auf�gabe",
            _server.Items.Single(item => item.ElementName == "Task").Request!.Element(FakeEwsServer.T + "Subject")!.Value);
        Assert.Contains(_server.Items, item => item.FolderId == "root-inbox/Pro�jekt");

        // Die Kennungen ändern sich nicht zwischen zwei Läufen, also geht nichts erneut hinaus.
        Assert.Equal(0, Run(config));

        Assert.Equal(8, _server.Items.Count);
    }

    [Fact]
    public void Lone_surrogate_in_an_error_text_still_records_the_failure()
    {
        _david.AddRoot(@"user\anna").Sub("in", "Eingang")
            .Add("3", "F", "Fax", item => item.FaxImagePath = $"\\\\srv\\fax\\F{(char)0xD83D}.tif");
        var config = Config("anna");

        Assert.Equal(1, Run(config));

        using var manifest = new LiteDbManifest(config.Staging.Manifest);
        var failed = Assert.Single(manifest.GetByState(ItemState.Failed));
        Assert.Contains("F�.tif", failed.LastError, StringComparison.Ordinal);
        Assert.True(manifest.HasCompleteWalk("anna", @"user\anna"));
    }

    [Fact]
    public void Keys_that_differ_only_in_a_lone_surrogate_do_not_share_a_row()
    {
        var inbox = _david.AddRoot(@"user\anna").Sub("in", "Eingang");
        inbox.Add("2", "A", "a", item => item.Set("TextSource", $"\\\\srv\\david\\ITEM{(char)0xD83D}.001"));
        inbox.Add("2", "B", "b", item => item.Set("TextSource", $"\\\\srv\\david\\ITEM{(char)0xD83E}.001"));
        var config = Config("anna");

        Assert.Equal(0, Run(config));

        // Sonst überschriebe die zweite Zeile die erste, ohne dass es jemand sieht.
        var summary = Summary(config);
        Assert.Equal(1, summary.Imported);
        Assert.Equal(1, summary.Skipped);
    }
}
