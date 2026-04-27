namespace NetLubricant
{
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Client for Netgear LTE modems that share the eternalegypt-style admin API
    /// (LM1200, LB2120, LB1120, MR1100, MR6110...). Supports reading, sending,
    /// and deleting SMS — useful as a 3DS OTP sink for automated payment flows.
    /// Call <see cref="GetInfoAsync"/> to confirm the actual device model.
    /// </summary>
    /// <remarks>
    /// Protocol (reverse-engineered, matches svbnet/netgear-sms and fidpa's poller):
    /// 1. GET /api/model.json → session.secToken (no auth, gives Guest role)
    /// 2. POST /Forms/config with session.password + token → 302, sets session cookie
    /// 3. GET /api/model.json again → userRole=Admin, sms.msgs[] populated
    /// Every subsequent config POST (markRead/delete) must re-read secToken because
    /// the modem rotates it per-request.
    /// </remarks>
    public class NetgearModemClient : INetgearModemClient
    {
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly Uri _base;
        private readonly string _password;
        private string? _token;

        public NetgearModemClient(string host, string password)
        {
            _base = new Uri($"http://{host}/");
            _password = password;
            _handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
            _http = new HttpClient(_handler) { BaseAddress = _base, Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task LoginAsync(CancellationToken ct = default)
        {
            var first = await GetModelAsync(ct).ConfigureAwait(false);
            if (first.Session.UserRole == "Admin")
            {
                _token = first.Session.SecToken;
                return;
            }

            var form = new Dictionary<string, string>
            {
                ["session.password"] = _password,
                ["token"] = first.Session.SecToken,
                ["ok_redirect"] = "/success.json",
                ["err_redirect"] = "/error.json",
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "/Forms/config") { Content = new FormUrlEncodedContent(form) };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            // 302 → /success.json on success, /error.json on failure. HttpClient auto-follows;
            // status code is always 200 after the redirect, so we have to inspect the landing URL.
            if ((int)resp.StatusCode is not (200 or 204 or 302))
                throw new InvalidOperationException($"Netgear login failed: HTTP {(int)resp.StatusCode}");
            if (LandedOnError(resp))
                throw new InvalidOperationException("Netgear login rejected (redirected to error.json). Wrong password?");

            var second = await GetModelAsync(ct).ConfigureAwait(false);
            if (second.Session.UserRole != "Admin")
                throw new InvalidOperationException("Netgear login rejected (userRole still Guest). Wrong password?");
            _token = second.Session.SecToken;
        }

        private static bool LandedOnError(HttpResponseMessage resp) =>
            resp.RequestMessage?.RequestUri?.AbsolutePath
                ?.EndsWith("/error.json", StringComparison.OrdinalIgnoreCase) == true;

        public async Task<bool> IsOnlineAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
            try
            {
                using var resp = await _http.GetAsync("/api/model.json", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<SmsMessage>> ListSmsAsync(CancellationToken ct = default)
        {
            var model = await GetModelAsync(ct).ConfigureAwait(false);
            _token = model.Session.SecToken;
            var msgs = model.Sms.Messages ?? Array.Empty<SmsMessage>();
            // The modem serializes an empty array as `[{}]`; filter out placeholder entries.
            return msgs.Where(m => m.Id > 0 && !string.IsNullOrEmpty(m.Text)).ToArray();
        }

        public async Task<ModemInfo> GetInfoAsync(CancellationToken ct = default)
        {
            // model.json exposes sim/wwan/wwanadv/general only to Admin — LoginAsync
            // must run first. We re-parse with a richer root here rather than reusing
            // GetModelAsync so SmsContainer doesn't need extra fields.
            using var resp = await _http.GetAsync("/api/model.json", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string S(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt32() : 0;
            bool B(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;

            var sim = root.GetProperty("sim");
            var wwan = root.GetProperty("wwan");
            var wwanadv = root.GetProperty("wwanadv");
            var general = root.GetProperty("general");

            return new ModemInfo(
                Model: S(general, "model"),
                Manufacturer: S(general, "manufacturer"),
                FirmwareVersion: S(general, "FWversion"),
                AppVersion: S(general, "appVersion"),
                Imei: S(general, "IMEI"),
                DeviceTempC: I(general, "devTemperature"),
                PhoneNumber: S(sim, "phoneNumber"),
                Iccid: S(sim, "iccid"),
                Imsi: S(sim, "imsi"),
                SimStatus: S(sim, "status"),
                PinMode: sim.TryGetProperty("pin", out var pin) ? S(pin, "mode") : "",
                Carrier: S(wwan, "registerNetworkDisplay"),
                ConnectionStatus: S(wwan, "connection"),
                Roaming: B(wwan, "roaming"),
                PublicIp: S(wwan, "IP"),
                RadioAccessTech: S(wwan, "RAT"),
                Band: S(wwanadv, "curBand"),
                RadioQuality: I(wwanadv, "radioQuality"),
                RxLevelDbm: I(wwanadv, "rxLevel"),
                Mcc: S(wwanadv, "MCC"),
                Mnc: S(wwanadv, "MNC"),
                CellId: I(wwanadv, "cellId"));
        }

        public async Task<IReadOnlyList<SentSms>> ListSentAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync("/api/model.json", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("sms", out var sms) ||
                !sms.TryGetProperty("sendMsg", out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return Array.Empty<SentSms>();

            var results = new List<SentSms>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("msgId", out var idEl)) continue; // skip empty trailing {}
                results.Add(new SentSms(
                    MsgId: idEl.GetInt32(),
                    Receiver: item.TryGetProperty("receiver", out var r) ? r.GetString() ?? "" : "",
                    Text: item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    Status: item.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                    ErrorCode: item.TryGetProperty("errorCode", out var ec) ? ec.GetInt32() : 0,
                    ClientId: item.TryGetProperty("clientId", out var c) ? c.GetString() ?? "" : "",
                    TxTime: item.TryGetProperty("txTime", out var tx) ? tx.GetString() ?? "" : ""));
            }
            return results;
        }

        public Task SendSmsAsync(string recipient, string text, CancellationToken ct = default) =>
            SendSmsCoreAsync(recipient, text, isRetry: false, ct);

        private async Task SendSmsCoreAsync(string recipient, string text, bool isRetry, CancellationToken ct)
        {
            if (_token is null) await LoginAsync(ct).ConfigureAwait(false);
            var form = new Dictionary<string, string>
            {
                ["sms.sendMsg.receiver"] = recipient,
                ["sms.sendMsg.text"] = text,
                ["sms.sendMsg.clientId"] = "q.netgear",
                ["action"] = "send",
                ["token"] = _token!,
                ["ok_redirect"] = "/success.json",
                ["err_redirect"] = "/error.json",
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "/Forms/smsSendMsg") { Content = new FormUrlEncodedContent(form) };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is not (200 or 204 or 302))
                throw new InvalidOperationException($"Netgear send SMS failed: HTTP {(int)resp.StatusCode}");
            if (LandedOnError(resp))
            {
                if (await TryRefreshSessionAsync(isRetry, ct).ConfigureAwait(false))
                {
                    await SendSmsCoreAsync(recipient, text, isRetry: true, ct).ConfigureAwait(false);
                    return;
                }
                throw new InvalidOperationException("Netgear send SMS rejected (redirected to error.json) after relogin retry.");
            }
            var next = await GetModelAsync(ct).ConfigureAwait(false);
            _token = next.Session.SecToken;
        }

        public Task MarkReadAsync(int id, CancellationToken ct = default) =>
            PostConfigAsync(new() { ["sms.readId"] = id.ToString() }, ct);

        public Task DeleteAsync(int id, CancellationToken ct = default) =>
            PostConfigAsync(new() { ["sms.deleteId"] = id.ToString() }, ct);

        public async Task RebootAsync(CancellationToken ct = default)
        {
            if (_token is null) await LoginAsync(ct).ConfigureAwait(false);
            var form = new Dictionary<string, string>
            {
                ["general.reboot"] = "true",
                ["token"] = _token!,
                ["ok_redirect"] = "/success.json",
                ["err_redirect"] = "/error.json",
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "/Forms/config") { Content = new FormUrlEncodedContent(form) };
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                // Modem may tear down the connection mid-response; don't treat that as failure.
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            _token = null;
        }

        public async Task ReconnectAsync(CancellationToken ct = default)
        {
            await PostConfigAsync(new() { ["wwan.disconnect"] = "DefaultProfile" }, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await PostConfigAsync(new() { ["wwan.connect"] = "DefaultProfile" }, ct).ConfigureAwait(false);
        }

        public async Task<UsageInfo> GetUsageAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync("/api/model.json", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // LM1200/LB2120 expose wwan.dataUsage.generic; MR1100 may add .roaming.
            long L(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : 0L;
            string S(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() ?? "" : "";

            JsonElement generic = default, limit = default;
            bool hasGeneric = false, hasLimit = false;
            if (root.TryGetProperty("wwan", out var wwan) && wwan.TryGetProperty("dataUsage", out var usage))
            {
                hasGeneric = usage.TryGetProperty("generic", out generic);
                hasLimit = usage.TryGetProperty("dataLimit", out limit);
            }

            long rx = hasGeneric ? L(generic, "dataTransferredRx") : 0;
            long tx = hasGeneric ? L(generic, "dataTransferredTx") : 0;
            long total = hasGeneric ? L(generic, "dataTransferred") : rx + tx;
            long limitBytes = hasLimit ? L(limit, "limit") : 0;
            string billingStart = hasLimit ? S(limit, "billingStart") : "";
            string unit = hasLimit ? S(limit, "unit") : "";

            return new UsageInfo(
                BytesRx: rx,
                BytesTx: tx,
                BytesTotal: total,
                LimitBytes: limitBytes,
                LimitUnit: unit,
                BillingCycleStart: billingStart);
        }

        /// <summary>
        /// Polls until an SMS matching <paramref name="predicate"/> arrives, then returns it.
        /// Use for 3DS OTP: pass a predicate that checks sender or regex over the text.
        /// </summary>
        public async Task<SmsMessage> WaitForSmsAsync(
            Func<SmsMessage, bool> predicate,
            TimeSpan timeout,
            TimeSpan? pollInterval = null,
            CancellationToken ct = default)
        {
            var interval = pollInterval ?? TimeSpan.FromSeconds(3);
            var deadline = DateTime.UtcNow + timeout;
            var seenIds = new HashSet<int>();
            // Seed with existing IDs so we only pick up messages that arrive after this call.
            foreach (var m in await ListSmsAsync(ct).ConfigureAwait(false)) seenIds.Add(m.Id);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var now = await ListSmsAsync(ct).ConfigureAwait(false);
                foreach (var m in now)
                {
                    if (seenIds.Contains(m.Id)) continue;
                    seenIds.Add(m.Id);
                    if (predicate(m)) return m;
                }
            }
            throw new TimeoutException($"No matching SMS arrived within {timeout}.");
        }

        private Task PostConfigAsync(Dictionary<string, string> fields, CancellationToken ct) =>
            PostConfigCoreAsync(fields, isRetry: false, ct);

        private async Task PostConfigCoreAsync(Dictionary<string, string> fields, bool isRetry, CancellationToken ct)
        {
            if (_token is null) await LoginAsync(ct).ConfigureAwait(false);
            // Copy so a retry can resend the original payload with a freshly rotated token.
            var sendFields = new Dictionary<string, string>(fields) { ["token"] = _token! };
            sendFields.TryAdd("ok_redirect", "/success.json");
            sendFields.TryAdd("err_redirect", "/error.json");
            using var req = new HttpRequestMessage(HttpMethod.Post, "/Forms/config") { Content = new FormUrlEncodedContent(sendFields) };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is not (200 or 204 or 302))
                throw new InvalidOperationException($"Netgear config POST failed: HTTP {(int)resp.StatusCode}");
            if (LandedOnError(resp))
            {
                if (await TryRefreshSessionAsync(isRetry, ct).ConfigureAwait(false))
                {
                    await PostConfigCoreAsync(fields, isRetry: true, ct).ConfigureAwait(false);
                    return;
                }
                throw new InvalidOperationException("Netgear config POST rejected (redirected to error.json) after relogin retry.");
            }
            // secToken rotates; refresh.
            var next = await GetModelAsync(ct).ConfigureAwait(false);
            _token = next.Session.SecToken;
        }

        // Drop the cached token and re-login. Returns false if we already retried this call,
        // so the caller can surface the underlying error instead of looping.
        private async Task<bool> TryRefreshSessionAsync(bool alreadyRetried, CancellationToken ct)
        {
            if (alreadyRetried) return false;
            _token = null;
            await LoginAsync(ct).ConfigureAwait(false);
            return true;
        }

        private async Task<ModelRoot> GetModelAsync(CancellationToken ct)
        {
            // Netgear serves model.json as text/plain; we parse manually.
            using var resp = await _http.GetAsync("/api/model.json", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ModelRoot>(text, SerializerOptions)
                ?? throw new InvalidOperationException("Netgear model.json returned null.");
        }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        public virtual void Dispose()
        {
            _http.Dispose();
            _handler.Dispose();
        }

        private sealed record ModelRoot(
            [property: JsonPropertyName("session")] SessionInfo Session,
            [property: JsonPropertyName("sms")] SmsContainer Sms);

        private sealed record SessionInfo(
            [property: JsonPropertyName("userRole")] string UserRole,
            [property: JsonPropertyName("secToken")] string SecToken);

        private sealed record SmsContainer(
            [property: JsonPropertyName("msgCount")] int MsgCount,
            [property: JsonPropertyName("unreadMsgs")] int UnreadCount,
            [property: JsonPropertyName("msgs")] SmsMessage[]? Messages);
    }

    public sealed record SentSms(int MsgId, string Receiver, string Text, string Status, int ErrorCode, string ClientId, string TxTime);

    public sealed record UsageInfo(
        long BytesRx, long BytesTx, long BytesTotal,
        long LimitBytes, string LimitUnit, string BillingCycleStart);

    public sealed record ModemInfo(
        string Model, string Manufacturer, string FirmwareVersion, string AppVersion,
        string Imei, int DeviceTempC,
        string PhoneNumber, string Iccid, string Imsi, string SimStatus, string PinMode,
        string Carrier, string ConnectionStatus, bool Roaming, string PublicIp, string RadioAccessTech,
        string Band, int RadioQuality, int RxLevelDbm, string Mcc, string Mnc, int CellId);

    public sealed record SmsMessage(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("sender")] string Sender,
        [property: JsonPropertyName("rxTime")] string RxTime,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("read")] bool Read);
}
