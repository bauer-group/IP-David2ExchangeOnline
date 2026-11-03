// SPDX-License-Identifier: MIT
namespace David2Exchange.Manifest;

public enum ItemState
{
    Discovered,
    Exported,
    Importing,
    Imported,
    Failed,
    Skipped,

    /// <summary>Nach einem vollständigen Lesen nicht mehr in der Quelle; blockiert verify nicht.</summary>
    NotInSource
}
