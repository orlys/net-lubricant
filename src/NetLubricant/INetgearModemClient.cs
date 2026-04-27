namespace NetLubricant
{
    /// <summary>
    /// Common surface for Netgear LTE modems that share the eternalegypt-style
    /// web API (LM1200, LB2120, LB1120, MR1100, MR6110, ...). Endpoints, session
    /// protocol and JSON schema are near-identical across the family; per-device
    /// differences usually show up as missing <see cref="ModemInfo"/> fields.
    /// </summary>
    /// <remarks>
    /// Typical lifecycle: construct → <see cref="LoginAsync"/> → call SMS / info /
    /// usage methods → <see cref="IDisposable.Dispose"/>. The implementation
    /// internally caches a rotating <c>secToken</c>; callers should not need to
    /// manage authentication state explicitly.
    /// </remarks>
    public interface INetgearModemClient : IDisposable
    {
        /// <summary>
        /// Authenticates against the modem's admin panel and primes the internal
        /// session token. Must be called before any operation that mutates state
        /// (<see cref="SendSmsAsync"/>, <see cref="MarkReadAsync"/>,
        /// <see cref="DeleteAsync"/>, <see cref="RebootAsync"/>,
        /// <see cref="ReconnectAsync"/>) or that requires the Admin role
        /// (<see cref="GetInfoAsync"/>).
        /// </summary>
        /// <param name="ct">Token used to cancel the login HTTP exchange.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the modem rejects the password or returns an unexpected
        /// HTTP status during login.
        /// </exception>
        Task LoginAsync(CancellationToken ct = default);

        /// <summary>
        /// Lightweight reachability probe. Issues a single GET against
        /// <c>/api/model.json</c> (no auth required) under a short timeout and
        /// reports whether the modem responded at all. Any HTTP response — even
        /// 4xx/5xx — counts as online, because the device is still answering.
        /// Connection refused, DNS failure, and timeouts count as offline.
        /// </summary>
        /// <param name="timeout">
        /// Per-call timeout. Defaults to 3 seconds when <see langword="null"/>;
        /// kept short so a yanked device does not block callers for the full
        /// HttpClient timeout.
        /// </param>
        /// <param name="ct">Token used to cancel the probe.</param>
        /// <returns><see langword="true"/> if the modem responded; otherwise <see langword="false"/>.</returns>
        Task<bool> IsOnlineAsync(TimeSpan? timeout = null, CancellationToken ct = default);

        /// <summary>
        /// Reads the inbox (<c>sms.msgs</c>) from <c>/api/model.json</c> and
        /// returns the messages currently stored on the modem. Placeholder/empty
        /// trailing entries that the firmware emits are filtered out.
        /// </summary>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        /// <returns>
        /// Snapshot of received SMS messages. The collection is ordered as the
        /// modem returns it (typically newest-first) and may be empty.
        /// </returns>
        Task<IReadOnlyList<SmsMessage>> ListSmsAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns the modem's outgoing SMS log (<c>sms.sendMsg</c>), including
        /// delivery <see cref="SentSms.Status"/> and <see cref="SentSms.ErrorCode"/>
        /// for each entry. Useful for verifying that a previous
        /// <see cref="SendSmsAsync"/> reached the carrier.
        /// </summary>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        /// <returns>Snapshot of sent SMS records; may be empty.</returns>
        Task<IReadOnlyList<SentSms>> ListSentAsync(CancellationToken ct = default);

        /// <summary>
        /// Sends an SMS via the modem's <c>/Forms/smsSendMsg</c> endpoint. The
        /// modem returns immediately after queuing the message; check
        /// <see cref="ListSentAsync"/> for actual delivery status.
        /// </summary>
        /// <param name="recipient">
        /// Destination phone number, typically in international E.164 form
        /// (e.g. <c>+886912345678</c>). Format requirements depend on the carrier.
        /// </param>
        /// <param name="text">Message body. Length limits follow standard SMS rules.</param>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the modem returns a non-success HTTP status.
        /// </exception>
        Task SendSmsAsync(string recipient, string text, CancellationToken ct = default);

        /// <summary>
        /// Marks the SMS with the given identifier as read by posting
        /// <c>sms.readId</c> to <c>/Forms/config</c>. No-op if the id does not
        /// exist (the modem still returns success).
        /// </summary>
        /// <param name="id">Inbox message id, as exposed by <see cref="SmsMessage.Id"/>.</param>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        Task MarkReadAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// Deletes the SMS with the given identifier from the inbox by posting
        /// <c>sms.deleteId</c> to <c>/Forms/config</c>.
        /// </summary>
        /// <param name="id">Inbox message id, as exposed by <see cref="SmsMessage.Id"/>.</param>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        Task DeleteAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// Polls the inbox until a message satisfying <paramref name="predicate"/>
        /// arrives, then returns it. Designed for OTP / 3DS flows: pass a predicate
        /// that filters by sender, regex, or arrival time.
        /// </summary>
        /// <remarks>
        /// Implementations seed the "already seen" set with the current inbox at
        /// invocation time, so only messages received <em>after</em> this call is
        /// made will be considered.
        /// </remarks>
        /// <param name="predicate">
        /// Filter applied to each newly arrived message. The first message for
        /// which this returns <see langword="true"/> is returned.
        /// </param>
        /// <param name="timeout">Maximum wall-clock wait before giving up.</param>
        /// <param name="pollInterval">
        /// How often to refresh the inbox. Defaults to 3 seconds when
        /// <see langword="null"/>.
        /// </param>
        /// <param name="ct">Token used to cancel polling.</param>
        /// <returns>The first message that matched <paramref name="predicate"/>.</returns>
        /// <exception cref="TimeoutException">
        /// Thrown when no matching message arrives within <paramref name="timeout"/>.
        /// </exception>
        Task<SmsMessage> WaitForSmsAsync(
            Func<SmsMessage, bool> predicate,
            TimeSpan timeout,
            TimeSpan? pollInterval = null,
            CancellationToken ct = default);

        /// <summary>
        /// Reads device, SIM, WWAN and radio diagnostics from <c>/api/model.json</c>
        /// and projects them into a strongly-typed <see cref="ModemInfo"/>.
        /// Requires Admin session — call <see cref="LoginAsync"/> first.
        /// </summary>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        /// <returns>
        /// Aggregate device info. Fields not exposed by the current firmware are
        /// returned as empty strings or zero rather than thrown errors.
        /// </returns>
        Task<ModemInfo> GetInfoAsync(CancellationToken ct = default);

        /// <summary>
        /// Soft-reboots the modem via <c>general.reboot</c>. Returns immediately
        /// after the POST is acknowledged; the device will drop the connection
        /// shortly after, so transient <see cref="HttpRequestException"/> /
        /// <see cref="TaskCanceledException"/> during the response are expected
        /// and swallowed by implementations.
        /// </summary>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        Task RebootAsync(CancellationToken ct = default);

        /// <summary>
        /// Reads <c>wwan.dataUsage</c> counters (bytes rx/tx/total, plus optional
        /// monthly limit and billing cycle start). Fields may be zero or empty on
        /// firmwares that don't populate them.
        /// </summary>
        /// <param name="ct">Token used to cancel the underlying HTTP request.</param>
        /// <returns>Aggregated data-usage snapshot.</returns>
        Task<UsageInfo> GetUsageAsync(CancellationToken ct = default);

        /// <summary>
        /// Forces an LTE re-attach by posting <c>wwan.disconnect</c> followed by
        /// <c>wwan.connect</c> against the <c>DefaultProfile</c>. Useful when
        /// signalling is stuck or the modem is registered but data is not flowing.
        /// </summary>
        /// <param name="ct">Token used to cancel the underlying HTTP requests.</param>
        Task ReconnectAsync(CancellationToken ct = default);
    }
}
