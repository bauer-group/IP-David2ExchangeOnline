// SPDX-License-Identifier: MIT
using David2Exchange.Cli;
using LiteDB;

namespace David2Exchange.Manifest;

public sealed class LiteDbManifest : IDisposable
{
    private const int CurrentSchemaVersion = 2;

    private readonly LiteDatabase _database;
    private readonly ILiteCollection<ItemDocument> _items;
    private readonly ILiteCollection<RunDocument> _runs;
    private readonly ILiteCollection<WalkDocument> _walks;
    private readonly string _path;
    private readonly object _gate = new();

    public LiteDbManifest(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _path = fullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _database = new LiteDatabase($"Filename={fullPath};Upgrade=true");
        _items = _database.GetCollection<ItemDocument>("items");
        _runs = _database.GetCollection<RunDocument>("runs");
        _walks = _database.GetCollection<WalkDocument>("walks");
        _items.EnsureIndex(x => x.MailboxId);
        _items.EnsureIndex(x => x.State);
        _items.EnsureIndex(x => x.ReceivedLocal);
    }

    public string DatabasePath => _path;

    internal LiteDatabase Database => _database;

    /// <summary>
    /// Stellt vor einem schreibenden Lauf sicher, dass das Manifest vertrauenswürdig ist.
    /// Manifeste vor Schema 2 konnten mehreren Items denselben EML-Pfad zuweisen; ihr
    /// Status "Imported" sagt deshalb nichts darüber, ob der richtige Inhalt übertragen wurde.
    /// </summary>
    public void EnsureCurrentSchema()
    {
        lock (_gate)
        {
            if (_database.UserVersion >= CurrentSchemaVersion)
            {
                return;
            }

            if (_items.Count() > 0)
            {
                throw new CliException(
                    $"Das Manifest '{_path}' stammt aus einer älteren Version. Damals konnten sich " +
                    "Eingang und Ausgang denselben EML-Staging-Pfad teilen, importierte Inhalte sind " +
                    "daher nicht vertrauenswürdig. Bitte das Manifest samt '-log.db' und den Ordner " +
                    "'eml-stage' sichern und entfernen, die Zielpostfächer prüfen und neu starten.");
            }

            _database.UserVersion = CurrentSchemaVersion;
        }
    }

    public RunDocument BeginRun(string command, string toolVersion)
    {
        lock (_gate)
        {
            var run = new RunDocument
            {
                Command = command,
                ToolVersion = toolVersion
            };
            _runs.Insert(run);
            return run;
        }
    }

    public void FinishRun(RunDocument run, string? error = null)
    {
        lock (_gate)
        {
            run.FinishedUtc = DateTime.UtcNow;
            run.Error = error;
            _runs.Update(run);
        }
    }

    /// <summary>
    /// Für Fehlerpfade: Der Abschluss-Eintrag darf den eigentlichen Fehler nie verdecken.
    /// </summary>
    public void TryFinishRun(RunDocument run, string? error)
    {
        try
        {
            FinishRun(run, error);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  WARNUNG Run-Abschluss nicht gespeichert: {ex.Message}");
        }
    }

    public ItemDocument? Get(string key)
    {
        lock (_gate)
        {
            return _items.FindById(key);
        }
    }

    /// <summary>
    /// Legt ein Item an oder aktualisiert seine Quelldaten. Zurück kommt immer das
    /// persistierte Dokument, damit der Transferzustand (Imported, Attempts, ...) erhalten bleibt.
    /// </summary>
    public ItemDocument UpsertDiscovered(ItemDocument item)
    {
        lock (_gate)
        {
            var existing = _items.FindById(item.Key);
            if (existing is null)
            {
                RemoveReservedKeys(item);
                _items.Insert(item);
                return item;
            }

            existing.LastSeenUtc = DateTime.UtcNow;
            existing.ArchivePath = item.ArchivePath;

            // Ein früherer Versuch kann das Item im bisherigen Ordner gespeichert haben, und die
            // Suche vor dem erneuten Senden läuft nur im aktuellen Zielordner.
            if (existing.Attempts == 0 || existing.State == ItemState.Imported)
            {
                existing.FolderPath = item.FolderPath;
            }

            existing.ItemType = item.ItemType;
            existing.Subject = item.Subject;
            existing.SizeBytes = item.SizeBytes;

            // Mails lesen die Anzahl erst beim Export; ein neuer Durchlauf darf sie nicht löschen.
            existing.AttachmentCount = item.AttachmentCount ?? existing.AttachmentCount;
            existing.Properties = item.Properties;
            existing.ReceivedLocal = item.ReceivedLocal;
            existing.SentLocal = item.SentLocal;
            existing.IsRead = item.IsRead;
            RemoveReservedKeys(existing);
            _items.Update(existing);
            return existing;
        }
    }

    public void Save(ItemDocument item)
    {
        lock (_gate)
        {
            RemoveReservedKeys(item);
            _items.Update(item);
        }
    }

    /// <summary>Speichert alle Dokumente in einer Transaktion.</summary>
    public void SaveMany(IReadOnlyCollection<ItemDocument> items)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                RemoveReservedKeys(item);
            }

            _items.Update(items);
        }
    }

    public void MarkWalkComplete(string mailboxId, string sourcePath)
    {
        lock (_gate)
        {
            _walks.Upsert(new WalkDocument
            {
                MailboxId = mailboxId,
                SourcePath = sourcePath,
                LastCompleteWalkUtc = DateTime.UtcNow
            });
        }
    }

    public void ClearWalkComplete(string mailboxId)
    {
        lock (_gate)
        {
            _walks.Delete(mailboxId);
        }
    }

    /// <summary>Ob der letzte Lauf das Postfach aus genau dieser Quelle vollständig gelesen hat.</summary>
    public bool HasCompleteWalk(string mailboxId, string sourcePath)
    {
        lock (_gate)
        {
            return _walks.FindById(mailboxId) is { } walk &&
                   string.Equals(walk.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Imported-Zeilen, die in ein anderes Ziel gingen, z. B. nur in die lokale Ausgabe.</summary>
    public int CountImportedElsewhere(string mailboxId, string targetName)
    {
        lock (_gate)
        {
            // Ohne ImportedTo stammt die Zeile aus einem Lauf, der das Ziel noch nicht vermerkte.
            return _items.Count(x =>
                x.MailboxId == mailboxId &&
                x.State == ItemState.Imported &&
                x.ImportedTo != null &&
                x.ImportedTo != targetName);
        }
    }

    public IReadOnlyList<ItemDocument> GetAll(string? mailboxId = null)
    {
        lock (_gate)
        {
            return string.IsNullOrWhiteSpace(mailboxId)
                ? _items.FindAll().ToList()
                : _items.Find(x => x.MailboxId == mailboxId).ToList();
        }
    }

    public IReadOnlyList<ItemDocument> GetByState(ItemState state, string? mailboxId = null)
    {
        lock (_gate)
        {
            return string.IsNullOrWhiteSpace(mailboxId)
                ? _items.Find(x => x.State == state).ToList()
                : _items.Find(x => x.State == state && x.MailboxId == mailboxId).ToList();
        }
    }

    public ManifestSummary GetSummary(string? mailboxId = null)
    {
        lock (_gate)
        {
            int Count(ItemState state) => string.IsNullOrWhiteSpace(mailboxId)
                ? _items.Count(x => x.State == state)
                : _items.Count(x => x.State == state && x.MailboxId == mailboxId);

            return new ManifestSummary
            {
                Discovered = Count(ItemState.Discovered),
                Exported = Count(ItemState.Exported),
                Importing = Count(ItemState.Importing),
                Imported = Count(ItemState.Imported),
                Failed = Count(ItemState.Failed),
                Skipped = Count(ItemState.Skipped)
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Ein Fehler beim Abschluss darf weder einen laufenden Fehler überdecken noch
            // die Datei gesperrt lassen. Nicht übernommene Daten stehen im Log und werden
            // beim nächsten Öffnen wiederhergestellt.
            try
            {
                _database.Checkpoint();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  WARNUNG Manifest-Checkpoint: {ex.Message}");
            }

            try
            {
                _database.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  WARNUNG Manifest schließen: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// LiteDB 5.0.21 schreibt einen verschachtelten Schlüssel "_ID" doppelt, zählt ihn aber
    /// nur einmal. Das endet in Fehler 999 und schließt die Engine. David liefert genau diesen
    /// Schlüssel; die ID steht ohnehin in <see cref="ItemDocument.DavidId"/>.
    /// </summary>
    private static void RemoveReservedKeys(ItemDocument item)
    {
        foreach (var key in item.Properties.Keys
                     .Where(key => key.Equals("_id", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            item.Properties.Remove(key);
        }
    }
}
