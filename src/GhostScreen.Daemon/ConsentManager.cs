namespace GhostScreen.Daemon;

public enum InteractionPolicy
{
    /// <summary>Block focus-requiring UIA actions while a human is active; ask after retries.</summary>
    Strict,

    /// <summary>Execute UIA actions and restore foreground (no consent prompt).</summary>
    Balanced,

    /// <summary>Execute UIA actions immediately; focus restoration remains best-effort.</summary>
    Force,
}

public static class InteractionPolicyExtensions
{
    /// <summary>Reads GHOSTSCREEN_INTERACTION_POLICY; defaults to strict.</summary>
    public static InteractionPolicy FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("GHOSTSCREEN_INTERACTION_POLICY");
        return value?.Trim().ToLowerInvariant() switch
        {
            "balanced" => InteractionPolicy.Balanced,
            "force" => InteractionPolicy.Force,
            _ => InteractionPolicy.Strict,
        };
    }
}

public enum GateOutcome
{
    Retry,
    AwaitingUser,
    Allowed,
    Denied,
    Expired,
}

public sealed record ConsentGateResult(
    GateOutcome Outcome,
    string? ConsentId,
    DateTimeOffset? ExpiresAt,
    int Attempts,
    int MaxAttempts,
    int RetryAfterMs,
    string? Message = null,
    string? Activity = null,
    string? ActivityDetail = null,
    string? Interruption = null,
    long IdleMs = 0);

public sealed record ConsentView(
    string Id,
    string TargetId,
    string TargetTitle,
    string Action,
    string ElementKey,
    string Agent,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    long RemainingMs,
    string Status,
    long TotalMs,
    int Deferrals,
    int MaxDeferrals,
    string Activity,
    string? ActivityDetail,
    string Interruption,
    long IdleMs = 0);

/// <summary>Result of pushing a pending consent's deadline back by one step.</summary>
public sealed record ConsentDeferResult(
    bool Deferred,
    GateOutcome Outcome,
    DateTimeOffset? AutoAllowAt,
    long RemainingMs,
    long TotalMs,
    int Deferrals,
    int MaxDeferrals,
    string? Message = null);

/// <summary>
/// Human Consent Gate: tracks focus-requiring UIA attempts, creates a consent
/// request after a few blocked retries, and auto-authorizes it after a visible
/// countdown. The agent executes the action itself after receiving a consent id;
/// this class only authorizes, so a stale pending action is never replayed.
/// </summary>
public sealed class ConsentManager
{
    private enum State
    {
        Pending,
        Allowed,
        Denied,
        Consumed,
        Expired,
    }

    private sealed class ConsentRequest
    {
        public required string Id;
        public required string Key;
        public required string TargetId;
        public required string TargetTitle;
        public required string Action;
        public required string ElementKey;
        public required string Agent;
        public required DateTimeOffset CreatedAt;
        public required DateTimeOffset AutoAllowAt;

        /// <summary>What the human was doing when the gate blocked: typing / pointing / active.</summary>
        public required string Activity;

        /// <summary>Foreground window title at block time, used to tell the human what to stop.</summary>
        public string? ActivityDetail;

        /// <summary>What the action needs from the human: taking the foreground.</summary>
        public required string Interruption;

        /// <summary>Milliseconds since the last human input; lets the agent judge urgency.</summary>
        public long IdleMs;

        /// <summary>Start of the current countdown window; moves with every deferral.</summary>
        public DateTimeOffset WindowStart;
        public int Attempts;
        public int Deferrals;
        public DateTimeOffset AllowedUntil;
        public State State = State.Pending;
    }

    private sealed class AttemptRecord
    {
        public DateTimeOffset FirstAt;
        public DateTimeOffset LastAt;
        public int Count;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ConsentRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttemptRecord> _attempts = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public ConsentManager(
        Func<DateTimeOffset>? now = null,
        TimeSpan? autoAllowAfter = null,
        TimeSpan? consentTtl = null,
        int maxAttempts = 3,
        TimeSpan? attemptWindow = null,
        int maxDeferrals = 3,
        TimeSpan? deferIncrement = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        AutoAllowAfter = autoAllowAfter ?? TimeSpan.FromSeconds(15);
        ConsentTtl = consentTtl ?? TimeSpan.FromSeconds(30);
        MaxAttempts = Math.Max(1, maxAttempts);
        AttemptWindow = attemptWindow ?? TimeSpan.FromSeconds(60);
        MaxDeferrals = Math.Max(0, maxDeferrals);
        DeferIncrement = deferIncrement ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan AutoAllowAfter { get; }

    public TimeSpan ConsentTtl { get; }

    public int MaxAttempts { get; }

    public TimeSpan AttemptWindow { get; }

    /// <summary>How many times a human may push the countdown back from the prompt.</summary>
    public int MaxDeferrals { get; }

    /// <summary>How much one "defer" click adds to the countdown.</summary>
    public TimeSpan DeferIncrement { get; }

    /// <summary>Shown in the human prompt; set from MCP clientInfo.</summary>
    public string AgentName { get; set; } = "agent";

    /// <summary>
    /// Record a blocked UIA attempt. Returns Retry before the threshold and an
    /// AwaitingUser ticket at the threshold (or the existing pending ticket).
    /// <paramref name="activity"/> describes what the human is doing (the gate
    /// only knows "input happened" plus a typing/pointing inference), so the
    /// prompt can ask for an interruption instead of a bare permission.
    /// </summary>
    public ConsentGateResult Evaluate(
        string targetId,
        string targetTitle,
        string action,
        string elementKey,
        string activity = "active",
        string? activityDetail = null,
        string interruption = "foreground",
        long idleMs = 0)
    {
        lock (_gate)
        {
            var now = _now();
            var key = MakeKey(targetId, action, elementKey);

            if (_requests.TryGetValue(key, out var existing))
            {
                var state = Resolve(existing, now);
                if (state is State.Expired or State.Consumed ||
                    (state == State.Denied && now - existing.CreatedAt > AttemptWindow))
                {
                    _requests.Remove(key);
                    _attempts.Remove(key);
                    return StartAttempts(
                        key, targetId, targetTitle, action, elementKey, now, activity, activityDetail, interruption, idleMs);
                }

                return state switch
                {
                    State.Pending => Awaiting(existing, GateOutcome.AwaitingUser, now, "Human consent is pending."),
                    State.Allowed => Awaiting(
                        existing,
                        GateOutcome.AwaitingUser,
                        now,
                        "Human consent was granted; retry with consent_id."),
                    State.Denied => new ConsentGateResult(
                        GateOutcome.Denied,
                        existing.Id,
                        null,
                        existing.Attempts,
                        MaxAttempts,
                        0,
                        "The human denied this action.",
                        existing.Activity,
                        existing.ActivityDetail,
                        existing.Interruption,
                        existing.IdleMs),
                    _ => StartAttempts(
                        key, targetId, targetTitle, action, elementKey, now, activity, activityDetail, interruption, idleMs),
                };
            }

            return StartAttempts(
                key, targetId, targetTitle, action, elementKey, now, activity, activityDetail, interruption, idleMs);
        }
    }

    /// <summary>Redeem a consent id for exactly this target/action/element.</summary>
    public ConsentGateResult Consume(
        string consentId,
        string targetId,
        string action,
        string elementKey)
    {
        lock (_gate)
        {
            var now = _now();
            var request = _requests.Values.FirstOrDefault(candidate =>
                candidate.Id.Equals(consentId, StringComparison.Ordinal));

            if (request is null)
            {
                return new ConsentGateResult(
                    GateOutcome.Expired,
                    consentId,
                    null,
                    0,
                    MaxAttempts,
                    0,
                    "Unknown consent_id; request a new consent.");
            }

            if (!request.Key.Equals(MakeKey(targetId, action, elementKey), StringComparison.Ordinal))
            {
                return new ConsentGateResult(
                    GateOutcome.Expired,
                    consentId,
                    null,
                    request.Attempts,
                    MaxAttempts,
                    0,
                    "consent_id does not match this target/action/element.");
            }

            var state = Resolve(request, now);
            switch (state)
            {
                case State.Pending:
                    return Awaiting(request, GateOutcome.AwaitingUser, now, "Human consent is still pending.");
                case State.Denied:
                    return new ConsentGateResult(
                        GateOutcome.Denied,
                        consentId,
                        null,
                        request.Attempts,
                        MaxAttempts,
                        0,
                        "The human denied this action.");
                case State.Allowed:
                    request.State = State.Consumed;
                    _requests.Remove(request.Key);
                    _attempts.Remove(request.Key);
                    return new ConsentGateResult(
                        GateOutcome.Allowed,
                        consentId,
                        null,
                        request.Attempts,
                        MaxAttempts,
                        0);
                default:
                    _requests.Remove(request.Key);
                    return new ConsentGateResult(
                        GateOutcome.Expired,
                        consentId,
                        null,
                        request.Attempts,
                        MaxAttempts,
                        0,
                        "The consent ticket expired; request a new consent.");
            }
        }
    }

    public ConsentGateResult Decide(string consentId, bool allow)
    {
        lock (_gate)
        {
            var now = _now();
            var request = _requests.Values.FirstOrDefault(candidate =>
                candidate.Id.Equals(consentId, StringComparison.Ordinal));

            if (request is null)
            {
                return new ConsentGateResult(
                    GateOutcome.Expired,
                    consentId,
                    null,
                    0,
                    MaxAttempts,
                    0,
                    "Unknown consent_id.");
            }

            var state = Resolve(request, now);
            if (state == State.Pending)
            {
                request.State = allow ? State.Allowed : State.Denied;
                if (allow)
                {
                    request.AllowedUntil = now + ConsentTtl;
                }

                return new ConsentGateResult(
                    allow ? GateOutcome.Allowed : GateOutcome.Denied,
                    request.Id,
                    allow ? request.AllowedUntil : null,
                    request.Attempts,
                    MaxAttempts,
                    0);
            }

            return state switch
            {
                State.Allowed => new ConsentGateResult(
                    GateOutcome.Allowed,
                    request.Id,
                    request.AllowedUntil,
                    request.Attempts,
                    MaxAttempts,
                    0),
                State.Denied => new ConsentGateResult(
                    GateOutcome.Denied,
                    request.Id,
                    null,
                    request.Attempts,
                    MaxAttempts,
                    0),
                _ => new ConsentGateResult(
                    GateOutcome.Expired,
                    request.Id,
                    null,
                    request.Attempts,
                    MaxAttempts,
                    0,
                    "The consent ticket is no longer pending."),
            };
        }
    }

    /// <summary>
    /// Push a pending consent's auto-allow deadline back by <see cref="DeferIncrement"/>,
    /// so a human who is still reading is not auto-authorized mid-sentence. Bounded by
    /// <see cref="MaxDeferrals"/> so an agent can never be stalled indefinitely.
    /// Returns null when the id is unknown.
    /// </summary>
    public ConsentDeferResult? Defer(string consentId)
    {
        lock (_gate)
        {
            var now = _now();
            var request = _requests.Values.FirstOrDefault(candidate =>
                candidate.Id.Equals(consentId, StringComparison.Ordinal));

            if (request is null)
            {
                return null;
            }

            var state = Resolve(request, now);
            if (state != State.Pending)
            {
                return new ConsentDeferResult(
                    false,
                    state == State.Denied ? GateOutcome.Denied : GateOutcome.Expired,
                    null,
                    0,
                    0,
                    request.Deferrals,
                    MaxDeferrals,
                    "The consent is no longer pending; defer is too late.");
            }

            if (request.Deferrals >= MaxDeferrals)
            {
                return new ConsentDeferResult(
                    false,
                    GateOutcome.AwaitingUser,
                    request.AutoAllowAt,
                    Math.Max(0, (long)(request.AutoAllowAt - now).TotalMilliseconds),
                    WindowTotalMs(request),
                    request.Deferrals,
                    MaxDeferrals,
                    "The deferral limit is reached; allow or deny instead.");
            }

            request.Deferrals++;
            request.AutoAllowAt += DeferIncrement;
            request.WindowStart = now;
            var remainingMs = Math.Max(0, (long)(request.AutoAllowAt - now).TotalMilliseconds);

            return new ConsentDeferResult(
                true,
                GateOutcome.AwaitingUser,
                request.AutoAllowAt,
                remainingMs,
                remainingMs,
                request.Deferrals,
                MaxDeferrals,
                null);
        }
    }

    public IReadOnlyList<ConsentView> Pending()
    {
        lock (_gate)
        {
            var now = _now();
            var result = new List<ConsentView>();
            foreach (var request in _requests.Values)
            {
                if (Resolve(request, now) != State.Pending)
                {
                    continue;
                }

                result.Add(ToView(request, now));
            }

            return result.OrderBy(view => view.CreatedAt).ToArray();
        }
    }

    public ConsentView? Get(string consentId)
    {
        lock (_gate)
        {
            var request = _requests.Values.FirstOrDefault(candidate =>
                candidate.Id.Equals(consentId, StringComparison.Ordinal));
            return request is null ? null : ToView(request, _now());
        }
    }

    private ConsentGateResult StartAttempts(
        string key,
        string targetId,
        string targetTitle,
        string action,
        string elementKey,
        DateTimeOffset now,
        string activity,
        string? activityDetail,
        string interruption,
        long idleMs)
    {
        if (!_attempts.TryGetValue(key, out var attempt) || now - attempt.FirstAt > AttemptWindow)
        {
            attempt = new AttemptRecord { FirstAt = now };
        }

        attempt.Count++;
        attempt.LastAt = now;
        _attempts[key] = attempt;

        if (attempt.Count < MaxAttempts)
        {
            var delay = RetryDelay(attempt.Count);
            return new ConsentGateResult(
                GateOutcome.Retry,
                null,
                null,
                attempt.Count,
                MaxAttempts,
                delay,
                $"The human is {ActivityPhrase(activity, activityDetail)}; retry after {delay} ms.",
                activity,
                activityDetail,
                interruption,
                idleMs);
        }

        var request = new ConsentRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Key = key,
            TargetId = targetId,
            TargetTitle = targetTitle,
            Action = action,
            ElementKey = elementKey,
            Agent = AgentName,
            CreatedAt = now,
            AutoAllowAt = now + AutoAllowAfter,
            WindowStart = now,
            Attempts = attempt.Count,
            Activity = activity,
            ActivityDetail = activityDetail,
            Interruption = interruption,
            IdleMs = idleMs,
        };
        _requests[key] = request;

        return new ConsentGateResult(
            GateOutcome.AwaitingUser,
            request.Id,
            request.AutoAllowAt,
            attempt.Count,
            MaxAttempts,
            2_000,
            "A consent prompt was shown; retry with consent_id after it is allowed or the countdown expires.",
            activity,
            activityDetail,
            interruption,
            idleMs);
    }

    private ConsentGateResult Awaiting(
        ConsentRequest request,
        GateOutcome outcome,
        DateTimeOffset now,
        string message) =>
        new(
            outcome,
            request.Id,
            request.State == State.Pending ? request.AutoAllowAt : request.AllowedUntil,
            request.Attempts,
            MaxAttempts,
            2_000,
            message,
            request.Activity,
            request.ActivityDetail,
            request.Interruption,
            request.IdleMs);

    private State Resolve(ConsentRequest request, DateTimeOffset now)
    {
        switch (request.State)
        {
            case State.Pending when now >= request.AutoAllowAt:
                request.State = State.Allowed;
                request.AllowedUntil = request.AutoAllowAt + ConsentTtl;
                break;
            case State.Allowed when now > request.AllowedUntil:
                request.State = State.Expired;
                break;
        }

        return request.State;
    }

    private ConsentView ToView(ConsentRequest request, DateTimeOffset now)
    {
        var expires = request.State == State.Pending ? request.AutoAllowAt : request.AllowedUntil;
        return new ConsentView(
            request.Id,
            request.TargetId,
            request.TargetTitle,
            request.Action,
            request.ElementKey,
            request.Agent,
            request.CreatedAt,
            expires,
            Math.Max(0, (long)(expires - now).TotalMilliseconds),
            request.State.ToString().ToLowerInvariant(),
            WindowTotalMs(request),
            request.Deferrals,
            MaxDeferrals,
            request.Activity,
            request.ActivityDetail,
            request.Interruption,
            request.IdleMs);
    }

    /// <summary>Plain-English description of the pointer-free activity token.</summary>
    private static string ActivityPhrase(string activity, string? detail) => activity switch
    {
        "typing" => detail is null ? "typing" : $"typing in \"{detail}\"",
        "pointing" => detail is null ? "using the pointer" : $"clicking in \"{detail}\"",
        _ => detail is null ? "using the desktop" : $"using \"{detail}\"",
    };

    /// <summary>Length of the countdown window currently running, for the progress bar.</summary>
    private static long WindowTotalMs(ConsentRequest request) =>
        request.State == State.Pending
            ? Math.Max(0, (long)(request.AutoAllowAt - request.WindowStart).TotalMilliseconds)
            : 0;

    private static string MakeKey(string targetId, string action, string elementKey) =>
        $"{targetId}\u001f{action}\u001f{elementKey}";

    private static int RetryDelay(int attempt) => 1_500 * Math.Max(1, attempt);
}
