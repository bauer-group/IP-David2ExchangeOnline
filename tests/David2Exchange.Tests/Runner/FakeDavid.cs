// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using David2Exchange.Source;

namespace David2Exchange.Tests.Runner;

/// <summary>
/// David-Quelle mit den am echten System beobachteten Eigenschaften: _ID ist die Position im
/// Archiv, die Datei in TextSource ist stabil, jeder Zugriff muss vom selben STA-Thread kommen.
/// </summary>
internal sealed class FakeDavid : IDavidSession
{
    private readonly Dictionary<string, FakeArchive> _archives = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<int, ApartmentState> Threads { get; } = new();

    public string ProgId => "fake";

    public string? ServerName => "fake";

    public string? DavidRoot => "fake";

    public void Touch()
    {
        Threads[Environment.CurrentManagedThreadId] = Thread.CurrentThread.GetApartmentState();
    }

    public FakeArchive AddRoot(string path, string key = "")
    {
        var archive = new FakeArchive(this, path, key.Length > 0 ? key : path[(path.LastIndexOf('\\') + 1)..]);
        _archives[path] = archive;
        return archive;
    }

    public void Logon(string logon)
    {
        Touch();
    }

    public IDavidArchive OpenArchive(string path)
    {
        Touch();
        return _archives[path];
    }

    public IReadOnlyList<DavidArchiveDescriptor> ListArchives(int maxDepth = 2) => Array.Empty<DavidArchiveDescriptor>();

    public IReadOnlyList<string> GetMemberInventory() => Array.Empty<string>();

    public void Dispose()
    {
    }
}

internal sealed class FakeArchive : IDavidArchive
{
    private readonly FakeDavid _david;
    private readonly List<FakeArchive> _children = new();
    private readonly List<FakeItem> _items = new();

    public FakeArchive(FakeDavid david, string path, string key, string? name = null)
    {
        _david = david;
        RequestedPath = path;
        ResolvedPath = path;
        FolderKey = key;
        Name = name ?? key;
    }

    public string RequestedPath { get; }

    public string ResolvedPath { get; }

    public string Name { get; }

    public string FolderKey { get; }

    public int? ArchiveType => null;

    public string FolderKind => "";

    public int? DeclaredItemCount => _items.Count;

    /// <summary>Simuliert einen COM-Fehler auf Archivebene, z. B. fehlende Rechte.</summary>
    public Exception? ItemsFailure { get; set; }

    public Exception? SubArchivesFailure { get; set; }

    public int UnpackCalls { get; private set; }

    public IEnumerable<IDavidArchive> SubArchives
    {
        get
        {
            _david.Touch();
            return SubArchivesFailure is null ? _children : throw SubArchivesFailure;
        }
    }

    public IEnumerable<IDavidItem> Items
    {
        get
        {
            if (ItemsFailure is not null)
            {
                throw ItemsFailure;
            }

            // Neueste zuerst und positionsbasierte IDs, wie von David geliefert.
            for (var index = _items.Count - 1; index >= 0; index--)
            {
                _david.Touch();
                var item = _items[index];
                item.Position = index;
                yield return item;
            }
        }
    }

    public FakeArchive Sub(string key, string? name = null)
    {
        var child = new FakeArchive(_david, ResolvedPath + "\\" + key, key, name);
        _children.Add(child);
        return child;
    }

    /// <summary>Verknüpftes Archiv außerhalb dieses Baums, z. B. resource\hol.</summary>
    public FakeArchive Link(string path, string key)
    {
        var child = new FakeArchive(_david, path, key);
        _children.Add(child);
        return child;
    }

    public FakeItem Add(string type, string file, string subject, Action<FakeItem>? configure = null)
    {
        var item = new FakeItem(_david, this, type, file, subject);
        configure?.Invoke(item);
        _items.Add(item);
        return item;
    }

    public void AddMails(string prefix, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Add("2", $"{prefix}{i:X4}", $"{prefix}-{i}");
        }
    }

    /// <summary>Entfernt ein Item; alle jüngeren rücken eine Position nach, wie in David.</summary>
    public void RemoveAt(int position)
    {
        _items.RemoveAt(position);
    }

    public bool TrySetUnpacked(out string? error)
    {
        UnpackCalls++;
        error = null;
        return true;
    }
}

internal sealed class FakeItem : IDavidItem
{
    private readonly FakeDavid _david;
    private readonly FakeArchive _archive;
    private readonly Dictionary<string, string?> _properties = new(StringComparer.OrdinalIgnoreCase);

    public FakeItem(FakeDavid david, FakeArchive archive, string type, string file, string subject)
    {
        _david = david;
        _archive = archive;
        Type = type;
        File = file;
        Subject = subject;
        _properties["TextSource"] = $@"\\srv\david\archive\{archive.ResolvedPath}\I[{file}.001";
        _properties["Received"] = archive.FolderKey.Equals("out", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
        _properties["SendTime"] = null;
    }

    public int Position { get; set; }

    public string File { get; }

    public string Id
    {
        get
        {
            _david.Touch();
            return Position.ToString();
        }
    }

    public string Type { get; }

    public string? Subject { get; set; }

    public long? SizeBytes => 100;

    public DateTime? ReceivedLocal { get; set; } = new DateTime(2024, 7, 1, 14, 30, 0, DateTimeKind.Unspecified);

    public DateTime? SentLocal { get; set; }

    public bool? IsRead { get; set; } = false;

    public int PropertyReads { get; private set; }

    public string? SourceFile => _properties.GetValueOrDefault("TextSource");

    public bool? IsReceived => bool.TryParse(_properties.GetValueOrDefault("Received"), out var value) ? value : null;

    public int? AttachmentCount { get; set; }

    public static string NativeErrorText { get; set; } = "Native export failed";

    public bool ThrowOnProperties { get; set; }

    public IReadOnlyDictionary<string, string?> Properties
    {
        get
        {
            _david.Touch();
            PropertyReads++;
            return ThrowOnProperties
                ? throw new InvalidOperationException("COM-Lesefehler")
                : _properties;
        }
    }

    public List<DavidAttachmentInfo> AttachmentList { get; } = new();

    public IReadOnlyList<DavidAttachmentInfo> Attachments => AttachmentList;

    public byte[]? FaxImage { get; set; }

    public string? FaxImagePath { get; set; }

    public Func<string, bool>? NativeExport { get; set; }

    public void Set(string name, string? value)
    {
        _properties[name] = value;
    }

    public bool TryExportEml(string path, out string? error)
    {
        _david.Touch();
        if (Type != "2")
        {
            throw new AccessViolationException("DVEmlFromMailItem darf nur für echte Mails laufen.");
        }

        if (NativeExport is not null)
        {
            error = NativeErrorText;
            return NativeExport(path);
        }

        error = null;
        System.IO.File.WriteAllText(
            path,
            $"Message-ID: <{File}@test>\r\nSubject: {Subject}\r\nDate: Mon, 01 Jul 2024 14:30:00 +0200\r\n\r\nInhalt von {_archive.FolderKey}\r\n");
        return true;
    }

    public bool TryCopyAttachment(
        DavidAttachmentInfo attachment,
        string directory,
        out string? copiedPath,
        out string? error)
    {
        _david.Touch();
        copiedPath = Path.Combine(directory, attachment.FileName);
        System.IO.File.WriteAllBytes(copiedPath, new[] { (byte)attachment.Index, (byte)7 });
        error = null;
        return true;
    }
}
