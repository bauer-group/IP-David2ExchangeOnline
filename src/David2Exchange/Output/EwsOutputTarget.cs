// SPDX-License-Identifier: MIT
using System.Security.Cryptography.X509Certificates;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Identity.Client;
using Task = System.Threading.Tasks.Task;

namespace David2Exchange.Output;

/// <summary>
/// Echte Exchange-Online-Ausgabe über EWS app-only (Zertifikat, Impersonation).
/// Kein Mock: Der Adapter spricht outlook.office365.com.
/// </summary>
public sealed class EwsOutputTarget : IOutputTarget
{
    private static readonly ExtendedPropertyDefinition PidTagMessageFlags =
        new(0x0E07, MapiPropertyType.Integer);

    private static readonly ExtendedPropertyDefinition PidTagMessageDeliveryTime =
        new(0x0E06, MapiPropertyType.SystemTime);

    private static readonly ExtendedPropertyDefinition PidTagClientSubmitTime =
        new(0x0039, MapiPropertyType.SystemTime);

    private static readonly ExtendedPropertyDefinition PidTagReadReceiptRequested =
        new(0x0029, MapiPropertyType.Boolean);

    private static readonly ExtendedPropertyDefinition PidTagDeliveryReportRequested =
        new(0x0023, MapiPropertyType.Boolean);

    private readonly EwsOutputOptions _options;
    private ExchangeService? _service;
    private Folder? _folder;
    private int _written;

    public EwsOutputTarget(EwsOutputOptions options)
    {
        _options = options;
    }

    public string Name => "ews";

    public async Task OpenAsync(OutputContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var certificate = LoadCertificate(_options.CertificateThumbprint);
        var app = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithCertificate(certificate)
            .WithTenantId(_options.TenantId)
            .Build();

        var token = await app
            .AcquireTokenForClient(new[] { "https://outlook.office365.com/.default" })
            .ExecuteAsync(cancellationToken);

        var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
        {
            Url = new Uri(_options.EwsUrl),
            Credentials = new OAuthCredentials(token.AccessToken),
            ImpersonatedUserId = new ImpersonatedUserId(
                ConnectingIdType.SmtpAddress,
                _options.TargetUpn),
            Timeout = 300_000
        };

        service.HttpHeaders["X-AnchorMailbox"] = _options.TargetUpn;
        service.UserAgent = "david2exchange/0.1";
        _service = service;
        _folder = await EnsureFolderAsync(service, _options.FolderName, cancellationToken);

        Console.WriteLine(
            $"  EWS verbunden: {_options.TargetUpn} · Zielordner '{_options.FolderName}'");
    }

    public async Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken)
    {
        if (_service is null || _folder is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        if (string.IsNullOrWhiteSpace(item.StagedEmlPath) || !File.Exists(item.StagedEmlPath))
        {
            throw new InvalidOperationException(
                $"EWS-Ausgabe benötigt eine EML-Datei; Item {item.Id} ({item.Type}) hat keine. " +
                "Für den EWS-Test --type 2 und --export-eml > 0 verwenden.");
        }

        var bytes = await File.ReadAllBytesAsync(item.StagedEmlPath, cancellationToken);
        var message = new EmailMessage(_service)
        {
            MimeContent = new MimeContent("UTF-8", bytes),
            IsRead = item.IsRead ?? false
        };

        message.SetExtendedProperty(PidTagMessageFlags, item.IsRead == true ? 1 : 0);
        if (item.ReceivedLocal.HasValue)
        {
            message.SetExtendedProperty(
                PidTagMessageDeliveryTime,
                item.ReceivedLocal.Value.ToUniversalTime());
        }

        if (item.SentLocal.HasValue)
        {
            message.SetExtendedProperty(
                PidTagClientSubmitTime,
                item.SentLocal.Value.ToUniversalTime());
        }

        message.SetExtendedProperty(PidTagReadReceiptRequested, false);
        message.SetExtendedProperty(PidTagDeliveryReportRequested, false);

        var responses = await _service.CreateItems(
            new[] { message },
            _folder.Id,
            MessageDisposition.SaveOnly,
            null);

        if (responses.Count == 0 || responses[0].Result != ServiceResult.Success)
        {
            var error = responses.Count > 0
                ? $"{responses[0].Result}: {responses[0].ErrorMessage}"
                : "Keine ServiceResponse";
            throw new InvalidOperationException($"EWS-Import fehlgeschlagen für {item.Id}: {error}");
        }

        _written++;
    }

    public Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  EWS: {_written} Items importiert.");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _folder = null;
        _service = null;
        return ValueTask.CompletedTask;
    }

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var normalized = thumbprint.Replace(" ", "", StringComparison.Ordinal);
        var certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            normalized,
            validOnly: false);

        if (certificates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Zertifikat mit Thumbprint '{thumbprint}' wurde in CurrentUser\\My nicht gefunden.");
        }

        return certificates[0];
    }

    private static async Task<Folder> EnsureFolderAsync(
        ExchangeService service,
        string folderName,
        CancellationToken cancellationToken)
    {
        var root = await Folder.Bind(service, WellKnownFolderName.MsgFolderRoot);
        var filter = new SearchFilter.IsEqualTo(FolderSchema.DisplayName, folderName);
        var view = new FolderView(10) { Traversal = FolderTraversal.Shallow };

        var found = await service.FindFolders(root.Id, filter, view);
        if (found.Folders.Count > 0)
        {
            return found.Folders[0];
        }

        var folder = new Folder(service) { DisplayName = folderName };
        await folder.Save(root.Id);
        return folder;
    }
}
