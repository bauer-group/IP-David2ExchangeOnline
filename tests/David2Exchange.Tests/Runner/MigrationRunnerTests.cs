// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text;
using David2Exchange.Cli;
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using David2Exchange.Manifest;
using David2Exchange.Output;
using David2Exchange.Runner;
using David2Exchange.Source;
using MimeKit;

namespace David2Exchange.Tests.Runner;

public sealed class MigrationRunnerTests : IDisposable
{
    private const string UserRoot = @"user\10018000";
    private const string TargetUpn = "ziel@example.com";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-run-" + Guid.NewGuid().ToString("N"));

    private readonly FakeDavid _david = new();
    private readonly RecordingOutput _output = new();
    private readonly FakeArchive _root;
    private readonly FakeArchive _in;
    private readonly FakeArchive _out;

    public MigrationRunnerTests()
    {
        Directory.CreateDirectory(_directory);
        _root = _david.AddRoot(UserRoot);
        _in = _root.Sub("in", "Eingang");
        _out = _root.Sub("out", "Ausgang");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private MigrationConfig Config(Action<LimitsConfig>? limits = null, List<string>? include = null)
    {
        var config = new MigrationConfig
        {
            ConfigPath = Path.Combine(_directory, "migration.yaml"),
            Staging = new StagingConfig
            {
                Root = Path.Combine(_directory, "staging"),
                Manifest = Path.Combine(_directory, "manifest.db")
            },
            Limits = new LimitsConfig { Limit = 0, Retries = 0, Workers = 8, BatchSize = 20 },
            Mailboxes =
            {
                new MailboxMapping
                {
                    Id = "box",
                    Source = new SourceConfig { ArchivePath = UserRoot, Email = TargetUpn },
                    Target = new TargetConfig { Upn = TargetUpn },
                    Include = include ?? new List<string> { "all" }
                }
            }
        };
        limits?.Invoke(config.Limits);
        return config;
    }

    private int Run(MigrationConfig? config = null, int limit = -1)
    {
        var effective = config ?? Config();
        return StaTaskRunner.Run(() => MigrationRunner.RunCoreAsync(
            effective,
            new RunOptions { Limit = limit },
            _david,
            _ => _output,
            TimeSpan.Zero,
            CancellationToken.None));
    }

    private ManifestSummary Summary(MigrationConfig? config = null)
    {
        using var manifest = new LiteDbManifest((config ?? Config()).Staging.Manifest);
        return manifest.GetSummary("box");
    }

    private IReadOnlyList<ItemDocument> Documents()
    {
        using var manifest = new LiteDbManifest(Config().Staging.Manifest);
        return manifest.GetAll("box");
    }

    [Fact]
    public void Same_david_ids_in_inbox_and_sent_keep_their_own_content()
    {
        // Eingang und Ausgang nummerieren je ab 0: das ist die Ursache der Fehlzuordnung.
        _in.AddMails("IN", 30);
        _out.AddMails("OUT", 30);

        Assert.Equal(0, Run());

        Assert.Equal(60, _output.Items.Count);
        foreach (var delivered in _output.Items)
        {
            var isInbox = delivered.Item.ArchivePath.EndsWith(@"\in", StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(isInbox ? "IN-" : "OUT-", delivered.EmlSubject, StringComparison.Ordinal);
            Assert.Equal(isInbox ? "in" : "out", delivered.Item.FolderPath);
        }
    }

    [Fact]
    public void David_is_used_from_exactly_one_sta_thread_and_workers_never_touch_it()
    {
        _in.AddMails("IN", 100);
        _out.AddMails("OUT", 100);
        _output.SendDelay = TimeSpan.FromMilliseconds(5);

        Assert.Equal(0, Run());

        var david = Assert.Single(_david.Threads);
        Assert.Equal(ApartmentState.STA, david.Value);
        Assert.DoesNotContain(_output.Threads.Keys, id => id == david.Key);
    }

    [Fact]
    public void Queue_uses_eight_workers_and_twenty_item_batches_without_mixing_folders()
    {
        _in.AddMails("IN", 170);
        _out.AddMails("OUT", 170);
        _output.HoldUntilConcurrency = 8;

        Assert.Equal(0, Run());

        Assert.Equal(8, _output.MaxConcurrency);
        Assert.All(_output.Batches, batch => Assert.InRange(batch.Count, 1, 20));
        Assert.All(_output.Batches, batch => Assert.Single(batch.Select(item => item.ArchivePath).Distinct()));
        Assert.Equal(20, _output.Batches.Max(batch => batch.Count));
        Assert.Equal(340, _output.Items.Count);
    }

    [Fact]
    public void Batch_byte_limit_splits_large_items()
    {
        var large = new string('x', 700 * 1024);
        for (var i = 0; i < 6; i++)
        {
            var number = i;
            _in.Add("2", $"L{number}", $"gross-{number}", item => item.NativeExport = path =>
            {
                File.WriteAllText(path, $"Subject: gross-{number}\r\n\r\n{large}");
                return true;
            });
        }

        var config = Config(limits => limits.MaxBatchMegabytes = 1);

        Assert.Equal(0, Run(config));

        Assert.All(_output.Batches, batch => Assert.Single(batch));
    }

    [Fact]
    public void Task_with_david_id_property_no_longer_breaks_the_run()
    {
        // Genau diese Stelle endete im echten Lauf mit dem FATAL zum Write-Lock.
        _in.AddMails("IN", 3);
        _root.Sub("todo", "Aufgaben").Add("12", "T1", "Aufgabe", item =>
        {
            item.Set("_ID", "0");
            item.Set("BodyText", "Text");
        });
        _root.Sub("cal", "Kalender").Add("7", "C1", "Termin", item => item.Set("_ID", "0"));

        Assert.Equal(0, Run());

        var summary = Summary();
        Assert.Equal(5, summary.Imported);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public void Imported_items_are_not_sent_again_on_the_next_run()
    {
        _in.AddMails("IN", 25);
        Assert.Equal(0, Run());
        var first = _output.Items.Count;

        Assert.Equal(0, Run());

        Assert.Equal(25, first);
        Assert.Equal(25, _output.Items.Count);
        Assert.Equal(25, Summary().Imported);
    }

    [Fact]
    public void Shifted_david_ids_neither_lose_new_items_nor_resend_old_ones()
    {
        _in.AddMails("IN", 5);
        Assert.Equal(0, Run());

        // Die älteste Mail wird gelöscht und eine neue kommt hinzu: Die neue Mail
        // trägt nun eine ID, die vorher einer bereits importierten Mail gehörte.
        _in.RemoveAt(0);
        _in.Add("2", "NEU1", "IN-neu");

        Assert.Equal(0, Run());

        Assert.Equal(6, _output.Items.Count);
        Assert.Single(_output.Items, item => item.EmlSubject == "IN-neu");
    }

    [Fact]
    public void First_attempt_skips_the_target_lookup_but_any_retry_requires_it()
    {
        _in.AddMails("IN", 3);
        _output.Behavior = item => new OutputResult(item, false, "Timeout");
        Assert.Equal(1, Run());
        Assert.All(_output.Batches, batch => Assert.All(batch, item => Assert.False(item.CheckExisting)));

        _output.Batches.Clear();
        _output.Behavior = null;
        Assert.Equal(0, Run());

        Assert.All(_output.Batches, batch => Assert.All(batch, item => Assert.True(item.CheckExisting)));
        Assert.Equal(3, Summary().Imported);
    }

    [Fact]
    public void Items_on_the_wire_during_an_abort_stay_importing_and_are_reconciled_next_run()
    {
        _in.AddMails("IN", 2);
        _output.BatchFailure = _ => new OutputAbortedException("Ziel gesperrt");

        Assert.Equal(2, Run());
        Assert.Equal(2, Summary().Importing);

        _output.Batches.Clear();
        _output.BatchFailure = null;
        Assert.Equal(0, Run());

        Assert.All(_output.Batches, batch => Assert.All(batch, item => Assert.True(item.CheckExisting)));
        Assert.Equal(2, Summary().Imported);
    }

    [Fact]
    public void Failed_items_at_the_head_do_not_end_the_queue_early()
    {
        // Früher beendeten 80 fehlgeschlagene Items am Anfang die Queue ohne Meldung.
        _in.AddMails("IN", 500);
        _output.Behavior = item => item.Subject!.CompareTo("IN-") > 0 && int.Parse(item.Subject[3..]) >= 300
            ? new OutputResult(item, false, "Abgelehnt", Retryable: false)
            : null;

        Assert.Equal(1, Run());

        var summary = Summary();
        Assert.Equal(300, summary.Imported);
        Assert.Equal(200, summary.Failed);
    }

    [Fact]
    public void Transient_failures_are_retried_within_the_run_and_permanent_ones_are_not()
    {
        _in.Add("2", "A", "vorübergehend");
        _in.Add("2", "B", "dauerhaft");
        var attempts = new Dictionary<string, int>();
        _output.Behavior = item =>
        {
            lock (attempts)
            {
                attempts[item.Subject!] = attempts.GetValueOrDefault(item.Subject!) + 1;
                if (item.Subject == "dauerhaft")
                {
                    return new OutputResult(item, false, "ErrorInvalidItem", Retryable: false);
                }

                return attempts[item.Subject!] == 1 ? new OutputResult(item, false, "ServerBusy") : null;
            }
        };

        Assert.Equal(1, Run(Config(limits => limits.Retries = 2)));

        Assert.Equal(2, attempts["vorübergehend"]);
        Assert.Equal(1, attempts["dauerhaft"]);
        var documents = Documents();
        Assert.Equal(ItemState.Imported, documents.Single(d => d.Subject == "vorübergehend").State);
        Assert.Equal(ItemState.Failed, documents.Single(d => d.Subject == "dauerhaft").State);
    }

    [Fact]
    public void Abort_from_the_target_stops_the_export_instead_of_hanging()
    {
        _in.AddMails("IN", 5_000);
        _output.BatchFailure = _ => new OutputAbortedException("EWS ist für den Tenant gesperrt");

        var exit = Run();

        Assert.Equal(2, exit);
        var open = Summary();
        Assert.True(open.Total < 5_000, $"Export hätte stoppen müssen, erfasst wurden {open.Total}.");
        Assert.Equal(0, open.Imported);
    }

    [Fact]
    public void Unexpected_batch_exception_marks_items_failed_and_keeps_going()
    {
        _in.AddMails("IN", 60);
        var failures = 0;
        _output.BatchFailure = _ => Interlocked.Increment(ref failures) == 1 ? new InvalidOperationException("kaputt") : null;

        Assert.Equal(1, Run());

        var summary = Summary();
        Assert.Equal(40, summary.Imported);
        Assert.Equal(20, summary.Failed);
        Assert.Contains(Documents(), d => d.LastError == "kaputt");
    }

    [Fact]
    public void Limit_counts_items_handled_in_this_run_so_pilot_runs_make_progress()
    {
        _in.AddMails("IN", 10);

        Assert.Equal(0, Run(limit: 3));
        Assert.Equal(3, _output.Items.Count);

        Assert.Equal(0, Run(limit: 3));
        Assert.Equal(6, _output.Items.Count);
    }

    [Fact]
    public void Linked_foreign_archives_are_not_migrated_into_the_mailbox()
    {
        _in.AddMails("IN", 1);
        var calendar = _root.Sub("cal", "Kalender");
        calendar.Add("7", "OWN", "Eigener Termin");
        calendar.Link(@"resource\hol", "hol").Add("7", "HOL", "Feiertag");
        calendar.Link(@"B\0\0", "0").Add("7", "OTHER", "Fremder Termin");

        Assert.Equal(0, Run());

        Assert.Equal(2, _output.Items.Count);
        Assert.Contains(_output.Items, item => item.Item.Subject == "Eigener Termin");
        Assert.DoesNotContain(_output.Items, item => item.Item.Subject is "Feiertag" or "Fremder Termin");
    }

    [Fact]
    public void Archive_root_is_refused_so_no_foreign_mailbox_can_leak_in()
    {
        var david = new FakeDavid();
        var everything = david.AddRoot(UserRoot, "archive");
        everything.AddMails("X", 2);
        var config = Config();

        var exit = StaTaskRunner.Run(() => MigrationRunner.RunCoreAsync(
            config,
            new RunOptions(),
            david,
            _ => _output,
            TimeSpan.Zero,
            CancellationToken.None));

        Assert.Equal(1, exit);
        Assert.Empty(_output.Items);
    }

    [Fact]
    public void One_broken_mailbox_does_not_stop_the_others()
    {
        _in.AddMails("IN", 2);
        var config = Config();
        config.Mailboxes.Insert(0, new MailboxMapping
        {
            Id = "kaputt",
            Source = new SourceConfig { ArchivePath = @"user\gibt-es-nicht", Email = "a@b.c" },
            Target = new TargetConfig { Upn = "a@b.c" },
            Include = new List<string> { "all" }
        });

        Assert.Equal(1, Run(config));

        Assert.Equal(2, Summary(config).Imported);
    }

    [Fact]
    public void Old_manifest_is_refused_because_its_imports_cannot_be_trusted()
    {
        var config = Config();
        using (var manifest = new LiteDbManifest(config.Staging.Manifest))
        {
            manifest.UpsertDiscovered(new ItemDocument { Key = "alt", MailboxId = "box" });
        }

        Assert.Throws<CliException>(() => Run(config));
        Assert.Empty(_output.Items);
    }

    [Fact]
    public void Staging_files_are_removed_after_a_complete_transfer()
    {
        _in.AddMails("IN", 25);

        Assert.Equal(0, Run());

        Assert.False(Directory.Exists(Path.Combine(_directory, "staging", "eml-stage", "box")));
    }

    [Fact]
    public void Failed_items_keep_their_staged_file_for_the_next_attempt()
    {
        _in.AddMails("IN", 2);
        _output.Behavior = item => new OutputResult(item, false, "Timeout");

        Assert.Equal(1, Run());

        Assert.All(Documents(), document => Assert.True(File.Exists(document.StagingPath)));
    }

    [Fact]
    public void Interrupted_eml_write_is_never_transferred()
    {
        _in.Add("2", "A", "abgebrochen", item => item.NativeExport = path =>
        {
            File.WriteAllText(path, "Subject: halb");
            throw new IOException("Datenträger voll");
        });

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        var document = Assert.Single(Documents());
        Assert.Equal(ItemState.Failed, document.State);
        Assert.Contains("Datenträger voll", document.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_native_export_falls_back_to_a_generated_message()
    {
        _in.Add("2", "A", "Fallback", item =>
        {
            item.Set("From", "kunde@example.com");
            item.Set("BodyText", "Inhalt");
            item.NativeExport = path =>
            {
                File.WriteAllText(path, "");
                return true;
            };
        });

        Assert.Equal(0, Run());

        Assert.Equal("Fallback", Assert.Single(_output.Items).EmlSubject);
    }

    [Fact]
    public void Mails_with_native_export_never_read_the_expensive_properties()
    {
        var mail = _in.Add("2", "A", "normal");

        Assert.Equal(0, Run());

        Assert.Equal(0, mail.PropertyReads);
    }

    [Fact]
    public void Sent_items_are_marked_read_and_received_items_keep_their_flag()
    {
        _in.AddMails("IN", 1);
        _out.AddMails("OUT", 1);

        Assert.Equal(0, Run());

        Assert.True(_output.Items.Single(i => i.EmlSubject == "OUT-0").Item.IsRead);
        Assert.False(_output.Items.Single(i => i.EmlSubject == "IN-0").Item.IsRead);
    }

    private static readonly byte[] FaxTiff =
        Convert.FromBase64String("SUkqAAgAAAACAAABBAABAAAAAQAAAAEBBAABAAAAAQAAAAAAAAA=");

    [Fact]
    public void Outgoing_fax_goes_from_the_mailbox_to_the_number()
    {
        _out.Add("3", "F1", "", item =>
        {
            item.Subject = null;
            item.Set("To", "+49 9971 123456");
            item.AttachmentList.Add(new DavidAttachmentInfo("fax.tif", "image/tiff", null, 2));
        });

        Assert.Equal(0, Run());

        var message = Assert.Single(_output.Items).Message!;
        Assert.Equal(TargetUpn, ((MailboxAddress)message.From[0]).Address);
        Assert.Equal("+49 9971 123456", ((MailboxAddress)message.To[0]).Name);
        Assert.Equal("[FAX] +49 9971 123456", message.Subject);
        Assert.Single(message.Attachments);
    }

    [Fact]
    public void Incoming_sms_goes_from_the_number_to_the_mailbox_with_its_text()
    {
        _in.Add("4", "S1", "", item =>
        {
            item.Subject = null;
            item.Set("From", "+49 171 1234567");
            item.Set("BodyText", "Rückruf bitte");
        });

        Assert.Equal(0, Run());

        var delivered = Assert.Single(_output.Items);
        var message = delivered.Message!;
        Assert.Equal("+49 171 1234567", ((MailboxAddress)message.From[0]).Name);
        Assert.Equal(TargetUpn, ((MailboxAddress)message.To[0]).Address);
        Assert.Equal("[SMS] +49 171 1234567", message.Subject);
        Assert.Equal("Rückruf bitte", message.TextBody!.Trim());
        Assert.Equal("sms", delivered.Item.Kind);
    }

    [Fact]
    public void Voice_message_keeps_every_audio_attachment_even_with_equal_names()
    {
        _in.Add("5", "V1", "Sprachnachricht", item =>
        {
            item.Set("From", "+49 171 1234567");
            item.AttachmentList.Add(new DavidAttachmentInfo("nachricht.wav", "audio/wav", null, 2, 0));
            item.AttachmentList.Add(new DavidAttachmentInfo("nachricht.wav", "audio/wav", null, 2, 1));
        });

        Assert.Equal(0, Run());

        var parts = Assert.Single(_output.Items).Message!.Attachments.OfType<MimePart>().ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, part => Assert.Equal("nachricht.wav", part.FileName));
        var contents = parts.Select(part =>
        {
            using var stream = new MemoryStream();
            part.Content!.DecodeTo(stream);
            return stream.ToArray()[0];
        });
        Assert.Equal(new byte[] { 0, 1 }, contents.OrderBy(x => x));
    }

    [Theory]
    [InlineData("3", "Fax")]
    [InlineData("5", "Voice")]
    public void Fax_and_voice_without_their_file_fail_instead_of_arriving_empty(string type, string label)
    {
        _in.Add(type, "X1", "ohne Datei", item => item.FaxImagePath = @"\\srv\david\archive\gibt-es-nicht.001");

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        var document = Assert.Single(Documents());
        Assert.Equal(ItemState.Failed, document.State);
        Assert.StartsWith(label, document.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Fax_image_from_the_archive_becomes_a_pdf_attachment()
    {
        _in.Add("3", "F2", "Fax mit Bild", item =>
        {
            item.Set("From", "ACME Fax");
            item.FaxImage = FaxTiff;
        });

        Assert.Equal(0, Run());

        var part = Assert.Single(Assert.Single(_output.Items).Message!.Attachments.OfType<MimePart>());
        Assert.StartsWith("fax-", part.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_fax_never_blocks_the_mails_behind_it()
    {
        _in.Add("3", "X1", "ohne Datei");
        _in.AddMails("IN", 5);

        Assert.Equal(1, Run());

        Assert.Equal(5, _output.Items.Count);
        Assert.Equal(1, Summary().Failed);
    }

    [Fact]
    public void Included_types_are_respected_and_unsupported_types_are_skipped_with_a_reason()
    {
        _in.AddMails("IN", 1);
        _in.Add("3", "F3", "Fax");
        _in.Add("8", "L1", "Verknüpfung");

        Assert.Equal(0, Run(Config(include: new List<string> { "mail" })));

        Assert.Single(_output.Items);
        var skipped = Documents().Where(d => d.State == ItemState.Skipped).ToList();
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, document => Assert.False(string.IsNullOrWhiteSpace(document.LastError)));
    }

    [Fact]
    public void Identical_contacts_without_a_file_are_all_kept()
    {
        var contacts = _root.Sub("address", "Adressen");
        for (var i = 0; i < 3; i++)
        {
            contacts.Add("1", "n" + i, "Max Muster", item => item.Set("TextSource", null));
        }

        Assert.Equal(0, Run());

        Assert.Equal(3, Summary().Imported);
        Assert.Equal(3, Documents().Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public void Item_key_reaches_the_target_for_idempotent_lookups()
    {
        _in.AddMails("IN", 2);

        Assert.Equal(0, Run());

        Assert.All(_output.Items, item =>
        {
            Assert.StartsWith(@"user\10018000|user\10018000\in|", item.Item.ItemKey, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("|0", item.Item.ItemKey, StringComparison.Ordinal);
        });
        Assert.Equal(2, _output.Items.Select(item => item.Item.ItemKey).Distinct().Count());
    }

    [Fact]
    public void Local_test_run_does_not_block_the_later_transfer_to_exchange()
    {
        _in.AddMails("IN", 2);
        _output.Name = "local";
        Assert.Equal(0, Run());

        _output.Name = "ews";
        Assert.Equal(0, Run());

        Assert.Equal(4, _output.Items.Count);
        Assert.All(Documents(), document => Assert.Equal("ews", document.ImportedTo));
    }

    [Fact]
    public void Fax_with_attachment_keeps_its_fax_image_too()
    {
        _in.Add("3", "F4", "Fax mit Anhang", item =>
        {
            item.Set("From", "ACME");
            item.FaxImage = FaxTiff;
            item.AttachmentList.Add(new DavidAttachmentInfo("anlage.txt", "text/plain", null, 2));
        });

        Assert.Equal(0, Run());

        var parts = Assert.Single(_output.Items).Message!.Attachments.OfType<MimePart>().ToList();
        Assert.Contains(parts, part => part.FileName!.StartsWith("fax-", StringComparison.Ordinal));
        Assert.Contains(parts, part => part.FileName == "anlage.txt");
    }

    [Theory]
    [InlineData("4", "SMS")]
    [InlineData("6", "Chat")]
    public void Sms_and_chat_without_text_fail_instead_of_arriving_empty(string type, string label)
    {
        _in.Add(type, "T1", "ohne Text");

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        Assert.StartsWith(label, Assert.Single(Documents()).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_david_api_fails_visibly_instead_of_replacing_all_mail_with_generated_messages()
    {
        _in.Add("2", "A", "normal", item => item.NativeExport = _ => false);
        _in.AddMails("IN", 0);
        var original = FakeItem.NativeErrorText;
        FakeItem.NativeErrorText = "DvApi32.dll nicht gefunden.";
        try
        {
            Assert.Equal(1, Run());
        }
        finally
        {
            FakeItem.NativeErrorText = original;
        }

        Assert.Empty(_output.Items);
        Assert.Contains("DvApi32.dll", Assert.Single(Documents()).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Exclusion_matches_whole_path_segments_and_not_parts_of_a_name()
    {
        _in.AddMails("IN", 1);
        _root.Sub("IP-Addresses", "IP-Addresses").Add("2", "KEEP", "behalten");
        _root.Sub("ADDRESS", "Adressen").Add("2", "DROP", "weg");
        var config = Config();
        config.Mailboxes[0].ExcludeArchives.Add("ADDRESS");

        Assert.Equal(0, Run(config));

        Assert.Contains(_output.Items, item => item.EmlSubject == "behalten");
        Assert.DoesNotContain(_output.Items, item => item.EmlSubject == "weg");
    }

    [Fact]
    public void Display_name_with_a_slash_stays_one_folder_level()
    {
        _in.Sub("123", "Kunden/Projekte").Add("2", "A", "tief");

        Assert.Equal(0, Run());

        Assert.Equal("in/Kunden_Projekte", Assert.Single(_output.Items).Item.FolderPath);
    }

    [Fact]
    public void Custom_top_level_folder_goes_below_the_inbox_even_when_named_like_a_root()
    {
        _root.Sub("9A", "Gesendet").Add("2", "A", "eigen");
        _root.Sub("system", "System").Sub("trash", "Papierkorb").Add("2", "B", "gelöscht");

        Assert.Equal(0, Run());

        Assert.Equal("in/Gesendet", _output.Items.Single(i => i.EmlSubject == "eigen").Item.FolderPath);
        Assert.Equal("trash", _output.Items.Single(i => i.EmlSubject == "gelöscht").Item.FolderPath);
    }

    [Fact]
    public void Limited_run_never_counts_as_a_complete_walk()
    {
        _in.AddMails("IN", 5);

        Assert.Equal(0, Run(limit: 2));

        using var manifest = new LiteDbManifest(Config().Staging.Manifest);
        Assert.False(manifest.HasCompleteWalk("box", UserRoot));
    }

    [Fact]
    public void Complete_run_is_recorded_as_a_complete_walk()
    {
        _in.AddMails("IN", 5);

        Assert.Equal(0, Run());

        using var manifest = new LiteDbManifest(Config().Staging.Manifest);
        Assert.True(manifest.HasCompleteWalk("box", UserRoot));
    }

    [Fact]
    public void Unreadable_item_keeps_the_others_but_marks_the_mailbox_failed()
    {
        _in.Add("2", "A", "lesbar");
        _in.Add("7", "B", "kaputt", item => item.ThrowOnProperties = true);
        _in.Add("2", "C", "auch lesbar");

        Assert.Equal(1, Run());

        Assert.Equal(2, _output.Items.Count);
        using var manifest = new LiteDbManifest(Config().Staging.Manifest);
        Assert.False(manifest.HasCompleteWalk("box", UserRoot));
    }

    [Fact]
    public void Unknown_mailbox_selection_is_an_error_and_not_an_empty_success()
    {
        var config = Config();

        Assert.Throws<CliException>(() => StaTaskRunner.Run(() => MigrationRunner.RunCoreAsync(
            config,
            new RunOptions { MailboxId = "gibt-es-nicht" },
            _david,
            _ => _output,
            TimeSpan.Zero,
            CancellationToken.None)));
    }

    [Fact]
    public void Cancellation_stops_the_export_and_keeps_the_state_resumable()
    {
        _in.AddMails("IN", 2_000);
        using var cancellation = new CancellationTokenSource();
        _output.BatchFailure = _ =>
        {
            cancellation.Cancel();
            return null;
        };
        var config = Config();

        var exit = StaTaskRunner.Run(() => MigrationRunner.RunCoreAsync(
            config,
            new RunOptions(),
            _david,
            _ => _output,
            TimeSpan.Zero,
            cancellation.Token));

        Assert.Equal(2, exit);
        Assert.True(Summary(config).Total < 2_000);
    }

    [Fact]
    public void Discover_only_records_items_without_exporting()
    {
        _in.AddMails("IN", 3);
        var config = Config();

        var exit = MigrationRunner.DiscoverCore(config, new RunOptions(), _david);

        Assert.Equal(0, exit);
        Assert.Equal(3, Summary(config).Discovered);
        Assert.False(Directory.Exists(Path.Combine(_directory, "staging", "eml-stage")));
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        _in.AddMails("IN", 3);
        var config = Config();

        var exit = MigrationRunner.DiscoverCore(config, new RunOptions { DryRun = true }, _david);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(config.Staging.Manifest));
    }

    [Fact]
    public void Fax_with_an_attachment_but_unreadable_image_fails_instead_of_losing_the_page()
    {
        _out.Add("3", "F", "", item =>
        {
            item.FaxImagePath = Path.Combine(_directory, "gibt-es-nicht", "M[1.001");
            item.Set("To", "+49 1");
            item.AttachmentList.Add(new DavidAttachmentInfo("anlage.txt", null, null, 2));
        });

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        var document = Assert.Single(Documents());
        Assert.Equal(ItemState.Failed, document.State);
        Assert.Contains("nicht lesbar", document.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_fax_is_transferred_once_its_image_became_readable()
    {
        var image = Path.Combine(_directory, "M[1.001");
        var fax = _out.Add("3", "F", "", item =>
        {
            item.FaxImagePath = image;
            item.Set("To", "+49 1");
        });
        Assert.Equal(1, Run());

        fax.FaxImage = TestTiff();

        Assert.Equal(0, Run());
        Assert.Equal(ItemState.Imported, Assert.Single(Documents()).State);
        Assert.Single(_output.Items);
    }

    private static byte[] TestTiff()
    {
        using var bitmap = new System.Drawing.Bitmap(10, 10);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Tiff);
        return stream.ToArray();
    }

    [Theory]
    [InlineData("3")]
    [InlineData("5")]
    public void Empty_fax_or_voice_file_fails_instead_of_arriving_empty(string type)
    {
        var empty = Path.Combine(_directory, "leer.001");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        _in.Add(type, "E", "", item =>
        {
            item.FaxImagePath = empty;
            item.Set("From", "+49 1");
        });

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        Assert.Equal(ItemState.Failed, Assert.Single(Documents()).State);
    }

    [Fact]
    public void Mail_without_native_export_and_without_text_fails_instead_of_arriving_empty()
    {
        _in.Add("2", "A", "Rechnung", item => item.NativeExport = _ => false);

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        var document = Assert.Single(Documents());
        Assert.Equal(ItemState.Failed, document.State);
        Assert.Contains(FakeItem.NativeErrorText, document.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Mail_fallback_keeps_real_recipients_and_cc_lists()
    {
        _out.Add("2", "A", "Fallback", item =>
        {
            item.NativeExport = _ => false;
            item.Set("BodyText", "Inhalt");
            item.Set("To", "Nur Anzeigetext");
            item.Set("Recipients", "a@example.com; b@example.com");
            item.Set("CC", "c@example.com; d@example.com");
        });

        Assert.Equal(0, Run());

        var message = Assert.Single(_output.Items).Message!;
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, message.To.Mailboxes.Select(m => m.Address));
        Assert.Equal(new[] { "c@example.com", "d@example.com" }, message.Cc.Mailboxes.Select(m => m.Address));
    }

    [Fact]
    public void Repeated_david_file_is_sent_once_and_recorded_with_its_reason()
    {
        _in.Add("2", "A", "erster");
        _in.Add("2", "A", "wiederholt");

        Assert.Equal(0, Run());

        Assert.Single(_output.Items);
        var skipped = Assert.Single(Documents(), document => document.State == ItemState.Skipped);
        Assert.Contains("mehrfach", skipped.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void File_names_that_only_look_alike_stay_two_messages()
    {
        _in.Add("2", "A", "eins", item => item.Set("TextSource", @"\\srv\david\archive\user\1\in\ICD058.001"));
        _in.Add("2", "B", "zwei", item => item.Set("TextSource", @"\\srv\david\archive\user\1\in\I0CD58.001"));

        Assert.Equal(0, Run());

        Assert.Equal(2, _output.Items.Count);
    }

    [Fact]
    public void Appointment_with_unexportable_attachments_fails_visibly_instead_of_losing_them()
    {
        _in.Add("7", "T", "Termin", item => item.Set("AttachmentCount", "2"));

        Assert.Equal(1, Run());

        Assert.Empty(_output.Items);
        var document = Assert.Single(Documents());
        Assert.Equal(ItemState.Failed, document.State);
        Assert.Contains("2 Anhänge", document.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachment_count_of_a_mail_survives_the_next_walk()
    {
        _in.Add("2", "A", "mit Anhang", item => item.AttachmentCount = 3);
        Assert.Equal(0, Run());

        Assert.Equal(0, Run());

        Assert.Equal(3, Assert.Single(Documents()).AttachmentCount);
    }

    [Fact]
    public void Unreadable_archive_keeps_its_siblings_but_leaves_the_walk_incomplete()
    {
        _in.ItemsFailure = new InvalidOperationException("Keine Rechte");
        _in.AddMails("IN", 2);
        _out.AddMails("OUT", 3);

        Assert.Equal(1, Run());

        Assert.Equal(3, _output.Items.Count);
        using var manifest = new LiteDbManifest(Config().Staging.Manifest);
        Assert.False(manifest.HasCompleteWalk("box", UserRoot));
    }

    [Fact]
    public void Unreadable_sub_archives_keep_the_items_of_that_archive()
    {
        _root.SubArchivesFailure = null;
        _in.AddMails("IN", 2);
        _in.SubArchivesFailure = new InvalidOperationException("Keine Rechte");
        _out.AddMails("OUT", 1);

        Assert.Equal(1, Run());

        Assert.Equal(3, _output.Items.Count);
    }

    [Fact]
    public void Fatal_transfer_abort_stops_the_david_walk()
    {
        _in.AddMails("ALT", 1500);
        Assert.Equal(0, Run());
        var before = DateTime.UtcNow;
        _in.AddMails("NEU", 21);
        _output.BatchFailure = _ => new OutputAbortedException("Postfach voll", affectsAllMailboxes: false);

        Assert.Equal(1, Run());

        Assert.True(Documents().Count(document => document.LastSeenUtc.ToUniversalTime() >= before) < 1521);
    }

    private int Verify(MigrationConfig config, string? output = null)
    {
        return MigrationRunner.VerifyCore(config, new RunOptions { OutputKind = output });
    }

    [Fact]
    public void Verify_does_not_accept_a_local_test_run_as_migration()
    {
        _in.AddMails("IN", 3);
        _output.Name = "local";
        var config = Config();
        Assert.Equal(0, Run(config));

        Assert.Equal(1, Verify(config));
        Assert.Equal(0, Verify(config, "local"));
        Assert.Equal(0, Verify(config, "file"));
    }

    [Fact]
    public void Verify_accepts_a_complete_exchange_run()
    {
        _in.AddMails("IN", 3);
        _output.Name = OutputTargetFactory.TargetName("ews", TargetUpn);
        var config = Config();

        Assert.Equal(0, Run(config));

        Assert.Equal(0, Verify(config));
    }

    [Fact]
    public void Changed_target_mailbox_is_transferred_again_and_fails_verify_until_then()
    {
        _in.AddMails("IN", 3);
        _output.Name = OutputTargetFactory.TargetName("ews", TargetUpn);
        var config = Config();
        Assert.Equal(0, Run(config));

        config.Mailboxes[0].Target.Upn = "ECHT@example.com";
        Assert.Equal(1, Verify(config));

        _output.Name = OutputTargetFactory.TargetName("ews", config.Mailboxes[0].Target.Upn);
        Assert.Equal(0, Run(config));

        Assert.Equal(6, _output.Items.Count);
        Assert.Equal(0, Verify(config));
        Assert.All(Documents(), document => Assert.Equal("ews:echt@example.com", document.ImportedTo));
    }

    [Fact]
    public void Verify_reflects_the_latest_walk_and_not_an_earlier_complete_one()
    {
        _in.AddMails("IN", 3);
        _output.Name = OutputTargetFactory.TargetName("ews", TargetUpn);
        var config = Config();
        Assert.Equal(0, Run(config));
        Assert.Equal(0, Verify(config));

        _in.AddMails("NEU", 5);
        Assert.Equal(0, Run(config, limit: 1));
        Assert.Equal(1, Verify(config));

        Assert.Equal(0, Run(config, limit: 0));
        Assert.Equal(0, Verify(config));
    }

    [Fact]
    public void Verify_fails_when_the_delta_run_hit_an_unreadable_item()
    {
        _in.AddMails("IN", 3);
        _output.Name = OutputTargetFactory.TargetName("ews", TargetUpn);
        var config = Config();
        Assert.Equal(0, Run(config));

        _in.Add("7", "K", "kaputt", item => item.ThrowOnProperties = true);
        Assert.Equal(1, Run(config));

        Assert.Equal(1, Verify(config));
    }

    [Fact]
    public void Verify_fails_when_the_source_of_the_mailbox_changed()
    {
        _in.AddMails("IN", 3);
        _output.Name = OutputTargetFactory.TargetName("ews", TargetUpn);
        var config = Config();
        Assert.Equal(0, Run(config));

        config.Mailboxes[0].Source.ArchivePath = @"user\anderes";

        Assert.Equal(1, Verify(config));
    }

    [Fact]
    public void Discover_continues_with_the_next_mailbox_after_a_broken_one()
    {
        _in.ItemsFailure = new InvalidOperationException("Keine Rechte");
        _david.AddRoot(@"user\zweites").Sub("in", "Eingang").AddMails("Z", 2);
        var config = Config();
        config.Mailboxes.Add(new MailboxMapping
        {
            Id = "zweites",
            Source = new SourceConfig { ArchivePath = @"user\zweites", Email = "z@example.com" },
            Target = new TargetConfig { Upn = "z@example.com" }
        });

        Assert.Equal(1, MigrationRunner.DiscoverCore(config, new RunOptions { Limit = 0 }, _david));

        using var manifest = new LiteDbManifest(config.Staging.Manifest);
        Assert.Equal(2, manifest.GetSummary("zweites").Total);
    }

    [Fact]
    public void Cancelled_dry_run_stops_with_the_abort_exit_code()
    {
        _in.AddMails("IN", 2);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal(
            2,
            MigrationRunner.DiscoverCore(Config(), new RunOptions { DryRun = true, Limit = 0 }, _david, cancelled.Token));
    }

    [Fact]
    public void Unpack_keeps_going_after_an_unreadable_archive_and_leaves_links_alone()
    {
        var broken = _root.Sub("kaputt");
        broken.SubArchivesFailure = new InvalidOperationException("Keine Rechte");
        var sibling = _root.Sub("heil");
        var linked = _root.Link(@"resource\hol", "hol");
        int archives = 0, changed = 0, failed = 0;

        UnpackRunner.Recurse(_root, _root.ResolvedPath, false, ref archives, ref changed, ref failed);

        Assert.Equal(1, failed);
        Assert.Equal(1, sibling.UnpackCalls);
        Assert.Equal(0, linked.UnpackCalls);
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("mail", true)]
    [InlineData("3", false)]
    [InlineData("4", false)]
    [InlineData("5", false)]
    [InlineData("6", false)]
    [InlineData("fax", false)]
    [InlineData("voicemail", false)]
    public void Probe_runs_the_native_export_only_on_real_mails(string type, bool expected)
    {
        Assert.Equal(expected, David2Exchange.Probe.ProbeRunner.MayExportEml(type));
    }

    [Theory]
    [InlineData("Europe/Berlinn")]
    [InlineData("")]
    public void Invalid_time_zone_stops_with_a_clear_error(string timeZone)
    {
        var config = Config();
        config.David.Timezone = timeZone;

        var error = Assert.Throws<CliException>(() => config.Validate(requireM365: false));

        Assert.Contains("david.timezone", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("W. Europe Standard Time")]
    public void Both_spellings_of_the_time_zone_are_accepted(string timeZone)
    {
        Assert.Equal(TimeSpan.FromHours(1), DavidTimeZone.Resolve(timeZone).BaseUtcOffset);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Local_output_appends_only_for_resumable_runs(bool append, int expectedRows)
    {
        var directory = Path.Combine(_directory, "lokal");
        for (var run = 0; run < 2; run++)
        {
            await using var output = new LocalFileOutputTarget();
            await output.OpenAsync(new OutputContext(directory, AppendItems: append), CancellationToken.None);
            await output.WriteItemAsync(
                new OutputItem("a", "1", "2", "mail", "s", 1, 0, null, null, null, null, null, new Dictionary<string, string?>()),
                CancellationToken.None);
        }

        Assert.Equal(expectedRows, File.ReadAllLines(Path.Combine(directory, "items.jsonl")).Length);
    }

    [Fact]
    public void Manifest_failure_in_the_retry_pass_lets_the_workers_finish_and_names_the_pass()
    {
        _in.Add("2", "A", "a");
        _in.Add("2", "B", "b");
        _in.Add("2", "C", "c");
        var config = Config(limits =>
        {
            limits.Retries = 1;
            limits.Workers = 1;
            limits.BatchSize = 1;
        });
        using var manifest = new LiteDbManifest(config.Staging.Manifest);
        var seen = new List<string>();
        _output.Behavior = item =>
        {
            seen.Add(item.ItemKey);
            if (seen.Count == 3)
            {
                // Ein Worker: Das zweite Dokument ist gespeichert und wird nicht mehr überschrieben.
                var raw = manifest.Database.GetCollection("items");
                var stored = raw.FindById(seen[1]);
                stored["State"] = "Kaputt";
                raw.Update(stored);
            }

            return seen.Count <= 3 ? new OutputResult(item, false, "ServerBusy") : null;
        };

        // Das Manifest gehört hier dem Test, deshalb direkt statt über RunCoreAsync.
        var migrate = typeof(MigrationRunner).GetMethod("MigrateMailboxAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failure = Record.Exception(() => StaTaskRunner.Run(async () =>
        {
            await (Task)migrate.Invoke(null, new object[]
            {
                config,
                manifest,
                _david,
                config.Mailboxes[0],
                (Func<MailboxMapping, IOutputTarget>)(_ => _output),
                0,
                TimeSpan.Zero,
                CancellationToken.None
            })!;
            return 0;
        }));

        Assert.NotNull(failure);
        Assert.Contains("Wiederholung", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ItemState.Imported, manifest.Get(seen[0])!.State);
    }
}
