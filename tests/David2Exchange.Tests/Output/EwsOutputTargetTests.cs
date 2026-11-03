// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using David2Exchange.Output;

namespace David2Exchange.Tests.Output;

public sealed class EwsOutputTargetTests : IDisposable
{
    private const string InboxId = "root-inbox";
    private const string SentId = "root-sentitems";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-ews-" + Guid.NewGuid().ToString("N"));

    private readonly FakeEwsServer _server = new();
    private readonly List<TimeSpan> _delays = new();
    private readonly List<bool> _tokenRequests = new();
    private Func<bool, string> _token = _ => "a.token1.c";

    public EwsOutputTargetTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<EwsOutputTarget> OpenAsync(int workers = 8)
    {
        var target = new EwsOutputTarget(
            new EwsOutputOptions("ziel@example.com", _server.Url, "", "", "", "", "Europe/Berlin", workers),
            (force, _) =>
            {
                lock (_tokenRequests)
                {
                    _tokenRequests.Add(force);
                }

                return Task.FromResult(_token(force));
            },
            (delay, _) =>
            {
                lock (_delays)
                {
                    _delays.Add(delay);
                }

                return Task.CompletedTask;
            });
        await target.OpenAsync(new OutputContext(_directory), CancellationToken.None);
        return target;
    }

    private OutputItem Mail(
        string id,
        string folderPath = "in",
        bool checkExisting = false,
        string kind = "mail",
        string? content = null,
        bool isRead = true)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".eml");
        File.WriteAllText(path, content ?? $"Subject: {id}\r\n\r\nInhalt {id}");
        return new OutputItem(
            @"user\1\" + folderPath,
            id,
            "2",
            kind,
            id,
            100,
            0,
            new DateTime(2024, 7, 1, 14, 30, 0, DateTimeKind.Local),
            null,
            isRead,
            null,
            path,
            new Dictionary<string, string?>(),
            kind,
            folderPath,
            checkExisting,
            $"box|{folderPath}|{id}");
    }

    private static string StoredKey(OutputItem item) => "v2|" + item.ItemKey;

    private static string MimeSubject(StoredItem item) =>
        Encoding.ASCII.GetString(item.Mime!).Split("\r\n")[0];

    [Fact]
    public async Task Twenty_items_go_into_one_request_and_each_keeps_its_content()
    {
        await using var target = await OpenAsync();
        var items = Enumerable.Range(0, 20).Select(i => Mail($"m{i}")).ToList();

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Single(_server.Operations, operation => operation == "CreateItem");
        Assert.Equal(20, _server.Items.Count);
        foreach (var item in items)
        {
            var stored = Assert.Single(_server.Items, candidate => candidate.ItemKey == StoredKey(item));
            Assert.Equal($"Subject: {item.Id}", MimeSubject(stored));
            Assert.Equal(InboxId, stored.FolderId);
        }
    }

    [Fact]
    public async Task Inbox_and_sent_items_land_in_their_own_folder_even_within_one_batch()
    {
        await using var target = await OpenAsync();
        var inbox = Mail("eingang", "in");
        var sent = Mail("ausgang", "out");

        var results = await target.WriteBatchAsync(new[] { sent, inbox }, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(SentId, _server.Items.Single(i => i.ItemKey == StoredKey(sent)).FolderId);
        Assert.Equal(InboxId, _server.Items.Single(i => i.ItemKey == StoredKey(inbox)).FolderId);
    }

    [Fact]
    public async Task Every_result_is_verified_by_id_and_carries_the_target_id()
    {
        await using var target = await OpenAsync();

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.Contains("GetItem", _server.Operations);
        Assert.Equal(_server.Items[0].Id, results[0].TargetId);
    }

    [Fact]
    public async Task Fresh_items_do_not_cost_a_lookup()
    {
        await using var target = await OpenAsync();

        await target.WriteBatchAsync(new[] { Mail("a"), Mail("b") }, CancellationToken.None);

        Assert.DoesNotContain("FindItem", _server.Operations);
    }

    [Fact]
    public async Task Item_that_may_exist_is_not_sent_again_when_found()
    {
        await using var target = await OpenAsync();
        var existing = Mail("schon-da", checkExisting: true);
        var missing = Mail("fehlt", checkExisting: true);
        await target.WriteBatchAsync(new[] { Mail("auslöser") }, CancellationToken.None);
        _server.Seed(InboxId, StoredKey(existing));

        var results = await target.WriteBatchAsync(new[] { existing, missing }, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.True(results.Single(r => r.Item == existing).AlreadyPresent);
        Assert.False(results.Single(r => r.Item == missing).AlreadyPresent);
        Assert.Single(_server.Items, item => item.ItemKey == StoredKey(existing));
        Assert.Single(_server.Items, item => item.ItemKey == StoredKey(missing));
    }

    [Fact]
    public async Task Same_key_in_another_folder_does_not_count_as_present()
    {
        await using var target = await OpenAsync();
        var item = Mail("x", "in", checkExisting: true);
        await target.WriteBatchAsync(new[] { Mail("auslöser", "out") }, CancellationToken.None);
        _server.Seed(SentId, StoredKey(item));

        var results = await target.WriteBatchAsync(new[] { item }, CancellationToken.None);

        Assert.False(results[0].AlreadyPresent);
        Assert.Equal(InboxId, _server.Items.Last().FolderId);
    }

    [Fact]
    public async Task Connection_lost_after_the_server_saved_creates_no_duplicate()
    {
        // Der gefährlichste Fall: gespeichert, aber die Antwort kommt nie an.
        await using var target = await OpenAsync();
        var items = Enumerable.Range(0, 5).Select(i => Mail($"m{i}")).ToList();
        var dropped = 0;
        _server.Intercept = request =>
            request.Operation == "CreateItem" && Interlocked.Increment(ref dropped) == 1
                ? new FakeReply(DropConnection: true, StoreFirst: true)
                : null;

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(5, _server.Items.Count);
        Assert.All(results, result => Assert.True(result.AlreadyPresent));
    }

    [Fact]
    public async Task Connection_lost_before_saving_sends_again_after_checking()
    {
        await using var target = await OpenAsync();
        var dropped = 0;
        _server.Intercept = request =>
            request.Operation == "CreateItem" && Interlocked.Increment(ref dropped) == 1
                ? new FakeReply(DropConnection: true)
                : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Single(_server.Items);
        Assert.Contains("FindItem", _server.Operations);
    }

    [Fact]
    public async Task Server_busy_fault_honours_the_requested_back_off_beyond_the_old_30_seconds()
    {
        await using var target = await OpenAsync();
        var busy = 0;
        _server.Intercept = request =>
            request.Operation == "CreateItem" && Interlocked.Increment(ref busy) == 1
                ? FakeEwsServer.ServerBusyFault(90_000)
                : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Contains(TimeSpan.FromSeconds(90), _delays);
        Assert.Single(_server.Items);
    }

    [Fact]
    public async Task Item_level_throttling_retries_only_the_affected_item()
    {
        await using var target = await OpenAsync();
        var a = Mail("a");
        var b = Mail("b");
        var first = 0;
        _server.Intercept = request =>
        {
            if (request.Operation != "CreateItem" || Interlocked.Increment(ref first) != 1)
            {
                return null;
            }

            // a wird gespeichert, b gedrosselt.
            var aId = "item-busy";
            return new FakeReply(Body: FakeEwsServer.Envelope(
                $"""
                <m:CreateItemResponse><m:ResponseMessages>
                <m:CreateItemResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode>
                <m:Items><t:Message><t:ItemId Id="{aId}" ChangeKey="ck" /></t:Message></m:Items></m:CreateItemResponseMessage>
                {FakeEwsServer.ItemError("ErrorServerBusy", 5000)}
                </m:ResponseMessages></m:CreateItemResponse>
                """));
        };
        _server.ReportedParent = _ => InboxId;

        var results = await target.WriteBatchAsync(new[] { a, b }, CancellationToken.None);

        var second = _server.Items.Where(item => item.ItemKey == StoredKey(b)).ToList();
        Assert.Single(second);
        Assert.Contains(TimeSpan.FromSeconds(5), _delays);
        Assert.True(results.Single(r => r.Item == b).Success);
    }

    [Fact]
    public async Task Permanent_item_error_fails_that_item_and_keeps_the_others()
    {
        await using var target = await OpenAsync();
        var good = Mail("gut");
        var bad = Mail("schlecht");
        var first = 0;
        _server.Intercept = request =>
        {
            if (request.Operation != "CreateItem" || Interlocked.Increment(ref first) != 1)
            {
                return null;
            }

            return new FakeReply(Body: FakeEwsServer.Envelope(
                $"""
                <m:CreateItemResponse><m:ResponseMessages>
                <m:CreateItemResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode>
                <m:Items><t:Message><t:ItemId Id="missing-on-server" ChangeKey="ck" /></t:Message></m:Items></m:CreateItemResponseMessage>
                {FakeEwsServer.ItemError("ErrorMessageSizeExceeded")}
                </m:ResponseMessages></m:CreateItemResponse>
                """));
        };

        var results = await target.WriteBatchAsync(new[] { good, bad }, CancellationToken.None);

        var failed = results.Single(r => r.Item == bad);
        Assert.False(failed.Success);
        Assert.False(failed.Retryable);
        Assert.Contains("ErrorMessageSizeExceeded", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_poison_item_rejecting_the_whole_request_does_not_take_the_others_down()
    {
        await using var target = await OpenAsync();
        var items = new[] { Mail("a"), Mail("gift"), Mail("c") };
        _server.Intercept = request =>
        {
            if (request.Operation != "CreateItem")
            {
                return null;
            }

            var containsPoison = request.Element.ToString().Contains(
                Convert.ToBase64String(Encoding.ASCII.GetBytes("Subject: gift\r\n\r\nInhalt gift")),
                StringComparison.Ordinal);
            return containsPoison
                ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(1, "ErrorInvalidRequest")))
                : null;
        };

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.True(results.Single(r => r.Item == items[0]).Success);
        Assert.True(results.Single(r => r.Item == items[2]).Success);
        var poison = results.Single(r => r.Item == items[1]);
        Assert.False(poison.Success);
        Assert.False(poison.Retryable);
    }

    [Fact]
    public async Task Item_reported_in_a_foreign_folder_is_failed_and_never_moved()
    {
        await using var target = await OpenAsync();
        _server.ReportedParent = _ => SentId;
        _server.Intercept = request => request.Operation == "FindItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(
                """
                <m:FindItemResponse><m:ResponseMessages><m:FindItemResponseMessage ResponseClass="Success">
                <m:ResponseCode>NoError</m:ResponseCode><m:RootFolder TotalItemsInView="0" IncludesLastItemInRange="true"><t:Items /></m:RootFolder>
                </m:FindItemResponseMessage></m:ResponseMessages></m:FindItemResponse>
                """))
            : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.False(results[0].Success);
        Assert.Contains("Zielordner", results[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveItem", _server.Operations);
    }

    [Fact]
    public async Task Differently_spelled_folder_id_is_settled_by_a_lookup_in_the_target_folder()
    {
        await using var target = await OpenAsync();
        _server.ReportedParent = item => item.FolderId + "==";

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_and_the_item_still_arrives()
    {
        var current = "a.token1.c";
        _token = force => force ? current = "a.token2.c" : current;
        await using var target = await OpenAsync();
        var unauthorized = 0;
        _server.Intercept = request =>
            request.Operation == "CreateItem" && Interlocked.Increment(ref unauthorized) == 1
                ? new FakeReply(StatusCode: 401, Body: "")
                : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Contains(true, _tokenRequests);
        Assert.Contains("Bearer a.token2.c", _server.Authorizations);
        Assert.Single(_server.Items);
    }

    [Fact]
    public async Task New_token_from_the_cache_is_picked_up_for_long_runs()
    {
        var current = "a.token1.c";
        _token = _ => current;
        await using var target = await OpenAsync(workers: 1);
        await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        current = "a.erneuert.c";
        await target.WriteBatchAsync(new[] { Mail("b") }, CancellationToken.None);

        Assert.Equal("Bearer a.erneuert.c", _server.Authorizations[^1]);
    }

    [Fact]
    public async Task Rejected_credentials_stop_the_whole_run()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(StatusCode: 401, Body: "")
            : null;

        var abort = await Assert.ThrowsAsync<OutputAbortedException>(() =>
            target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None));

        Assert.True(abort.AffectsAllMailboxes);
    }

    [Fact]
    public async Task Ews_policy_block_names_the_reason_from_the_response_header()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(
                StatusCode: 403,
                Body: "",
                Headers: new Dictionary<string, string>
                {
                    ["X-EWS-Policy-Reason"] = "EWS is blocked by policy for this user or tenant"
                })
            : null;

        var abort = await Assert.ThrowsAsync<OutputAbortedException>(() =>
            target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None));

        Assert.True(abort.AffectsAllMailboxes);
        Assert.Contains("blocked by policy", abort.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocked_tenant_is_detected_before_any_export_starts()
    {
        _server.Intercept = request => request.Operation == "GetFolder"
            ? new FakeReply(
                StatusCode: 403,
                Body: "",
                Headers: new Dictionary<string, string>
                {
                    ["X-EWS-Policy-Reason"] = "EWS is disabled for this user or tenant"
                })
            : null;

        var abort = await Assert.ThrowsAsync<OutputAbortedException>(() => OpenAsync());

        Assert.Contains("disabled", abort.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_mailbox_stops_only_this_mailbox()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(2, "ErrorQuotaExceeded")))
            : null;

        var abort = await Assert.ThrowsAsync<OutputAbortedException>(() =>
            target.WriteBatchAsync(new[] { Mail("a"), Mail("b") }, CancellationToken.None));

        Assert.False(abort.AffectsAllMailboxes);
    }

    [Fact]
    public async Task Persistent_throttling_gives_up_with_a_retryable_failure()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? FakeEwsServer.ServerBusyFault(1000)
            : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.False(results[0].Success);
        Assert.True(results[0].Retryable);
        Assert.Empty(_server.Items);
    }

    [Fact]
    public async Task Missing_staged_file_fails_without_reaching_the_server()
    {
        await using var target = await OpenAsync();
        var item = Mail("a");
        File.Delete(item.StagedEmlPath!);

        var results = await target.WriteBatchAsync(new[] { item }, CancellationToken.None);

        Assert.False(results[0].Success);
        Assert.DoesNotContain("CreateItem", _server.Operations);
    }

    [Theory]
    [InlineData("fax")]
    [InlineData("sms")]
    [InlineData("voice")]
    [InlineData("chat")]
    public async Task Fax_sms_voice_and_chat_are_stored_byte_exact_as_messages(string kind)
    {
        await using var target = await OpenAsync();
        var content = $"Subject: {kind}\r\nX-Test: ä\r\n\r\nInhalt";
        var item = Mail(kind, kind: kind, content: content);

        var results = await target.WriteBatchAsync(new[] { item }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        var stored = _server.Items.Single();
        Assert.Equal("Message", stored.ElementName);
        Assert.Equal(File.ReadAllBytes(item.StagedEmlPath!), stored.Mime);
    }

    [Fact]
    public async Task Message_flags_times_and_read_state_are_set_on_creation()
    {
        await using var target = await OpenAsync();

        await target.WriteBatchAsync(new[] { Mail("gelesen", isRead: true), Mail("neu", isRead: false) }, CancellationToken.None);

        var read = _server.Items.Single(i => i.ItemKey.EndsWith("gelesen", StringComparison.Ordinal)).Request!;
        var unread = _server.Items.Single(i => i.ItemKey.EndsWith("neu", StringComparison.Ordinal)).Request!;
        Assert.Equal("1", TaggedValue(read, "0xe07"));
        Assert.Equal("0", TaggedValue(unread, "0xe07"));
        // 14:30 Uhr Berliner Sommerzeit des David-Servers sind 12:30 UTC.
        Assert.StartsWith("2024-07-01T12:30:00", TaggedValue(read, "0xe06"), StringComparison.Ordinal);
        Assert.StartsWith("2024-07-01T12:30:00", TaggedValue(read, "0x39"), StringComparison.Ordinal);
    }

    private static int ParseTag(string value)
    {
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value, 16)
            : int.Parse(value);
    }

    private static string? TaggedValue(XElement item, string tag)
    {
        return item.Elements(FakeEwsServer.T + "ExtendedProperty")
            .Where(property =>
            {
                var value = property.Element(FakeEwsServer.T + "ExtendedFieldURI")?.Attribute("PropertyTag")?.Value;
                return value is not null && ParseTag(value) == ParseTag(tag);
            })
            .Select(property => property.Element(FakeEwsServer.T + "Value")?.Value)
            .FirstOrDefault();
    }

    [Fact]
    public async Task Appointment_never_sends_invitations_and_goes_to_the_calendar()
    {
        await using var target = await OpenAsync();
        var item = Mail("Termin", "cal", kind: "calendar") with
        {
            Properties = new Dictionary<string, string?>
            {
                ["StartTime"] = "2024-07-01T14:30:00.0000000",
                ["StopTime"] = "2024-07-01T15:30:00.0000000"
            }
        };

        var results = await target.WriteBatchAsync(new[] { item }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        var stored = _server.Items.Single();
        Assert.Equal("root-calendar", stored.FolderId);
        Assert.Equal("CalendarItem", stored.ElementName);
        Assert.Contains(
            "SendToNone",
            stored.Request!.Parent!.Parent!.Attribute("SendMeetingInvitations")?.Value ?? "",
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contact_and_task_go_to_their_folders()
    {
        await using var target = await OpenAsync();

        var results = await target.WriteBatchAsync(
            new[] { Mail("Kontakt", "address", kind: "contact"), Mail("Aufgabe", "todo", kind: "task") },
            CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal("root-contacts", _server.Items.Single(i => i.ElementName == "Contact").FolderId);
        Assert.Equal("root-tasks", _server.Items.Single(i => i.ElementName == "Task").FolderId);
    }

    [Fact]
    public async Task Failed_appointment_is_not_silently_moved_to_the_calendar_root()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(1, "ErrorInvalidRequest")))
            : null;

        var results = await target.WriteBatchAsync(new[] { Mail("Termin", "cal/Privat", kind: "calendar") }, CancellationToken.None);

        Assert.False(results[0].Success);
        Assert.Empty(_server.Items);
    }

    [Fact]
    public async Task Subfolder_named_like_a_root_stays_below_the_inbox()
    {
        await using var target = await OpenAsync();

        var results = await target.WriteBatchAsync(new[] { Mail("a", "in/Ausgang/Kunde") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Equal("root-inbox/Ausgang/Kunde", _server.Items.Single().FolderId);
    }

    [Fact]
    public async Task Concurrent_workers_create_a_shared_folder_only_once()
    {
        await using var target = await OpenAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            target.WriteBatchAsync(new[] { Mail($"m{i}", "in/Projekt") }, CancellationToken.None)));

        Assert.All(results.SelectMany(r => r), result => Assert.True(result.Success, result.Error));
        Assert.Single(_server.Operations, operation => operation == "CreateFolder");
        Assert.Equal(8, _server.Items.Count(item => item.FolderId == "root-inbox/Projekt"));
    }

    [Fact]
    public async Task Eight_workers_really_send_in_parallel()
    {
        await using var target = await OpenAsync();
        await target.WriteBatchAsync(new[] { Mail("warm", "in") }, CancellationToken.None);
        using var barrier = new Barrier(8);
        _server.Intercept = request =>
        {
            if (request.Operation == "CreateItem")
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            }

            return null;
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            target.WriteBatchAsync(new[] { Mail($"m{i}") }, CancellationToken.None)));

        Assert.All(results.SelectMany(r => r), result => Assert.True(result.Success, result.Error));
        Assert.True(_server.MaxConcurrentRequests >= 8, $"Maximal {_server.MaxConcurrentRequests} parallel.");
    }

    [Theory]
    [InlineData("", "Inbox", "")]
    [InlineData("in", "Inbox", "")]
    [InlineData("out", "SentItems", "")]
    [InlineData("out/Kunde", "SentItems", "Kunde")]
    [InlineData("in/Ausgang", "Inbox", "Ausgang")]
    [InlineData("in/Spam", "Inbox", "Spam")]
    [InlineData("trash", "DeletedItems", "")]
    [InlineData("spam", "JunkEmail", "")]
    [InlineData("drafts", "Drafts", "")]
    [InlineData("in/Eingang/out", "Inbox", "Eingang/out")]
    [InlineData("cal", "Inbox", "cal")]
    public void Mail_routing_follows_the_technical_root_only(string folderPath, string root, string subfolders)
    {
        var item = new OutputItem(@"user\1\x", "1", "2", "mail", null, null, null, null, null, null, null, null, new Dictionary<string, string?>(), "mail", folderPath);

        var (resolvedRoot, resolvedSubfolders, _) = EwsOutputTarget.ResolveTarget(item);

        Assert.Equal(root, resolvedRoot.ToString());
        Assert.Equal(subfolders, string.Join('/', resolvedSubfolders));
    }

    [Theory]
    [InlineData("calendar", "cal/Privat", "Calendar", "Privat")]
    [InlineData("calendar", "in/Projekt", "Calendar", "Projekt")]
    [InlineData("contact", "address/Team", "Contacts", "Team")]
    [InlineData("task", "todo", "Tasks", "")]
    [InlineData("calendar", "deleteditems", "Calendar", "David-Papierkorb")]
    [InlineData("contact", "trash", "Contacts", "David-Papierkorb")]
    [InlineData("task", "trash", "Tasks", "David-Papierkorb")]
    [InlineData("calendar", "junkemail", "Calendar", "David-Spam")]
    [InlineData("calendar", "drafts", "Calendar", "David-Entwürfe")]
    [InlineData("calendar", "deleteditems/Altprojekt", "Calendar", "David-Papierkorb/Altprojekt")]
    public void Non_mail_kinds_always_use_their_own_root(string kind, string folderPath, string root, string subfolders)
    {
        var item = new OutputItem(@"user\1\x", "1", "7", kind, null, null, null, null, null, null, null, null, new Dictionary<string, string?>(), kind, folderPath);

        var (resolvedRoot, resolvedSubfolders, _) = EwsOutputTarget.ResolveTarget(item);

        Assert.Equal(root, resolvedRoot.ToString());
        Assert.Equal(subfolders, string.Join('/', resolvedSubfolders));
    }

    [Fact]
    public async Task Target_name_contains_the_mailbox()
    {
        await using var target = await OpenAsync();

        Assert.Equal("ews:ziel@example.com", target.Name);
    }

    [Fact]
    public async Task Whole_request_fault_with_an_unmapped_code_looks_up_before_sending_again()
    {
        await using var target = await OpenAsync();
        var isFirst = true;
        _server.Intercept = request =>
        {
            if (request.Operation == "CreateItem" && isFirst)
            {
                isFirst = false;
                return FakeEwsServer.Fault("ErrorProxyRequestProcessingFailed", storeFirst: true);
            }

            return null;
        };
        var items = Enumerable.Range(0, 3).Select(i => Mail($"m{i}")).ToList();

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(3, _server.Items.Count);
        Assert.Contains("FindItem", _server.Operations);
    }

    [Fact]
    public async Task Cut_off_response_counts_as_unknown_outcome_and_creates_no_duplicates()
    {
        await using var target = await OpenAsync();
        var isFirst = true;
        _server.Intercept = request =>
        {
            if (request.Operation == "CreateItem" && isFirst)
            {
                isFirst = false;
                return new FakeReply(StoreFirst: true, Body: "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>");
            }

            return null;
        };
        var items = Enumerable.Range(0, 3).Select(i => Mail($"m{i}")).ToList();

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(3, _server.Items.Select(item => item.ItemKey).Distinct().Count());
        Assert.Equal(3, _server.Items.Count);
    }

    [Fact]
    public async Task Service_unavailable_after_saving_creates_no_duplicates()
    {
        await using var target = await OpenAsync();
        var isFirst = true;
        _server.Intercept = request =>
        {
            if (request.Operation == "CreateItem" && isFirst)
            {
                isFirst = false;
                return new FakeReply(StatusCode: 503, Body: "", StoreFirst: true);
            }

            return null;
        };
        var items = Enumerable.Range(0, 3).Select(i => Mail($"m{i}")).ToList();

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(3, _server.Items.Count);
        Assert.Contains("FindItem", _server.Operations);
    }

    [Fact]
    public async Task Lookup_reads_all_pages_before_calling_an_item_missing()
    {
        await using var target = await OpenAsync();
        var items = Enumerable.Range(0, 3).Select(i => Mail($"m{i}", checkExisting: true)).ToList();
        foreach (var item in items)
        {
            _server.Seed(InboxId, StoredKey(item));
        }

        _server.FindPageLimit = 1;

        var results = await target.WriteBatchAsync(items, CancellationToken.None);

        Assert.All(results, result => Assert.True(result.AlreadyPresent, result.Error));
        Assert.DoesNotContain("CreateItem", _server.Operations);
        Assert.Equal(3, _server.Items.Count);
    }

    [Fact]
    public async Task Oversized_message_is_rejected_once_and_not_uploaded_again()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(StatusCode: 413, Body: "")
            : null;

        var results = await target.WriteBatchAsync(new[] { Mail("gross") }, CancellationToken.None);

        Assert.False(results[0].Success);
        Assert.False(results[0].Retryable);
        Assert.Single(_server.Operations, operation => operation == "CreateItem");
    }

    [Fact]
    public async Task Dropped_connection_gives_the_server_time_before_the_lookup()
    {
        await using var target = await OpenAsync();
        var isFirst = true;
        _server.Intercept = request =>
        {
            if (request.Operation == "CreateItem" && isFirst)
            {
                isFirst = false;
                return new FakeReply(DropConnection: true);
            }

            return null;
        };

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Contains(TimeSpan.FromSeconds(65), _delays);
    }

    [Fact]
    public async Task Throttled_verification_waits_and_reads_again_without_sending_again()
    {
        await using var target = await OpenAsync();
        var isFirst = true;
        _server.Intercept = request =>
        {
            if (request.Operation == "GetItem" && isFirst)
            {
                isFirst = false;
                return new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.GetItemErrors(1, "ErrorServerBusy", 30_000)));
            }

            return null;
        };

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Contains(TimeSpan.FromSeconds(30), _delays);
        Assert.Equal(2, _server.Operations.Count(operation => operation == "GetItem"));
        Assert.Single(_server.Items);
    }

    [Fact]
    public async Task Persistent_throttling_during_verification_stays_retryable_and_sends_only_once()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "GetItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.GetItemErrors(1, "ErrorServerBusy")))
            : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.False(results[0].Success);
        Assert.True(results[0].Retryable);
        Assert.Contains("nicht bestätigt", results[0].Error, StringComparison.Ordinal);
        Assert.Single(_server.Operations, operation => operation == "CreateItem");
    }

    [Theory]
    [InlineData("ErrorAccessDenied")]
    [InlineData("ErrorMailboxLogonFailed")]
    public async Task Access_errors_during_verification_abort_the_mailbox(string code)
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "GetItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.GetItemErrors(1, code)))
            : null;

        await Assert.ThrowsAsync<OutputAbortedException>(
            () => target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None));
    }

    [Fact]
    public async Task No_wait_runs_after_the_last_attempt()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(1, "ErrorServerBusy")))
            : null;

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Retryable);
        Assert.Equal(8, _server.Operations.Count(operation => operation == "CreateItem"));
        Assert.Equal(7, _delays.Count);
    }

    [Fact]
    public async Task Hint_from_the_last_attempt_still_holds_the_next_batch_back()
    {
        await using var target = await OpenAsync();
        _server.Intercept = request => request.Operation == "CreateItem"
            ? new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(1, "ErrorServerBusy", 300_000)))
            : null;
        await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);
        _delays.Clear();
        _server.Intercept = null;

        await target.WriteBatchAsync(new[] { Mail("b") }, CancellationToken.None);

        Assert.Contains(_delays, delay => delay > TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task Waiting_worker_honours_a_pause_that_was_extended_meanwhile()
    {
        var gate = typeof(EwsOutputTarget).GetField("_resumeAtMilliseconds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var waits = new List<TimeSpan>();
        EwsOutputTarget? target = null;
        target = new EwsOutputTarget(
            new EwsOutputOptions("ziel@example.com", _server.Url, "", "", "", "", "Europe/Berlin", 8),
            (_, _) => Task.FromResult("a.token1.c"),
            (delay, _) =>
            {
                waits.Add(delay);
                if (waits.Count == 1)
                {
                    // Während dieser Worker wartet, verlängert ein anderer die Pause.
                    gate.SetValue(target, Environment.TickCount64 + 90_000);
                }

                return Task.CompletedTask;
            });
        await target.OpenAsync(new OutputContext(_directory), CancellationToken.None);
        gate.SetValue(target, Environment.TickCount64 + 30_000);

        await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);
        await target.DisposeAsync();

        Assert.True(waits.Count >= 2);
        Assert.True(waits[1] > TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Short_token_outage_recovers_without_aborting_the_run()
    {
        await using var target = await OpenAsync();
        var calls = 0;
        _token = _ => calls++ == 0 ? throw new HttpRequestException("503") : "a.token1.c";

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
    }

    [Fact]
    public async Task Lasting_token_outage_aborts_the_run_after_the_attempts()
    {
        await using var target = await OpenAsync();
        _token = _ => throw new HttpRequestException("503");

        var error = await Assert.ThrowsAsync<OutputAbortedException>(
            () => target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None));

        Assert.True(error.AffectsAllMailboxes);
        Assert.Equal(7, _delays.Count);
    }

    [Fact]
    public async Task Rejected_credentials_abort_the_run_at_once()
    {
        await using var target = await OpenAsync();
        _token = _ => throw new InvalidOperationException("invalid_client");

        var error = await Assert.ThrowsAsync<OutputAbortedException>(
            () => target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None));

        Assert.True(error.AffectsAllMailboxes);
        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData("ErrorADUnavailable")]
    [InlineData("ErrorConnectionFailed")]
    public async Task Backend_outage_on_an_item_is_retried(string code)
    {
        await using var target = await OpenAsync();
        var isFirst = true;
        _server.Intercept = request =>
        {
            if (request.Operation == "CreateItem" && isFirst)
            {
                isFirst = false;
                return new FakeReply(Body: FakeEwsServer.Envelope(FakeEwsServer.ItemErrors(1, code)));
            }

            return null;
        };

        var results = await target.WriteBatchAsync(new[] { Mail("a") }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
    }

    [Fact]
    public async Task Appointment_in_the_daylight_saving_gap_can_be_saved()
    {
        await using var target = await OpenAsync();
        var properties = new Dictionary<string, string?>
        {
            ["StartTime"] = "2024-03-31T02:30:00",
            ["StopTime"] = "2024-03-31T02:45:00"
        };
        var appointment = new OutputItem(
            @"user\1\cal", "1", "7", "calendar", "Termin", null, null, null, null, null, null, null, properties, "calendar", "cal", false, "box|cal|1");

        var results = await target.WriteBatchAsync(new[] { appointment }, CancellationToken.None);

        Assert.True(results[0].Success, results[0].Error);
        Assert.Single(_server.Items);
    }
}
