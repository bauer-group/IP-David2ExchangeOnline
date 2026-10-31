// SPDX-License-Identifier: MIT
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace David2Exchange.Infrastructure;

internal static class ComHelpers
{
    private const BindingFlags GetFlags =
        BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance;

    private const BindingFlags InvokeFlags =
        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance;

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
                if (entry is not null)
                {
                    yield return entry;
                }
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
            return date;
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
                return parsed;
            }
        }

        try
        {
            // OLE-Automation-Datum (double).
            var oaDate = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return DateTime.FromOADate(oaDate);
        }
        catch (Exception)
        {
            return null;
        }
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
