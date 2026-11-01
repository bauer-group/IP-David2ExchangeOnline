// SPDX-License-Identifier: MIT
namespace David2Exchange.Source.David;

/// <summary>
/// DvArchiveTypes-Werte aus DVAPI32. Die Ordneranzeigenamen sind lokalisiert,
/// die Typnummern nicht. Deshalb wird der Exchange-Zielordner über den Typ
/// und WellKnownFolderName bestimmt, nicht über den Anzeigenamen.
/// </summary>
internal static class DavidArchiveTypes
{
    public static string ToFolderKind(int? archiveType)
    {
        return archiveType switch
        {
            101 => "inbox",          // DvArchivePersonalIn
            102 => "sentitems",      // DvArchivePersonalOut
            104 => "contacts",       // DvArchivePersonalContacts
            105 => "drafts",         // DvArchivePersonalDrafts
            106 => "contacts",       // DvArchivePersonalAddress
            107 => "calendar",       // DvArchivePersonalCalendar
            109 => "tasks",          // DvArchivePersonalToDo
            112 => "deleteditems",   // DvArchivePersonalTrash
            116 => "junkemail",      // DvArchivePersonalSpam
            17 => "junkemail",       // DvArchiveCommonSpam
            _ => ""
        };
    }
}
