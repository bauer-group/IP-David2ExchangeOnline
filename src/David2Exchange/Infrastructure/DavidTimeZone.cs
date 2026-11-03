// SPDX-License-Identifier: MIT
using David2Exchange.Cli;

namespace David2Exchange.Infrastructure;

internal static class DavidTimeZone
{
    /// <summary>
    /// Eine falsche Zeitzone verschiebt jedes Datum im Ziel und fällt danach nicht mehr auf.
    /// Deshalb gibt es keinen stillen Rückfall auf die Zeit des ausführenden Rechners.
    /// </summary>
    public static TimeZoneInfo Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CliException("david.timezone fehlt. Beispiel: W. Europe Standard Time.");
        }

        // IANA-Namen löst .NET unter Windows nur mit ICU auf; der Standardwert muss überall gelten.
        var name = id.Trim();
        if (name.Equals("Europe/Berlin", StringComparison.OrdinalIgnoreCase))
        {
            name = "W. Europe Standard Time";
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new CliException(
                $"david.timezone '{id}' ist keine gültige Zeitzone. Beispiel: W. Europe Standard Time.");
        }
    }
}
