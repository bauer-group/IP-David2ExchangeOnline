// SPDX-License-Identifier: MIT
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using David2Exchange.Manifest;
using David2Exchange.Output;
using David2Exchange.Runner;
using David2Exchange.Source;
using Xunit;

namespace David2Exchange.Tests.Runner;

/// <summary>Verhalten, das erst Messungen am echten David gezeigt haben.</summary>
public sealed class RealDavidShapeTests : IDisposable
{
    private const string UserRoot = @"user\1";
    private const string TargetUpn = "ziel@example.com";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "d2e-shape-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDavid _david = new();
    private readonly RecordingOutput _output = new();
    private readonly FakeArchive _in;
    private readonly FakeArchive _out;

    public RealDavidShapeTests()
    {
        Directory.CreateDirectory(_directory);
        var root = _david.AddRoot(UserRoot);
        _in = root.Sub("in", "Eingang");
        _out = root.Sub("out", "Ausgang");
        _output.Name = "local";
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private MigrationConfig Config(string id = "box", string wave = "default")
    {
        return new MigrationConfig
        {
            ConfigPath = Path.Combine(_directory, "migration.yaml"),
            Staging = new StagingConfig
            {
                Root = Path.Combine(_directory, "staging"),
                Manifest = Path.Combine(_directory, "manifest.db")
            },
            Limits = new LimitsConfig { Limit = 0, Retries = 0, Workers = 2, BatchSize = 20 },
            Mailboxes =
            {
                new MailboxMapping
                {
                    Id = id,
                    Wave = wave,
                    Source = new SourceConfig { ArchivePath = UserRoot, Email = TargetUpn },
                    Target = new TargetConfig { Upn = TargetUpn },
                    Include = new List<string> { "all" }
                }
            }
        };
    }

    private int Run(MigrationConfig? config = null, int limit = -1)
    {
        return StaTaskRunner.Run(() => MigrationRunner.RunCoreAsync(
            config ?? Config(),
            new RunOptions { Limit = limit },
            _david,
            _ => _output,
            TimeSpan.Zero,
            CancellationToken.None));
    }

    private int Verify(MigrationConfig? config = null)
    {
        return MigrationRunner.VerifyCore(config ?? Config(), new RunOptions { OutputKind = "local" });
    }

    private IReadOnlyList<ItemDocument> Rows()
    {
        using var manifest = new LiteDbManifest(Config().Staging.Manifest);
        return manifest.GetAll();
    }

    [Fact]
    public void Task_attachments_are_staged_and_reach_the_output()
    {
        _in.Add("12", @"\\srv\a\T1.001", "Aufgabe", item =>
        {
            item.Set("AttachmentCount", "1");
            item.AttachmentList.Add(new DavidAttachmentInfo("plan.pdf", "application/pdf", null, 2));
        });

        Assert.Equal(0, Run());

        var delivered = Assert.Single(_output.Items);
        var part = Assert.Single(delivered.Message!.Attachments);
        Assert.Equal("plan.pdf", part.ContentDisposition!.FileName);
        Assert.Equal(ItemState.Imported, Assert.Single(Rows()).State);
    }

    [Fact]
    public void Task_with_an_unexportable_attachment_fails_and_is_not_reported_as_imported()
    {
        _in.Add("12", @"\\srv\a\T1.001", "Aufgabe", item =>
        {
            item.Set("AttachmentCount", "1");
            item.AttachmentList.Add(new DavidAttachmentInfo("kaputt.pdf", "application/pdf", null, 2));
            item.FailAttachmentCopy = true;
        });

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        Assert.Equal(ItemState.Failed, Assert.Single(Rows()).State);
    }

    [Fact]
    public void Vanished_rows_do_not_block_verify_and_return_when_the_item_returns()
    {
        _in.AddMails("A", 3);
        _in.Add("3", @"\\srv\a\F1.001", "Fax", item => item.FaxImagePath = @"\\srv\fehlt\F1.001");

        Assert.Equal(1, Run());
        Assert.Equal(1, Verify());

        // Das gepackte Fax wird gelöscht: Sonst bliebe das Postfach für immer rot.
        _in.RemoveAt(3);
        Assert.Equal(0, Run());
        Assert.Equal(0, Verify());
        var closed = Assert.Single(Rows(), row => row.State == ItemState.NotInSource);
        Assert.Contains("Nicht mehr in der Quelle", closed.LastError, StringComparison.Ordinal);

        _in.Add("3", @"\\srv\a\F1.001", "Fax", item => item.FaxImagePath = @"\\srv\fehlt\F1.001");
        Assert.Equal(1, Run());
        Assert.DoesNotContain(Rows(), row => row.State == ItemState.NotInSource);
    }

    [Fact]
    public void Limited_run_does_not_close_rows_it_did_not_reach()
    {
        _in.AddMails("A", 3);
        Assert.Equal(0, Run(limit: 1));
        var open = Rows().Count;
        _in.AddMails("B", 1);

        Assert.Equal(0, Run(limit: 1));

        Assert.DoesNotContain(Rows(), row => row.State == ItemState.NotInSource);
        Assert.True(Rows().Count >= open);
    }

    [Fact]
    public void Moved_mail_keeps_one_copy_and_is_listed_as_skipped()
    {
        _in.Add("2", @"\\srv\a\M1.001", "Rechnung");
        Assert.Equal(0, Run());

        _in.RemoveAt(0);
        _out.Add("2", @"\\srv\a\M1.001", "Rechnung");
        Assert.Equal(0, Run());

        Assert.Single(_output.Items);
        Assert.Contains(Rows(), row => row.State == ItemState.Skipped && row.LastError!.Contains("verschoben", StringComparison.Ordinal));
        Assert.Equal(0, Verify());
    }

    [Fact]
    public void Series_mail_from_an_older_run_reaches_the_missing_recipients_without_resending()
    {
        // Stand vor der Empfängerkennung: Ein Eintrag ist übertragen, die anderen gelten als Wiederholung.
        var addresses = new[] { "a@example.com", "b@example.com", "c@example.com" };
        var entries = addresses.Select(_ => _out.Add("2", "S", "Serie")).ToList();
        Assert.Equal(0, Run());
        Assert.Single(_output.Items);
        Assert.Equal(2, Rows().Count(row => row.Key.Contains("#dup", StringComparison.Ordinal)));
        var imported = Assert.Single(Rows(), row => row.State == ItemState.Imported);
        using (var manifest = new LiteDbManifest(Config().Staging.Manifest))
        {
            manifest.Database.DropCollection("fileOwners");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Set("To", addresses[i]);
        }

        Assert.Equal(0, Run());
        Assert.Equal(3, _output.Items.Count);

        // Delta-Lauf: Alle Kennungen sind stabil, der zuerst übertragene Eintrag behält seine.
        Assert.Equal(0, Run());
        Assert.Equal(3, _output.Items.Count);
        Assert.Contains(Rows(), row => row.Key == imported.Key && row.State == ItemState.Imported);
        Assert.DoesNotContain(Rows(), row => row.State == ItemState.Skipped);
        Assert.Equal(0, Verify());
    }

    [Fact]
    public void Series_entries_keep_their_identity_when_new_entries_come_first_and_true_repeats_stay_single()
    {
        _out.Add("2", "S", "Serie", item => item.Set("To", "a@example.com"));
        _out.Add("2", "S", "Serie", item => item.Set("To", "b@example.com"));
        Assert.Equal(0, Run());
        Assert.Equal(2, _output.Items.Count);

        // David liefert neueste zuerst; die neuen Einträge stehen also vor den übertragenen.
        _out.Add("2", "S", "Serie", item => item.Set("To", "c@example.com"));
        _out.Add("2", "S", "Serie", item => item.Set("To", "a@example.com"));
        Assert.Equal(0, Run());

        Assert.Equal(3, _output.Items.Count);
        var skipped = Assert.Single(Rows(), row => row.State == ItemState.Skipped);
        Assert.Contains("mehrfach", skipped.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Moved_series_entries_are_not_sent_again()
    {
        _in.Add("2", "S", "Serie", item => item.Set("To", "a@example.com"));
        _in.Add("2", "S", "Serie", item => item.Set("To", "b@example.com"));
        Assert.Equal(0, Run());
        Assert.Equal(2, _output.Items.Count);

        _in.RemoveAt(0);
        _in.RemoveAt(0);
        _out.Add("2", "S", "Serie", item => item.Set("To", "a@example.com"));
        _out.Add("2", "S", "Serie", item => item.Set("To", "b@example.com"));
        Assert.Equal(0, Run());

        Assert.Equal(2, _output.Items.Count);
    }

    [Fact]
    public void Same_file_number_with_other_content_in_another_archive_is_still_sent()
    {
        // Gemessen: 13 Dateinummern kamen in mehreren Archiven vor, alle mit anderem Inhalt.
        _in.Add("2", @"\\srv\a\M1.001", "Rechnung");
        _out.Add("2", @"\\srv\a\M1.001", "Etwas anderes");

        Assert.Equal(0, Run());

        Assert.Equal(2, _output.Items.Count);
    }

    [Fact]
    public void Renamed_mailbox_id_does_not_send_the_mailbox_again()
    {
        _in.AddMails("A", 3);
        Assert.Equal(0, Run(Config("alt")));

        Assert.Equal(0, Run(Config("neu")));

        Assert.Equal(3, _output.Items.Count);
        Assert.All(Rows(), row => Assert.Equal("neu", row.MailboxId));
    }

    [Fact]
    public void Two_active_mailboxes_cannot_share_one_source()
    {
        var config = Config();
        config.Mailboxes.Add(new MailboxMapping
        {
            Id = "zwei",
            Source = new SourceConfig { ArchivePath = UserRoot.ToUpperInvariant(), Email = TargetUpn },
            Target = new TargetConfig { Upn = TargetUpn }
        });

        Assert.Throws<David2Exchange.Cli.CliException>(() => config.Validate(requireM365: false));
    }

    [Fact]
    public void Failure_in_another_wave_does_not_turn_this_run_red()
    {
        var config = Config(wave: "w2");
        _in.Add("3", @"\\srv\a\F1.001", "Fax", item => item.FaxImagePath = @"\\srv\fehlt\F1.001");
        Assert.Equal(1, Run(config));

        // Die erste Zuordnung ruht, die zweite ist sauber.
        var root = _david.AddRoot(@"user\2");
        root.Sub("in", "Eingang").AddMails("B", 2);
        config.Mailboxes[0].Enabled = false;
        config.Mailboxes.Add(new MailboxMapping
        {
            Id = "sauber",
            Wave = "w1",
            Source = new SourceConfig { ArchivePath = @"user\2", Email = TargetUpn },
            Target = new TargetConfig { Upn = TargetUpn },
            Include = new List<string> { "all" }
        });

        Assert.Equal(0, Run(config));
    }

    [Fact]
    public void Known_failures_do_not_use_up_the_limit_of_a_pilot()
    {
        _in.AddMails("A", 3);
        for (var position = 0; position < 2; position++)
        {
            _in.Add("3", $@"\\srv\a\F{position}.001", "Fax", item => item.FaxImagePath = @"\\srv\fehlt\x.001");
        }

        // Die Faxe liegen vorn oder hinten: Nach genug Läufen sind alle Mails übertragen.
        for (var pass = 0; pass < 4; pass++)
        {
            Run(limit: 2);
        }

        Assert.Equal(3, _output.Items.Count);
    }

    [Fact]
    public void Empty_source_with_a_complete_run_passes_verify()
    {
        Assert.Equal(1, Verify());
        Assert.Equal(0, Run());
        Assert.Equal(0, Verify());
    }

    [Fact]
    public void Outgoing_sms_keeps_its_number_from_recipients()
    {
        // Gemessen: To ist bei ausgehenden SMS leer, die Nummer steht in Recipients.
        _out.Add("4", @"\\srv\a\S1.001", "", item =>
        {
            item.Set("BodyText", "Hallo");
            item.Set("Received", "False");
            item.Set("Recipients", "0170(123)456");
        });

        Assert.Equal(0, Run());

        var message = Assert.Single(_output.Items).Message!;
        Assert.Contains("0170(123)456", message.To.ToString(), StringComparison.Ordinal);
        Assert.Contains("0170(123)456", message.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Fax_with_a_recipient_in_an_own_folder_counts_as_sent()
    {
        var folder = _in.Sub("9", "Kunde Meier");
        folder.Add("3", @"\\srv\a\F1.001", "", item =>
        {
            item.FaxImage = new byte[] { 1, 2, 3 };
            item.Set("To", "+49 9971 77");
            item.Set("Received", null);
        });

        Run();

        var message = Assert.Single(_output.Items).Message!;
        Assert.Contains("+49 9971 77", message.To.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Packed_voice_message_fails_with_a_clear_message()
    {
        // Gemessen: Alle Voice-Dateien lagen in gepackten Archiven, das Item nennt nur den Pfad.
        _in.Add("5", @"\\srv\a\V1.001", "Anruf", item => item.FaxImagePath = @"\\srv\fehlt\V1.001");

        Assert.Equal(1, Run());

        var row = Assert.Single(Rows());
        Assert.Equal(ItemState.Failed, row.State);
        Assert.Contains("unpack", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_keeps_equal_names_and_exact_bytes()
    {
        var first = Path.Combine(_directory, "a.bin");
        var second = Path.Combine(_directory, "b.bin");
        File.WriteAllBytes(first, new byte[] { 0, 1, 2, 255 });
        File.WriteAllBytes(second, new byte[] { 9 });
        var bundle = Path.Combine(_directory, "bundle.eml");

        AttachmentBundle.Write(bundle, new[]
        {
            new SyntheticAttachment("mail.eml", first),
            new SyntheticAttachment("mail.eml", second)
        });

        var read = AttachmentBundle.Read(bundle);
        Assert.Equal(2, read.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, read[0].Content);
        Assert.Equal("mail.eml", read[1].Name);
        Assert.Equal(new byte[] { 9 }, read[1].Content);
    }

    [Fact]
    public void Orphaned_manifest_log_is_refused()
    {
        var path = Path.Combine(_directory, "m.db");
        File.WriteAllText(Path.Combine(_directory, "m-log.db"), "alt");

        Assert.Throws<David2Exchange.Cli.CliException>(() => new LiteDbManifest(path).Dispose());
    }
}
