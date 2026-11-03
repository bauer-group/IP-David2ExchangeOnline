// SPDX-License-Identifier: MIT
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace David2Exchange.Infrastructure;

internal static class ComHelpers
{
    // DoNotWrapExceptions: Fehlertexte nennen die echte COM-Ursache statt
    // "Exception has been thrown by the target of an invocation".
    private const BindingFlags GetFlags =
        BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DoNotWrapExceptions;

    private const BindingFlags InvokeFlags =
        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DoNotWrapExceptions;

    private const BindingFlags SetFlags =
        BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DoNotWrapExceptions;

    /// <summary>David meldet so u. a. ein unbekanntes Feld und Attachments auf Fax und Link.</summary>
    public const int EFail = unchecked((int)0x80004005);

    public static object? GetProperty(object target, string name)
    {
        return target.GetType().InvokeMember(name, GetFlags, null, target, null);
    }

    public static bool TryGetProperty(object? target, string name, out object? value)
    {
        value = null;
        if (target is null)
        {
            return false;
        }

        try
        {
            value = GetProperty(target, name);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Für Pflichtdaten: Nur ein fehlendes Member gilt als "nicht vorhanden". Jeder andere
    /// COM-Fehler wird weitergereicht, statt den Migrationsumfang still zu verkleinern.
    /// </summary>
    public static bool TryGetRequiredProperty(object target, string name, out object? value)
    {
        value = null;
        try
        {
            value = GetProperty(target, name);
            return true;
        }
        catch (Exception ex) when (IsMissingMember(ex))
        {
            return false;
        }
    }

    private static bool IsMissingMember(Exception exception)
    {
        return exception is MissingMemberException ||
               exception.HResult is unchecked((int)0x80020006) or unchecked((int)0x80020003);
    }

    /// <summary>
    /// Für Daten, die ins Ziel gehen oder die Identität bestimmen. Am echten David melden nicht
    /// vorhandene Member durchgehend DISP_E_UNKNOWNNAME; jeder andere Fehler ist ein Lesefehler.
    /// </summary>
    public static string? GetRequiredString(object target, params string[] names)
    {
        return RequiredValues(target, names)
            .Select(ToScalarString)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    public static long? GetRequiredLong(object target, params string[] names)
    {
        return RequiredValues(target, names).Select(ToNullableLong).FirstOrDefault(value => value.HasValue);
    }

    public static DateTime? GetRequiredDate(object target, params string[] names)
    {
        return RequiredValues(target, names).Select(ToNullableDate).FirstOrDefault(value => value.HasValue);
    }

    public static bool? GetRequiredBool(object target, params string[] names)
    {
        return RequiredValues(target, names).Select(ToNullableBool).FirstOrDefault(value => value.HasValue);
    }

    private static IEnumerable<object> RequiredValues(object target, string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetRequiredProperty(target, name, out var value) && value is not null)
            {
                yield return value;
            }
        }
    }

    public static void SetProperty(object target, string name, object? value)
    {
        target.GetType().InvokeMember(name, SetFlags, null, target, new[] { value });
    }

    public static object? Invoke(object target, string name, params object?[] args)
    {
        return target.GetType().InvokeMember(name, InvokeFlags, null, target, args);
    }

    public static bool TryInvoke(object? target, string name, out object? value, params object?[] args)
    {
        value = null;
        if (target is null)
        {
            return false;
        }

        try
        {
            value = Invoke(target, name, args);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Für den Migrationsumfang: Eine nicht lesbare Collection oder Lücke wird zum Fehler und
    /// verkleinert den Umfang nicht still. Alle David-Collections bieten laut Typbibliothek _NewEnum.
    /// </summary>
    public static IEnumerable<object> EnumerateRequired(object? collection)
    {
        if (collection is null)
        {
            yield break;
        }

        if (collection is not IEnumerable enumerable)
        {
            // Der Cast verschluckt den HRESULT von _NewEnum; der direkte Aufruf zeigt den echten Fehler.
            GetProperty(collection, "_NewEnum");
            throw new InvalidOperationException("David-Collection lässt sich nicht aufzählen.");
        }

        var declared = GetCount(collection);
        var delivered = 0;
        foreach (var entry in enumerable)
        {
            // Ein fehlschlagendes IEnumVARIANT::Next erscheint hier als null, und zwar endlos.
            if (entry is null)
            {
                throw new InvalidOperationException($"David-Collection lieferte nach {delivered} Einträgen einen leeren Eintrag.");
            }

            delivered++;
            yield return entry;
        }

        if (declared is { } expected && delivered < expected)
        {
            throw new InvalidOperationException($"David-Collection lieferte nur {delivered} von {expected} Einträgen.");
        }
    }

    public static IEnumerable<object> Enumerate(object? collection)
    {
        if (collection is null)
        {
            yield break;
        }

        if (collection is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                // Ein fehlschlagendes IEnumVARIANT::Next erscheint hier als null, und zwar endlos.
                if (entry is null)
                {
                    Console.Error.WriteLine("  WARNUNG COM-Collection vorzeitig beendet: leerer Eintrag.");
                    yield break;
                }

                yield return entry;
            }

            yield break;
        }

        if (!TryGetProperty(collection, "Count", out var countValue) || countValue is null)
        {
            yield break;
        }

        var count = ToInt(countValue);
        if (count <= 0)
        {
            yield break;
        }

        // COM-Collections sind meist 1-basiert, können aber 0-basiert sein.
        var zeroBased = TryGetItemAt(collection, 0, out _);
        for (var offset = 0; offset < count; offset++)
        {
            var index = zeroBased ? offset : offset + 1;
            if (TryGetItemAt(collection, index, out var item))
            {
                yield return item;
            }
            else
            {
                Console.Error.WriteLine($"  WARNUNG COM-Eintrag {index} von {count} nicht lesbar und übersprungen.");
            }
        }
    }

    private static bool TryGetItemAt(object collection, int index, out object item)
    {
        item = null!;

        try
        {
            var value = Invoke(collection, "Item", index);
            if (value is not null)
            {
                item = value;
                return true;
            }
        }
        catch (Exception)
        {
            // Kein Treffer.
        }

        return false;
    }

    public static string? GetFirstString(object target, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(target, name, out var value) && value is not null)
            {
                var text = ToScalarString(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    public static long? GetFirstLong(object target, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(target, name, out var value) && value is not null)
            {
                var number = ToNullableLong(value);
                if (number.HasValue)
                {
                    return number;
                }
            }
        }

        return null;
    }

    public static DateTime? GetFirstDate(object target, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(target, name, out var value) && value is not null)
            {
                var date = ToNullableDate(value);
                if (date.HasValue)
                {
                    return date;
                }
            }
        }

        return null;
    }

    public static bool? GetFirstBool(object target, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(target, name, out var value) && value is not null)
            {
                var flag = ToNullableBool(value);
                if (flag.HasValue)
                {
                    return flag;
                }
            }
        }

        return null;
    }

    public static int? GetCount(object? collection)
    {
        if (collection is null)
        {
            return null;
        }

        if (TryGetProperty(collection, "Count", out var value) && value is not null)
        {
            return ToInt(value);
        }

        return null;
    }

    public static string? ToScalarString(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    public static long? ToNullableLong(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static int ToInt(object value)
    {
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static DateTime? ToNullableDate(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is DateTime date)
        {
            return WithoutOleZero(date);
        }

        if (value is bool)
        {
            return null;
        }

        if (value is string text)
        {
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return WithoutOleZero(parsed);
            }
        }

        try
        {
            // OLE-Automation-Datum (double).
            var oaDate = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return WithoutOleZero(DateTime.FromOADate(oaDate));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// David liefert für "nicht gesetzt" den OLE-Nullwert 30.12.1899 (z. B. SendTime).
    /// Als echtes Datum übernommen stünde er sonst als Sendezeit in Exchange.
    /// </summary>
    private static DateTime? WithoutOleZero(DateTime value)
    {
        return value.Year < 1900 ? null : value;
    }

    public static bool? ToNullableBool(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is bool flag)
        {
            return flag;
        }

        if (value is string text)
        {
            if (bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            if (text is "1" or "0")
            {
                return text == "1";
            }
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> GetMemberInventory(object? target)
    {
        if (target is null)
        {
            return Array.Empty<string>();
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var member in target.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                names.Add($"{member.MemberType}: {member.Name}");
            }
        }
        catch (Exception)
        {
            // COM-Typen liefern oft keine Reflection-Metadaten.
        }

        try
        {
            foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(target))
            {
                names.Add($"Property: {property.Name}");
            }
        }
        catch (Exception)
        {
            // Optional.
        }

        return names.ToArray();
    }

    public static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (Exception)
        {
            // Beim Beenden nicht kritisch.
        }
    }
}
