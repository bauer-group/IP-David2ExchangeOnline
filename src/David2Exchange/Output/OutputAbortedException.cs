// SPDX-License-Identifier: MIT
namespace David2Exchange.Output;

/// <summary>
/// Das Ziel ist nicht nutzbar, weitere Items würden denselben Fehler liefern. Gilt für das
/// aktuelle Zielpostfach (z. B. Quota) oder den ganzen Lauf (z. B. EWS für den Tenant gesperrt).
/// </summary>
public sealed class OutputAbortedException : Exception
{
    public OutputAbortedException(string message, bool affectsAllMailboxes = true, Exception? innerException = null)
        : base(message, innerException)
    {
        AffectsAllMailboxes = affectsAllMailboxes;
    }

    public bool AffectsAllMailboxes { get; }
}
