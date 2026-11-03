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
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new CliException(
                $"david.timezone '{id}' ist keine gültige Zeitzone. Beispiele: Europe/Berlin, W. Europe Standard Time.");
        }
    }
}
