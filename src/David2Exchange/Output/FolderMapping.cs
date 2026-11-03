// SPDX-License-Identifier: MIT
using Microsoft.Exchange.WebServices.Data;

namespace David2Exchange.Output;

/// <summary>
/// Bildet technische David-Ordnerschlüssel (in, out, cal, ...) auf Exchange-Standardordner ab.
/// Anzeigenamen gehören bewusst nicht dazu: Sie sind lokalisiert und frei vergebbar.
/// </summary>
public static class FolderMapping
{
    public static bool TryMapRoot(string segment, out WellKnownFolderName folder)
    {
        switch (segment.ToLowerInvariant())
        {
            case "in":
            case "inbox":
                folder = WellKnownFolderName.Inbox;
                return true;
            case "out":
            case "sentitems":
                folder = WellKnownFolderName.SentItems;
                return true;
            case "drafts":
                folder = WellKnownFolderName.Drafts;
                return true;
            case "trash":
            case "deleteditems":
                folder = WellKnownFolderName.DeletedItems;
                return true;
            case "spam":
            case "junkemail":
                folder = WellKnownFolderName.JunkEmail;
                return true;
            case "cal":
            case "calendar":
                folder = WellKnownFolderName.Calendar;
                return true;
            case "address":
            case "contacts":
                folder = WellKnownFolderName.Contacts;
                return true;
            case "todo":
            case "tasks":
                folder = WellKnownFolderName.Tasks;
                return true;
            default:
                folder = WellKnownFolderName.Inbox;
                return false;
        }
    }

    public static bool IsKnownRoot(string segment)
    {
        return TryMapRoot(segment, out _);
    }

    public static bool IsSentRoot(string folderPath)
    {
        var first = folderPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return first is not null &&
               TryMapRoot(first, out var folder) &&
               folder == WellKnownFolderName.SentItems;
    }
}
