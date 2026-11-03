// SPDX-License-Identifier: MIT
using System.Reflection;
using David2Exchange.Infrastructure;

namespace David2Exchange.Source.David;

public sealed class DavidComSession : IDavidSession
{
    private readonly object _application;
    private object? _account;
    private object? _archiveRoot;

    public string? ServerName =>
        _account is null ? null : ComHelpers.GetFirstString(_account, "ServerName", "Server");

    public string? DavidRoot =>
        _account is null ? null : ComHelpers.GetFirstString(_account, "DavidRoot", "ArchiveRootPath");

    public DavidComSession(string progId)
    {
        ProgId = progId;

        var type = Type.GetTypeFromProgID(progId, throwOnError: false);
        if (type is null)
        {
            throw new DavidApiException(
                $"ProgID '{progId}' ist nicht registriert. Ist der David-Client installiert und der Prozess x86?");
        }

        try
        {
            _application = Activator.CreateInstance(type)
                ?? throw new DavidApiException($"COM-Objekt für '{progId}' konnte nicht erzeugt werden.");
        }
        catch (DavidApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DavidApiException(
                $"COM-Objekt für '{progId}' konnte nicht erzeugt werden: {ex.Message}", ex);
        }
    }

    public string ProgId { get; }

    public void Logon(string logon)
    {
        var allAttempts = new List<string>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var attempts = new List<string>();

            foreach (var arguments in ArgumentSets(logon))
            {
                try
                {
                    var account = ComHelpers.Invoke(_application, "Logon", arguments);
                    if (account is not null)
                    {
                        _account = account;
                        if (ComHelpers.TryGetProperty(_account, "ArchiveRoot", out var root))
                        {
                            _archiveRoot = root;
                        }

                        return;
                    }

                    attempts.Add($"{arguments.Length} Argumente: Logon lieferte null");
                }
                catch (Exception ex)
                {
                    attempts.Add($"{arguments.Length} Argumente: {Unwrap(ex).Message}");
                }
            }

            allAttempts.AddRange(attempts);

            if (attempt < 3 && attempts.Any(IsTransient))
            {
                Thread.Sleep(1500);
                continue;
            }

            break;
        }

        throw new DavidApiException(
            "David-Logon fehlgeschlagen. Läuft der David-Client (InfoCenter) und ist der Windows-Benutzer " +
            "dort angemeldet? Erwartet wird ein Benutzer mit David-Rechten. " +
            $"Versuche: {string.Join(" | ", allAttempts)}");
    }

    public IDavidArchive OpenArchive(string path)
    {
        if (_account is null)
        {
            throw new DavidApiException("OpenArchive ohne vorheriges Logon.");
        }

        var effectivePath = ResolveRequestedPath(path);
        var attempts = new List<string>();
        var calls = new (string Member, object?[] Arguments)[]
        {
            ("GetArchive", new object?[] { effectivePath }),
            ("GetArchive", new object?[] { effectivePath, 0 }),
            ("GetArchiveByPath", new object?[] { effectivePath }),
            ("OpenArchive", new object?[] { effectivePath })
        };

        foreach (var call in calls)
        {
            try
            {
                var archive = ComHelpers.Invoke(_account, call.Member, call.Arguments);
                if (archive is not null)
                {
                    return new DavidComArchive(effectivePath, archive);
                }

                attempts.Add($"{call.Member}/{call.Arguments.Length}: null");
            }
            catch (Exception ex)
            {
                attempts.Add($"{call.Member}/{call.Arguments.Length}: {Unwrap(ex).Message}");
            }
        }

        if (path.Contains("unverteilt", StringComparison.OrdinalIgnoreCase))
        {
            for (var special = 0; special <= 12; special++)
            {
                try
                {
                    var archive = ComHelpers.Invoke(_account, "GetSpecialArchive", special);
                    if (archive is null)
                    {
                        continue;
                    }

                    var candidate = new DavidComArchive(effectivePath, archive);
                    if (candidate.ResolvedPath.Contains("unverteilt", StringComparison.OrdinalIgnoreCase) ||
                        candidate.Name.Contains("unverteilt", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex)
                {
                    attempts.Add($"GetSpecialArchive/{special}: {Unwrap(ex).Message}");
                }
            }
        }

        if (TryFindArchiveInTree(effectivePath, out var found))
        {
            return found;
        }

        var hint = attempts.Any(attempt =>
            attempt.Contains("Insufficient rights", StringComparison.OrdinalIgnoreCase) ||
            attempt.Contains("Keine Berechtigung", StringComparison.OrdinalIgnoreCase))
            ? " Der Windows-Benutzer hat keine Rechte auf dieses Archiv, oder der Archivpfad ist falsch."
            : "";

        throw new DavidApiException(
            $"Archiv '{effectivePath}' konnte nicht geöffnet werden.{hint} " +
            $"Versuche: {string.Join(" | ", attempts)}. " +
            "Archivbaum durchsucht: " + (_archiveRoot is null ? "nein" : "ja") + ". " +
            "Diagnose: david2exchange probe --list-members ausführen.");
    }

    public IReadOnlyList<DavidArchiveDescriptor> ListArchives(int maxDepth = 2)
    {
        var result = new List<DavidArchiveDescriptor>();
        if (_archiveRoot is null)
        {
            return result;
        }

        // Der Katalog bestimmt den Migrationsumfang: Eine Lücke darf keine Benutzer still auslassen.
        if (!ComHelpers.TryGetRequiredProperty(_archiveRoot, "Archives", out var topLevel))
        {
            return result;
        }

        var emails = GetUserEmails();

        foreach (var top in ComHelpers.EnumerateRequired(topLevel))
        {
            var topPath = DavidArchivePaths.GetRelativePath(top) ?? "";
            if (string.IsNullOrWhiteSpace(topPath))
            {
                continue;
            }

            var topName = ComHelpers.GetFirstString(top, "DisplayName", "Name") ?? topPath;
            result.Add(new DavidArchiveDescriptor(topPath, topName, topPath, 0));

            if (maxDepth > 1)
            {
                WalkArchiveCatalog(top, topPath, topPath, 1, maxDepth, result, emails);
            }
        }

        return result;
    }

    private static void WalkArchiveCatalog(
        object parent,
        string parentPath,
        string kind,
        int depth,
        int maxDepth,
        List<DavidArchiveDescriptor> result,
        IReadOnlyDictionary<string, string> emails)
    {
        if (depth > maxDepth ||
            !ComHelpers.TryGetRequiredProperty(parent, "Archives", out var children))
        {
            return;
        }

        foreach (var child in ComHelpers.EnumerateRequired(children))
        {
            var path = DavidArchivePaths.GetRelativePath(child);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = parentPath.TrimEnd('\\') + "\\" +
                       (ComHelpers.GetFirstString(child, "DisplayName", "Name") ?? "sub");
            }

            var name = ComHelpers.GetFirstString(child, "DisplayName", "Name")
                       ?? DavidArchivePaths.LastSegment(path);
            emails.TryGetValue(name, out var email);
            if (string.IsNullOrWhiteSpace(email))
            {
                emails.TryGetValue(DavidArchivePaths.LastSegment(path), out email);
            }

            result.Add(new DavidArchiveDescriptor(path, name, kind, depth, email));

            WalkArchiveCatalog(child, path, kind, depth + 1, maxDepth, result, emails);
        }
    }

    private IReadOnlyDictionary<string, string> GetUserEmails()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_account is null ||
            !ComHelpers.TryGetProperty(_account, "Users", out var users))
        {
            return result;
        }

        foreach (var user in ComHelpers.Enumerate(users))
        {
            var name = ComHelpers.GetFirstString(user, "Name");
            var uid = ComHelpers.GetFirstString(user, "UID");
            string? email = null;

            if (ComHelpers.TryGetProperty(user, "Addresses", out var addresses))
            {
                foreach (var address in ComHelpers.Enumerate(addresses))
                {
                    var candidate = ComHelpers.GetFirstString(address, "EMail", "Email");
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        email = candidate.Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                result[name] = email;
            }

            if (!string.IsNullOrWhiteSpace(uid))
            {
                result[uid] = email;
            }
        }

        return result;
    }

    public IReadOnlyList<string> GetMemberInventory()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ComHelpers.GetMemberInventory(_application))
        {
            names.Add($"Application/{entry}");
        }

        foreach (var entry in ComHelpers.GetMemberInventory(_account))
        {
            names.Add($"Account/{entry}");
        }

        foreach (var entry in ComHelpers.GetMemberInventory(_archiveRoot))
        {
            names.Add($"ArchiveRoot/{entry}");
        }

        names.Add($"Type/Application: {_application.GetType().FullName}");
        if (_account is not null)
        {
            names.Add($"Type/Account: {_account.GetType().FullName}");
        }

        if (_archiveRoot is not null &&
            ComHelpers.TryGetProperty(_archiveRoot, "Archives", out var topLevel))
        {
            foreach (var child in ComHelpers.Enumerate(topLevel))
            {
                var path = DavidArchivePaths.GetRelativePath(child);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    names.Add($"ArchiveRoot/Child: {path}");
                }
            }
        }

        return names.ToArray();
    }

    public void Dispose()
    {
        ComHelpers.Release(_archiveRoot);
        ComHelpers.Release(_account);
        ComHelpers.Release(_application);
        _archiveRoot = null;
        _account = null;
    }

    private string ResolveRequestedPath(string path)
    {
        if (path.Equals(@"user\@me", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(@"user\me", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("@me", StringComparison.OrdinalIgnoreCase))
        {
            return GetCurrentUserArchivePath()
                   ?? throw new DavidApiException(
                       "Das eigene Benutzerarchiv konnte nicht ermittelt werden (Account.User.ID fehlt). " +
                       "Bitte den Pfad explizit angeben, z. B. user\\1000C000.");
        }

        return path;
    }

    private string? GetCurrentUserArchivePath()
    {
        if (_account is null ||
            !ComHelpers.TryGetProperty(_account, "User", out var user) ||
            user is null)
        {
            return null;
        }

        var id = ComHelpers.GetFirstLong(user, "ID", "UserID");
        if (id.HasValue)
        {
            return $"user\\{id.Value:X8}";
        }

        var name = ComHelpers.GetFirstString(user, "Name", "UserName");
        return string.IsNullOrWhiteSpace(name) ? null : $"user\\{name}";
    }

    private bool TryFindArchiveInTree(string requested, out IDavidArchive archive)
    {
        archive = null!;
        if (_archiveRoot is null)
        {
            return false;
        }

        var segments = DavidArchivePaths.Normalize(requested)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        object? current = _archiveRoot;
        foreach (var segment in segments)
        {
            if (current is null)
            {
                return false;
            }

            if (ComHelpers.TryInvoke(current, "GetSubArchive", out var next, segment) && next is not null)
            {
                current = next;
                continue;
            }

            current = FindDirectChild(current, segment);
        }

        if (current is null)
        {
            return false;
        }

        archive = new DavidComArchive(requested, current);
        return true;
    }

    private static object? FindDirectChild(object parent, string segment)
    {
        if (!ComHelpers.TryGetProperty(parent, "Archives", out var children))
        {
            return null;
        }

        foreach (var child in ComHelpers.Enumerate(children))
        {
            var relative = DavidArchivePaths.GetRelativePath(child) ?? "";
            var displayName = ComHelpers.GetFirstString(child, "DisplayName", "Name", "ArchiveName") ?? "";

            if (DavidArchivePaths.Matches(relative, segment) ||
                string.Equals(DavidArchivePaths.LastSegment(relative), segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(displayName, segment, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    private static object?[][] ArgumentSets(string logon)
    {
        return new[]
        {
            new object?[] { "", "", "", "", "", logon },
            new object?[] { "", "", "", "", logon },
            new object?[] { "", "", "", "", "", logon, 0 },
            new object?[] { logon }
        };
    }

    private static bool IsTransient(string message)
    {
        return message.Contains("OLE_E_CANT_GETMONIKER", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("OLE_E_BLANK", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Moniker", StringComparison.OrdinalIgnoreCase);
    }

    private static Exception Unwrap(Exception exception)
    {
        if (exception is TargetInvocationException { InnerException: not null } invocation)
        {
            return invocation.InnerException!;
        }

        return exception;
    }
}
