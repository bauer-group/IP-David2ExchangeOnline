// SPDX-License-Identifier: MIT
using David2Exchange.Manifest;
using LiteDB;

namespace David2Exchange.Tests.Manifest;

public sealed class LiteDbManifestTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-manifest-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "manifest.db");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ItemDocument Document(string key, Dictionary<string, string?>? properties = null)
    {
        return new ItemDocument
        {
            Key = key,
            MailboxId = "mailbox",
            ArchivePath = @"user\1\cal",
            DavidId = "1",
            ItemType = "7",
            Properties = properties ?? new Dictionary<string, string?>()
        };
    }

    /// <summary>
    /// LiteDB 5.0.21 writes a nested "_ID" key twice but counts it once, which closes the
    /// engine with error 999. Calendar, contact and task items carry that key from David.
    /// </summary>
    [Theory]
    [InlineData("_ID")]
    [InlineData("_Id")]
    [InlineData("_id")]
    public void Properties_with_reserved_id_key_are_stored_without_breaking_the_database(string reservedKey)
    {
        using (var manifest = new LiteDbManifest(DatabasePath))
        {
            manifest.UpsertDiscovered(Document("a", new Dictionary<string, string?>
            {
                [reservedKey] = "12",
                ["Subject"] = "Termin",
                ["BodyText"] = "Text"
            }));
        }

        using var reopened = new LiteDbManifest(DatabasePath);
        var stored = reopened.Get("a");
        Assert.NotNull(stored);
        Assert.Equal("Termin", stored.Properties["Subject"]);
        Assert.DoesNotContain(stored.Properties.Keys, key => key.Equals("_id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_also_strips_reserved_keys_when_properties_are_added_later()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        var document = manifest.UpsertDiscovered(Document("a"));

        document.Properties = new Dictionary<string, string?> { ["_ID"] = "1", ["Location"] = "Raum" };
        manifest.Save(document);

        Assert.Equal("Raum", manifest.Get("a")!.Properties["Location"]);
    }

    [Fact]
    public void UpsertDiscovered_returns_the_persisted_state_of_a_known_item()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        var first = manifest.UpsertDiscovered(Document("a"));
        first.State = ItemState.Imported;
        first.ImportedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        first.Attempts = 2;
        manifest.Save(first);

        var again = manifest.UpsertDiscovered(Document("a"));

        Assert.Equal(ItemState.Imported, again.State);
        Assert.Equal(2, again.Attempts);
        Assert.NotNull(again.ImportedUtc);
    }

    [Fact]
    public void UpsertDiscovered_refreshes_source_fields_but_keeps_transfer_state()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        var first = manifest.UpsertDiscovered(Document("a"));
        first.State = ItemState.Exported;
        first.StagingPath = "x.eml";
        manifest.Save(first);

        var changed = Document("a");
        changed.FolderPath = "in/Projekt";
        changed.Subject = "Neu";
        var merged = manifest.UpsertDiscovered(changed);

        Assert.Equal("in/Projekt", merged.FolderPath);
        Assert.Equal("Neu", merged.Subject);
        Assert.Equal(ItemState.Exported, merged.State);
        Assert.Equal("x.eml", merged.StagingPath);
    }

    [Fact]
    public void SaveMany_persists_every_document()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        var documents = new[] { "a", "b", "c" }.Select(key => manifest.UpsertDiscovered(Document(key))).ToList();
        foreach (var document in documents)
        {
            document.State = ItemState.Importing;
        }

        manifest.SaveMany(documents);

        Assert.All(new[] { "a", "b", "c" }, key => Assert.Equal(ItemState.Importing, manifest.Get(key)!.State));
    }

    [Fact]
    public void Parallel_writers_and_readers_do_not_break_the_database()
    {
        using (var manifest = new LiteDbManifest(DatabasePath))
        {
            var documents = Enumerable.Range(0, 400)
                .Select(i => manifest.UpsertDiscovered(Document("k" + i)))
                .ToList();

            Parallel.ForEach(
                documents,
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                document =>
                {
                    document.State = ItemState.Imported;
                    manifest.Save(document);
                    _ = manifest.Get(document.Key);
                    _ = manifest.GetSummary("mailbox");
                });

            Assert.Equal(400, manifest.GetSummary("mailbox").Imported);
        }

        using var reopened = new LiteDbManifest(DatabasePath);
        Assert.Equal(400, reopened.GetSummary().Imported);
    }

    [Fact]
    public void Summary_counts_every_state_per_mailbox()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        var a = manifest.UpsertDiscovered(Document("a"));
        a.State = ItemState.Failed;
        manifest.Save(a);
        manifest.UpsertDiscovered(Document("b"));
        var other = Document("c");
        other.MailboxId = "other";
        manifest.UpsertDiscovered(other);

        var summary = manifest.GetSummary("mailbox");

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Discovered);
        Assert.Equal(2, summary.Total);
    }

    [Fact]
    public void Dispose_never_masks_an_earlier_failure_and_releases_the_file()
    {
        // Provokes the engine close the same way the real incident did: a nested "_ID" key
        // written through the raw API leaves a read lock behind on this thread.
        var manifest = new LiteDbManifest(DatabasePath);
        manifest.UpsertDiscovered(Document("a"));
        var original = Record.Exception(() => manifest.Database.GetCollection("probe").Insert(
            new BsonDocument { ["_id"] = "x", ["nested"] = new BsonDocument { ["_ID"] = "1" } }));
        Assert.IsType<LiteException>(original);

        var dispose = Record.Exception(manifest.Dispose);

        Assert.Null(dispose);
        using var reopened = new LiteDbManifest(DatabasePath);
        Assert.NotNull(reopened.Get("a"));
    }

    [Fact]
    public void Folder_of_an_item_that_may_already_be_stored_stays_unchanged()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        var document = manifest.UpsertDiscovered(Document("k"));
        document.Attempts = 1;
        document.FolderPath = "in/Kunden";
        document.State = ItemState.Failed;
        manifest.Save(document);

        var incoming = Document("k");
        incoming.FolderPath = "in/Kunden-Alt";

        Assert.Equal("in/Kunden", manifest.UpsertDiscovered(incoming).FolderPath);
    }

    [Fact]
    public void Folder_of_an_item_without_a_send_attempt_follows_the_source()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        manifest.UpsertDiscovered(Document("k"));

        var incoming = Document("k");
        incoming.FolderPath = "in/Neu";

        Assert.Equal("in/Neu", manifest.UpsertDiscovered(incoming).FolderPath);
    }

    [Fact]
    public void Walk_proof_is_bound_to_its_source_and_can_be_revoked()
    {
        using var manifest = new LiteDbManifest(DatabasePath);

        manifest.MarkWalkComplete("mailbox", @"user\1");

        Assert.True(manifest.HasCompleteWalk("mailbox", @"USER\1"));
        Assert.False(manifest.HasCompleteWalk("mailbox", @"user\2"));

        manifest.ClearWalkComplete("mailbox");

        Assert.False(manifest.HasCompleteWalk("mailbox", @"user\1"));
    }

    [Fact]
    public void Imported_rows_of_other_targets_are_counted_but_rows_without_target_are_not()
    {
        using var manifest = new LiteDbManifest(DatabasePath);
        foreach (var (key, target) in new[] { ("a", "local"), ("b", "ews:ziel@example.com"), ("c", (string?)null) })
        {
            var document = manifest.UpsertDiscovered(Document(key));
            document.State = ItemState.Imported;
            document.ImportedTo = target;
            manifest.Save(document);
        }

        Assert.Equal(1, manifest.CountImportedElsewhere("mailbox", "ews:ziel@example.com"));
        Assert.Equal(1, manifest.CountImportedElsewhere("mailbox", "local"));
    }
}
