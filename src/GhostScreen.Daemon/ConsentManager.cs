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
    string? Message = null);

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
    string Status);

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
        public required int Attempts;
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
        TimeSpan? attemptWindow = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        AutoAllowAfter = autoAllowAfter ?? TimeSpan.FromSeconds(15);
        ConsentTtl = consentTtl ?? TimeSpan.FromSeconds(30);
        MaxAttempts = Math.Max(1, maxAttempts);
        AttemptWindow = attemptWindow ?? TimeSpan.FromSeconds(60);
    }

    public TimeSpan AutoAllowAfter { get; }

    public TimeSpan ConsentTtl { get; }

    public int MaxAttempts { get; }

    public TimeSpan AttemptWindow { get; }

    /// <summary>Shown in the human prompt; set from MCP clientInfo.</summary>
    public string AgentName { get; set; } = "agent";

    /// <summary>
    /// Record a blocked UIA attempt. Returns Retry before the threshold and an
    /// AwaitingUser ticket at the threshold (or the existing pending ticket).
    /// </summary>
    public ConsentGateResult Evaluate(
        string targetId,
        string targetTitle,
        string action,
        string elementKey)
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
                    return StartAttempts(key, targetId, targetTitle, action, elementKey, now);
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
                        "The human denied this action."),
                    _ => StartAttempts(key, targetId, targetTitle, action, elementKey, now),
                };
            }

            return StartAttempts(key, targetId, targetTitle, action, elementKey, now);
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
        DateTimeOffset now)
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
            return new ConsentGateResult(
                GateOutcome.Retry,
                null,
                null,
                attempt.Count,
                MaxAttempts,
                RetryDelay(attempt.Count),
                $"Human is using the desktop; retry after {RetryDelay(attempt.Count)} ms.");
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
            Attempts = attempt.Count,
        };
        _requests[key] = request;

        return new ConsentGateResult(
            GateOutcome.AwaitingUser,
            request.Id,
            request.AutoAllowAt,
            attempt.Count,
            MaxAttempts,
            2_000,
            $"A consent prompt was shown; retry with consent_id after it is allowed or the countdown expires.");
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
            message);

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
            request.State.ToString().ToLowerInvariant());
    }

    private static string MakeKey(string targetId, string action, string elementKey) =>
        $"{targetId}\u001f{action}\u001f{elementKey}";

    private static int RetryDelay(int attempt) => 1_500 * Math.Max(1, attempt);
}
