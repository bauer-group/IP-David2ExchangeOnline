// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace David2Exchange.Tests.Output;

/// <summary>
/// Kleiner SOAP-Server für die Operationen des Tools. So läuft in den Tests die echte EWS
/// Managed API samt Serialisierung und Fehlerpfaden, nur ohne Exchange Online.
/// </summary>
internal sealed class FakeEwsServer : IDisposable
{
    public static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
    public static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    private const string ItemKeyPropertySet = "7f9c1a52-3b6e-4f0a-9d2c-5e8b1a4c7d90";

    private readonly HttpListener _listener;
    private readonly object _gate = new();
    private readonly List<StoredItem> _items = new();
    private readonly Dictionary<string, string> _folderNames = new();
    private readonly List<string> _operations = new();
    private readonly List<string> _authorizations = new();
    private int _nextId;
    private int _currentRequests;
    private int _maximumRequests;

    public FakeEwsServer()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = Random.Shared.Next(20_000, 50_000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Url = $"http://localhost:{port}/EWS/Exchange.asmx";
                break;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                // Port belegt (Windows-RPC, WinRM, ein zweiter Testlauf). Ein gescheitertes Start
                // gibt den Listener frei, also braucht jeder Versuch einen neuen.
            }
        }

        _ = Task.Run(AcceptLoopAsync);
    }

    public string Url { get; }

    /// <summary>Kann eine Anfrage abfangen. Null lässt sie normal verarbeiten.</summary>
    public Func<FakeRequest, FakeReply?>? Intercept { get; set; }

    /// <summary>Begrenzt die Treffer je FindItem-Antwort, wie EWS es unter Last darf.</summary>
    public int? FindPageLimit { get; set; }

    /// <summary>Überschreibt den gemeldeten Elternordner in GetItem.</summary>
    public Func<StoredItem, string>? ReportedParent { get; set; }

    public int MaxConcurrentRequests => Volatile.Read(ref _maximumRequests);

    public IReadOnlyList<StoredItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToList();
            }
        }
    }

    public IReadOnlyList<string> Authorizations
    {
        get
        {
            lock (_gate)
            {
                return _authorizations.ToList();
            }
        }
    }

    public IReadOnlyDictionary<string, string> Folders
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_folderNames);
            }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    public void Seed(string folderId, string itemKey)
    {
        lock (_gate)
        {
            _items.Add(new StoredItem($"item-{++_nextId}", folderId, itemKey, "Message", null, null));
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var current = Interlocked.Increment(ref _currentRequests);
        int seen;
        while (current > (seen = Volatile.Read(ref _maximumRequests)))
        {
            if (Interlocked.CompareExchange(ref _maximumRequests, current, seen) == seen)
            {
                break;
            }
        }

        try
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            var document = XDocument.Parse(body);
            var operation = document.Root!
                .Element(XNamespace.Get("http://schemas.xmlsoap.org/soap/envelope/") + "Body")!
                .Elements().First();

            lock (_gate)
            {
                _operations.Add(operation.Name.LocalName);
                _authorizations.Add(context.Request.Headers["Authorization"] ?? "");
            }

            var reply = Intercept?.Invoke(new FakeRequest(operation.Name.LocalName, operation, this));
            if (reply is { StoreFirst: true })
            {
                Process(operation);
            }

            if (reply is { DropConnection: true })
            {
                context.Response.Abort();
                return;
            }

            var status = reply?.StatusCode ?? 200;
            var content = reply?.Body ?? Envelope(Process(operation));

            context.Response.StatusCode = status;
            context.Response.ContentType = "text/xml; charset=utf-8";
            foreach (var header in reply?.Headers ?? new Dictionary<string, string>())
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // Client hat aufgelegt.
        }
        finally
        {
            Interlocked.Decrement(ref _currentRequests);
        }
    }

    private string Process(XElement operation)
    {
        lock (_gate)
        {
            return operation.Name.LocalName switch
            {
                "GetFolder" => GetFolder(operation),
                "FindFolder" => FindFolder(operation),
                "CreateFolder" => CreateFolder(operation),
                "CreateItem" => CreateItem(operation),
                "GetItem" => GetItem(operation),
                "FindItem" => FindItem(operation),
                _ => throw new NotSupportedException(operation.Name.LocalName)
            };
        }
    }

    private string GetFolder(XElement operation)
    {
        var id = operation.Descendants(T + "DistinguishedFolderId").First().Attribute("Id")!.Value;
        var folderId = "root-" + id;
        _folderNames.TryAdd(folderId, id);
        return $"""
            <m:GetFolderResponse><m:ResponseMessages><m:GetFolderResponseMessage ResponseClass="Success">
            <m:ResponseCode>NoError</m:ResponseCode><m:Folders>{FolderXml(folderId, id)}</m:Folders>
            </m:GetFolderResponseMessage></m:ResponseMessages></m:GetFolderResponse>
            """;
    }

    private string FindFolder(XElement operation)
    {
        var parent = operation.Element(M + "ParentFolderIds")!.Elements().First().Attribute("Id")!.Value;
        var name = operation.Descendants(T + "Constant").First().Attribute("Value")!.Value;
        var found = _folderNames.FirstOrDefault(folder => folder.Key == $"{parent}/{name}");
        var folders = found.Key is null ? "" : FolderXml(found.Key, name);
        var count = found.Key is null ? 0 : 1;
        return $"""
            <m:FindFolderResponse><m:ResponseMessages><m:FindFolderResponseMessage ResponseClass="Success">
            <m:ResponseCode>NoError</m:ResponseCode>
            <m:RootFolder TotalItemsInView="{count}" IncludesLastItemInRange="true"><t:Folders>{folders}</t:Folders></m:RootFolder>
            </m:FindFolderResponseMessage></m:ResponseMessages></m:FindFolderResponse>
            """;
    }

    private string CreateFolder(XElement operation)
    {
        var parent = operation.Element(M + "ParentFolderId")!.Elements().First().Attribute("Id")!.Value;
        var name = operation.Descendants(T + "DisplayName").First().Value;
        var folderId = $"{parent}/{name}";
        if (!_folderNames.TryAdd(folderId, name))
        {
            return """
                <m:CreateFolderResponse><m:ResponseMessages><m:CreateFolderResponseMessage ResponseClass="Error">
                <m:MessageText>Exists</m:MessageText><m:ResponseCode>ErrorFolderExists</m:ResponseCode><m:DescriptiveLinkKey>0</m:DescriptiveLinkKey>
                <m:Folders /></m:CreateFolderResponseMessage></m:ResponseMessages></m:CreateFolderResponse>
                """;
        }

        return $"""
            <m:CreateFolderResponse><m:ResponseMessages><m:CreateFolderResponseMessage ResponseClass="Success">
            <m:ResponseCode>NoError</m:ResponseCode><m:Folders><t:Folder><t:FolderId Id="{Escape(folderId)}" ChangeKey="ck" /></t:Folder></m:Folders>
            </m:CreateFolderResponseMessage></m:ResponseMessages></m:CreateFolderResponse>
            """;
    }

    private string CreateItem(XElement operation)
    {
        var folder = operation.Element(M + "SavedItemFolderId")!.Elements().First().Attribute("Id")!.Value;
        var builder = new StringBuilder("<m:CreateItemResponse><m:ResponseMessages>");

        foreach (var element in operation.Element(M + "Items")!.Elements())
        {
            var id = $"item-{++_nextId}";
            var mime = element.Element(T + "MimeContent")?.Value;
            _items.Add(new StoredItem(
                id,
                folder,
                ReadItemKey(element),
                element.Name.LocalName,
                mime is null ? null : Convert.FromBase64String(mime),
                element));

            builder.Append(
                $"""
                <m:CreateItemResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode>
                <m:Items><t:{element.Name.LocalName}><t:ItemId Id="{id}" ChangeKey="ck" /></t:{element.Name.LocalName}></m:Items></m:CreateItemResponseMessage>
                """);
        }

        return builder.Append("</m:ResponseMessages></m:CreateItemResponse>").ToString();
    }

    private string GetItem(XElement operation)
    {
        var builder = new StringBuilder("<m:GetItemResponse><m:ResponseMessages>");
        foreach (var requested in operation.Element(M + "ItemIds")!.Elements())
        {
            var id = requested.Attribute("Id")!.Value;
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
            {
                builder.Append(
                    """
                    <m:GetItemResponseMessage ResponseClass="Error"><m:MessageText>Gone</m:MessageText>
                    <m:ResponseCode>ErrorItemNotFound</m:ResponseCode><m:DescriptiveLinkKey>0</m:DescriptiveLinkKey><m:Items /></m:GetItemResponseMessage>
                    """);
                continue;
            }

            var parent = ReportedParent?.Invoke(item) ?? item.FolderId;
            builder.Append(
                $"""
                <m:GetItemResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode><m:Items>
                <t:Message><t:ItemId Id="{item.Id}" ChangeKey="ck" /><t:ParentFolderId Id="{Escape(parent)}" ChangeKey="ck" />{KeyXml(item.ItemKey)}</t:Message>
                </m:Items></m:GetItemResponseMessage>
                """);
        }

        return builder.Append("</m:ResponseMessages></m:GetItemResponse>").ToString();
    }

    private string FindItem(XElement operation)
    {
        var parent = operation.Element(M + "ParentFolderIds")!.Elements().First().Attribute("Id")!.Value;
        var keys = operation.Descendants(T + "Constant").Select(constant => constant.Attribute("Value")!.Value).ToHashSet();
        var matches = _items.Where(item => item.FolderId == parent && keys.Contains(item.ItemKey)).ToList();
        var offset = int.Parse(operation.Element(M + "IndexedPageItemView")?.Attribute("Offset")?.Value ?? "0");
        var page = matches.Skip(offset).Take(FindPageLimit ?? int.MaxValue).ToList();
        var isLast = offset + page.Count >= matches.Count;
        var items = string.Concat(page.Select(item =>
            $"""<t:Message><t:ItemId Id="{item.Id}" ChangeKey="ck" />{KeyXml(item.ItemKey)}</t:Message>"""));
        return $"""
            <m:FindItemResponse><m:ResponseMessages><m:FindItemResponseMessage ResponseClass="Success">
            <m:ResponseCode>NoError</m:ResponseCode>
            <m:RootFolder TotalItemsInView="{matches.Count}" IncludesLastItemInRange="{(isLast ? "true" : "false")}"><t:Items>{items}</t:Items></m:RootFolder>
            </m:FindItemResponseMessage></m:ResponseMessages></m:FindItemResponse>
            """;
    }

    private static string ReadItemKey(XElement item)
    {
        return item.Elements(T + "ExtendedProperty")
            .Where(property => property.Element(T + "ExtendedFieldURI")?.Attribute("PropertySetId")?.Value == ItemKeyPropertySet)
            .Select(property => property.Element(T + "Value")!.Value)
            .FirstOrDefault() ?? "";
    }

    private static string FolderXml(string id, string name)
    {
        return $"""<t:Folder><t:FolderId Id="{Escape(id)}" ChangeKey="ck" /><t:DisplayName>{Escape(name)}</t:DisplayName></t:Folder>""";
    }

    private static string KeyXml(string key)
    {
        return $"""
            <t:ExtendedProperty><t:ExtendedFieldURI PropertySetId="{ItemKeyPropertySet}" PropertyName="david2exchange.ItemKey" PropertyType="String" /><t:Value>{Escape(key)}</t:Value></t:ExtendedProperty>
            """;
    }

    private static string Escape(string value)
    {
        return System.Security.SecurityElement.Escape(value);
    }

    public static string Envelope(string body)
    {
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:m="{M}" xmlns:t="{T}">
            <s:Header><t:ServerVersionInfo MajorVersion="15" MinorVersion="20" MajorBuildNumber="1" MinorBuildNumber="1" Version="V2018_01_08" /></s:Header>
            <s:Body>{body}</s:Body></s:Envelope>
            """;
    }

    /// <summary>EWS-Antwort für eine Anfrage, deren Items alle denselben Fehler bekommen.</summary>
    public static string ItemErrors(int count, string code, int? backOff = null)
    {
        var builder = new StringBuilder("<m:CreateItemResponse><m:ResponseMessages>");
        for (var i = 0; i < count; i++)
        {
            builder.Append(ItemError(code, backOff));
        }

        return builder.Append("</m:ResponseMessages></m:CreateItemResponse>").ToString();
    }

    public static string ItemError(string code, int? backOff = null)
    {
        var details = backOff is null
            ? ""
            : $"""<m:MessageXml><t:Value Name="BackOffMilliseconds">{backOff}</t:Value></m:MessageXml>""";
        return $"""
            <m:CreateItemResponseMessage ResponseClass="Error"><m:MessageText>{code}</m:MessageText>
            <m:ResponseCode>{code}</m:ResponseCode><m:DescriptiveLinkKey>0</m:DescriptiveLinkKey>{details}<m:Items /></m:CreateItemResponseMessage>
            """;
    }

    /// <summary>HTTP-500-SOAP-Fault mit einem beliebigen Fehlercode.</summary>
    public static FakeReply Fault(string code, bool storeFirst = false)
    {
        return new FakeReply(StatusCode: 500, StoreFirst: storeFirst, Body: $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
            <faultcode xmlns:a="http://schemas.microsoft.com/exchange/services/2006/types">a:{code}</faultcode>
            <faultstring xml:lang="en-US">{code}</faultstring>
            <detail><e:ResponseCode xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">{code}</e:ResponseCode>
            <e:Message xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">{code}</e:Message>
            </detail></s:Fault></s:Body></s:Envelope>
            """);
    }

    /// <summary>GetItem-Antwort, deren Items alle denselben Fehler bekommen.</summary>
    public static string GetItemErrors(int count, string code, int? backOff = null)
    {
        var details = backOff is null
            ? ""
            : $"""<m:MessageXml><t:Value Name="BackOffMilliseconds">{backOff}</t:Value></m:MessageXml>""";
        var builder = new StringBuilder("<m:GetItemResponse><m:ResponseMessages>");
        for (var i = 0; i < count; i++)
        {
            builder.Append(
                $"""
                <m:GetItemResponseMessage ResponseClass="Error"><m:MessageText>{code}</m:MessageText>
                <m:ResponseCode>{code}</m:ResponseCode><m:DescriptiveLinkKey>0</m:DescriptiveLinkKey>{details}<m:Items /></m:GetItemResponseMessage>
                """);
        }

        return builder.Append("</m:ResponseMessages></m:GetItemResponse>").ToString();
    }

    /// <summary>HTTP-500-SOAP-Fault, wie ihn Exchange bei Drosselung sendet.</summary>
    public static FakeReply ServerBusyFault(int backOffMilliseconds)
    {
        return new FakeReply(StatusCode: 500, Body: $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
            <faultcode xmlns:a="http://schemas.microsoft.com/exchange/services/2006/types">a:ErrorServerBusy</faultcode>
            <faultstring xml:lang="en-US">The server cannot service this request right now. Try again later.</faultstring>
            <detail><e:ResponseCode xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">ErrorServerBusy</e:ResponseCode>
            <e:Message xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">Busy</e:Message>
            <t:MessageXml xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"><t:Value Name="BackOffMilliseconds">{backOffMilliseconds}</t:Value></t:MessageXml>
            </detail></s:Fault></s:Body></s:Envelope>
            """);
    }
}

internal sealed record FakeRequest(string Operation, XElement Element, FakeEwsServer Server)
{
    public int ItemCount => Element.Element(FakeEwsServer.M + "Items")?.Elements().Count() ?? 0;
}

internal sealed record FakeReply(
    int? StatusCode = null,
    string? Body = null,
    bool DropConnection = false,
    bool StoreFirst = false,
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record StoredItem(
    string Id,
    string FolderId,
    string ItemKey,
    string ElementName,
    byte[]? Mime,
    XElement? Request);
