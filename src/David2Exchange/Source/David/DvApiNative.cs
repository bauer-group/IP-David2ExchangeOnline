// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using System.Text;

namespace David2Exchange.Source.David;

internal static class DvApiNative
{
    // Signatur aus dem Konzept: int (nicht long), Erfolg != 0, Pfad im Puffer.
    [DllImport(
        "DvApi32.dll",
        EntryPoint = "DVEmlFromMailItem",
        ExactSpelling = true,
        CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall,
        SetLastError = true)]
    private static extern int DVEmlFromMailItem(
        [MarshalAs(UnmanagedType.IDispatch)] object mailItem,
        StringBuilder path);

    public static bool TryExport(object item, string destination, out string? error)
    {
        error = null;

        try
        {
            var buffer = new StringBuilder(260);
            var result = DVEmlFromMailItem(item, buffer);
            if (result == 0)
            {
                error = "DVEmlFromMailItem hat 0 zurückgegeben (kein Erfolg).";
                return false;
            }

            var produced = buffer.ToString().Trim('\0', ' ', '"');
            if (string.IsNullOrWhiteSpace(produced))
            {
                produced = destination;
            }

            if (!File.Exists(produced))
            {
                error = $"DVEmlFromMailItem meldete Erfolg, aber die Datei fehlt: {produced}";
                return false;
            }

            var expected = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetFullPath(produced), expected, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
                File.Move(produced, expected, overwrite: true);
            }

            return true;
        }
        catch (DllNotFoundException ex)
        {
            error = $"DvApi32.dll nicht gefunden. 32-Bit-Prozess und David-Client prüfen. ({ex.Message})";
            return false;
        }
        catch (BadImageFormatException ex)
        {
            error = $"DvApi32.dll hat das falsche Format. Der Prozess muss x86 sein. ({ex.Message})";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"DVEmlFromMailItem wurde in DvApi32.dll nicht gefunden. Signatur/Version prüfen. ({ex.Message})";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
