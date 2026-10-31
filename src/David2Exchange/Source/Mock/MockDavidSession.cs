// SPDX-License-Identifier: MIT
using System.Text;

namespace David2Exchange.Source.Mock;

public sealed class MockDavidSession : IDavidSession
{
    private readonly Dictionary<string, IDavidArchive> _archives;

    public MockDavidSession()
    {
        var user = new MockArchive(@"user\00000003", "00000003");
        var inbox = new MockArchive(@"user\00000003\Eingang", "Eingang");
        var sent = new MockArchive(@"user\00000003\Ausgang", "Ausgang");
        var fax = new MockArchive(@"user\00000003\Eingang\Fax", "Fax");

        inbox.Add(
            new MockItem("00000001", "mail", "Willkommen bei David", 12_400,
                new DateTime(2024, 3, 4, 8, 12, 0, DateTimeKind.Unspecified), true),
            new MockItem("00000002", "mail", "Rechnung 2024-0042", 88_100,
                new DateTime(2024, 3, 5, 9, 30, 0, DateTimeKind.Unspecified), false),
            new MockItem("00000003", "fax", "Fax von +49 9971 123456", 240_000,
                new DateTime(2024, 3, 6, 10, 0, 0, DateTimeKind.Unspecified), true));

        fax.Add(new MockItem("00000004", "fax", "Fax Anlage 3 Seiten", 500_000,
            new DateTime(2024, 3, 6, 10, 2, 0, DateTimeKind.Unspecified), true));
        inbox.Add(fax);

        sent.Add(new MockItem("00000005", "mail", "AW: Rechnung 2024-0042", 44_200,
            new DateTime(2024, 3, 5, 11, 0, 0, DateTimeKind.Unspecified), true));

        user.Add(inbox, sent);
        user.AddItem(new MockItem("00000006", "calendar", "Projekttermin", 2_000,
            new DateTime(2024, 3, 7, 14, 0, 0, DateTimeKind.Unspecified), true));

        var common = new MockArchive(@"common\unverteilt", "Unverteilt");
        var commonInbox = new MockArchive(@"common\unverteilt\Eingang", "Eingang");
        commonInbox.Add(
            new MockItem("U0000001", "mail", "Bewerbung – Eingang", 120_000,
                new DateTime(2024, 3, 8, 7, 45, 0, DateTimeKind.Unspecified), false),
            new MockItem("U0000002", "sms", "SMS von +49 171 1234567", 1_200,
                new DateTime(2024, 3, 8, 8, 0, 0, DateTimeKind.Unspecified), true));
        common.Add(commonInbox);

        _archives = new Dictionary<string, IDavidArchive>(StringComparer.OrdinalIgnoreCase)
        {
            [user.RequestedPath] = user,
            [common.RequestedPath] = common
        };
    }

    public string ProgId => "mock";

    public void Logon(string logon)
    {
    }

    public IDavidArchive OpenArchive(string path)
    {
        if (path.Equals(@"user\@me", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(@"user\me", StringComparison.OrdinalIgnoreCase))
        {
            path = @"user\00000003";
        }

        if (_archives.TryGetValue(path, out var archive))
        {
            return archive;
        }

        throw new InvalidOperationException(
            $"Mock-Archiv '{path}' unbekannt. Vorhanden: {string.Join(", ", _archives.Keys)}");
    }

    public IReadOnlyList<DavidArchiveDescriptor> ListArchives(int maxDepth = 2)
    {
        return new[]
        {
            new DavidArchiveDescriptor(@"user\00000003", "00000003", "USER", 0, "user01@example.com"),
            new DavidArchiveDescriptor(@"user\00000003\Eingang", "Eingang", "USER", 1, "user01@example.com"),
            new DavidArchiveDescriptor(@"user\00000003\Ausgang", "Ausgang", "USER", 1, "user01@example.com"),
            new DavidArchiveDescriptor(@"common\unverteilt", "Unverteilt", "COMMON", 0)
        };
    }

    public IReadOnlyList<string> GetMemberInventory()
    {
        return new[]
        {
            "Mock: Logon",
            "Mock: OpenArchive(path)",
            "Mock: Archives",
            "Mock: AllItems",
            "Mock: DVEmlFromMailItem (simuliert)"
        };
    }

    public void Dispose()
    {
    }

    private sealed class MockArchive : IDavidArchive
    {
        private readonly List<IDavidArchive> _subArchives = new();
        private readonly List<IDavidItem> _items = new();

        public MockArchive(string path, string name)
        {
            RequestedPath = path;
            Name = name;
        }

        public string RequestedPath { get; }

        public string ResolvedPath => RequestedPath;

        public string Name { get; }

        public int? DeclaredItemCount => _items.Count;

        public IEnumerable<IDavidArchive> SubArchives => _subArchives;

        public IEnumerable<IDavidItem> Items => _items;

        public void Add(params IDavidArchive[] archives)
        {
            _subArchives.AddRange(archives);
        }

        public void AddItem(IDavidItem item)
        {
            _items.Add(item);
        }

        public void Add(params IDavidItem[] items)
        {
            _items.AddRange(items);
        }
    }

    private sealed class MockItem : IDavidItem
    {
        public MockItem(
            string id,
            string type,
            string subject,
            long sizeBytes,
            DateTime receivedLocal,
            bool isRead)
        {
            Id = id;
            Type = type;
            Subject = subject;
            SizeBytes = sizeBytes;
            ReceivedLocal = receivedLocal;
            SentLocal = receivedLocal.AddMinutes(1);
            IsRead = isRead;
            Properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_ID"] = id,
                ["Type"] = type,
                ["Subject"] = subject,
                ["Size"] = sizeBytes.ToString(),
                ["Received"] = receivedLocal.ToString("O"),
                ["Read"] = isRead ? "true" : "false",
                ["ArchivePath"] = $@"user\00000003\{type}"
            };
        }

        public string Id { get; }

        public string Type { get; }

        public string? Subject { get; }

        public long? SizeBytes { get; }

        public DateTime? ReceivedLocal { get; }

        public DateTime? SentLocal { get; }

        public bool? IsRead { get; }

        public IReadOnlyDictionary<string, string?> Properties { get; }

        public bool TryExportEml(string path, out string? error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var body = $"""
                    From: "David Mock" <mock@example.com>
                    To: user01@example.com
                    Subject: {Subject}
                    Date: {ReceivedLocal:O}
                    Message-ID: <mock-{Id}@example.com>
                    MIME-Version: 1.0
                    Content-Type: text/plain; charset=utf-8

                    Mock-Item {Id} vom Typ {Type}.
                    """;
                File.WriteAllText(path, body, new UTF8Encoding(false));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
