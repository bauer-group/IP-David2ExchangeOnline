// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Identity.Client;

namespace David2Exchange.Output;

internal enum EwsFailureKind
{
    /// <summary>Der Server hat nichts verarbeitet; einfach später wiederholen.</summary>
    Transient,

    /// <summary>Ausgang unbekannt: vor dem erneuten Senden im Ziel nachsehen.</summary>
    Ambiguous,

    /// <summary>Betrifft nur dieses Item, ein neuer Versuch hilft nicht.</summary>
    Permanent,

    /// <summary>Betrifft das ganze Zielpostfach (z. B. Quota, Impersonation).</summary>
    AbortMailbox,

    /// <summary>Betrifft den ganzen Lauf (z. B. Anmeldung, EWS für den Tenant gesperrt).</summary>
    AbortRun
}

/// <summary>
/// Einordnung von EWS-Fehlern. Grundlage sind die Fehlerpfade der EWS Managed API und
/// Microsoft Learn "EWS throttling in Exchange" sowie "Control access to EWS in Exchange".
/// </summary>
internal static partial class EwsFailure
{
    public const int Unauthorized = 401;
    private const int Forbidden = 403;
    private const string BackOffKey = "BackOffMilliseconds";

    public static EwsFailureKind Classify(ServiceError code)
    {
        return code switch
        {
            ServiceError.ErrorServerBusy or
            ServiceError.ErrorExceededConnectionCount or
            ServiceError.ErrorInsufficientResources or
            ServiceError.ErrorTooManyObjectsOpened or
            ServiceError.ErrorMailboxStoreUnavailable or
            ServiceError.ErrorMailboxMoveInProgress or
            ServiceError.ErrorMailboxFailover or
            ServiceError.ErrorInternalServerTransientError or
            ServiceError.ErrorBatchProcessingStopped or
            ServiceError.ErrorADUnavailable or
            ServiceError.ErrorConnectionFailed => EwsFailureKind.Transient,

            ServiceError.ErrorTimeoutExpired or
            ServiceError.ErrorInternalServerError or
            ServiceError.ErrorADOperation or
            ServiceError.ErrorNoApplicableProxyCASServersAvailable or
            ServiceError.ErrorProxyServiceDiscoveryFailed => EwsFailureKind.Ambiguous,

            ServiceError.ErrorQuotaExceeded or
            ServiceError.ErrorAccessDenied or
            ServiceError.ErrorAccountDisabled or
            ServiceError.ErrorImpersonationDenied or
            ServiceError.ErrorImpersonateUserDenied or
            ServiceError.ErrorImpersonationFailed or
            ServiceError.ErrorNonExistentMailbox or
            ServiceError.ErrorMailboxLogonFailed => EwsFailureKind.AbortMailbox,

            _ => EwsFailureKind.Permanent
        };
    }

    public static EwsFailureKind Classify(Exception exception)
    {
        switch (exception)
        {
            case ServiceResponseException response:
                return Classify(response.ErrorCode);

            case ServiceRequestException request:
                return ClassifyRequest(request);

            // Scheitert schon beim Aufbau der Anfrage, ein neuer Versuch ändert nichts.
            case PropertyException:
            case ServiceValidationException:
            case ServiceVersionException:
            case ServiceXmlSerializationException:
                return EwsFailureKind.Permanent;

            // Ein unbekannter Fehler sagt nichts darüber, was der Server getan hat. Das gilt auch für
            // eine abgeschnittene Antwort, die als XmlException ankommt, und für unbekannte Codes.
            default:
                return EwsFailureKind.Ambiguous;
        }
    }

    /// <summary>
    /// Die Anfrage kann beim Server angekommen sein, ohne dass er sie zu Ende bearbeitet hat. Eine
    /// vollständig gelesene, aber unbrauchbare Antwort gehört nicht dazu: Dann ist er bereits fertig.
    /// </summary>
    public static bool IsTransportFailure(Exception exception)
    {
        return exception is ServiceRequestException or HttpRequestException or TimeoutException or IOException or OperationCanceledException;
    }

    /// <summary>Anmeldung vorübergehend nicht möglich; alles andere (z. B. falsches Zertifikat) bleibt fatal.</summary>
    public static bool IsTransientTokenFailure(Exception exception)
    {
        return exception is MsalException { IsRetryable: true } or HttpRequestException or TimeoutException;
    }

    public static int? HttpStatus(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var match = HttpStatusText().Match(current.Message);
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    public static TimeSpan? ServerBackOff(Exception exception)
    {
        return exception switch
        {
            ServerBusyException { BackOffMilliseconds: > 0 } busy => TimeSpan.FromMilliseconds(busy.BackOffMilliseconds),
            ServiceResponseException { Response: not null } response => ServerBackOff(response.Response),
            _ => null
        };
    }

    public static TimeSpan? ServerBackOff(ServiceResponse response)
    {
        return response.ErrorDetails is { } details &&
               details.TryGetValue(BackOffKey, out var text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) &&
               milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
    }

    private static EwsFailureKind ClassifyRequest(ServiceRequestException exception)
    {
        switch (HttpStatus(exception))
        {
            case Unauthorized:
            case Forbidden:
                return EwsFailureKind.AbortRun;
            case 429:
                return EwsFailureKind.Transient;

            // Ein Fehler in der Anfrage selbst; z. B. wäre eine zu große Nachricht nie zustellbar.
            case 400:
            case 413:
                return EwsFailureKind.Permanent;
        }

        // Auch ein 503 ist kein Beweis: Ein Frontend kann ihn nach dem Speichern im Backend melden.
        // Nur ein nicht auflösbarer Name beweist, dass nichts gesendet wurde.
        return exception.InnerException is WebException { Status: WebExceptionStatus.NameResolutionFailure }
            ? EwsFailureKind.Transient
            : EwsFailureKind.Ambiguous;
    }

    [GeneratedRegex(@"\((\d{3})\)", RegexOptions.CultureInvariant)]
    private static partial Regex HttpStatusText();
}
