using CodeOrbit.Contracts;
using CodeOrbit.Core.Models;
using CodeOrbit.Core.Services;

namespace CodeOrbit.Hub;

public sealed record HubPendingActionSnapshot(
    string ActionId,
    string Kind,
    DateTime CreatedAt,
    string SessionId,
    string Source,
    string? ProjectName,
    string? WorkingDirectory,
    PermissionRequest? Permission,
    QuestionData? Question,
    int CurrentQuestionIndex,
    string? CurrentAnswerKey);

public sealed class HubStateChangedEventArgs : EventArgs
{
    public HubStateChangedEventArgs(
        IReadOnlyList<SessionSnapshot> sessions,
        IReadOnlyList<HubPendingActionSnapshot> pendingActions,
        string? affectedSessionId,
        string? affectedActionId,
        string? normalizedEventName,
        SideEffect effect,
        string? realtimeEventType,
        PendingResolutionDto? resolution = null)
    {
        Sessions = sessions;
        PendingActions = pendingActions;
        AffectedSessionId = affectedSessionId;
        AffectedActionId = affectedActionId;
        NormalizedEventName = normalizedEventName;
        Effect = effect;
        RealtimeEventType = realtimeEventType;
        Resolution = resolution;
    }

    public IReadOnlyList<SessionSnapshot> Sessions { get; }
    public IReadOnlyList<HubPendingActionSnapshot> PendingActions { get; }
    public string? AffectedSessionId { get; }
    public string? AffectedActionId { get; }
    public string? NormalizedEventName { get; }
    public SideEffect Effect { get; }
    public string? RealtimeEventType { get; }
    public PendingResolutionDto? Resolution { get; }
}

public sealed record HubRealtimeEventArgs(string Type, object? Data);

public sealed class CodeOrbitHubState : ICodeOrbitHubState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<PendingPermission> _permissionQueue = new();
    private readonly Queue<PendingQuestion> _questionQueue = new();
    private readonly Queue<PendingResolutionDto> _history = new();
    private const int MaxHistoryEntries = 200;
    private readonly Func<PermissionRequest, bool>? _shouldAutoApprove;

    public CodeOrbitHubState(Func<PermissionRequest, bool>? shouldAutoApprove = null)
    {
        _shouldAutoApprove = shouldAutoApprove;
    }

    public event EventHandler<HubStateChangedEventArgs>? StateChanged;
    public event EventHandler<HubRealtimeEventArgs>? RealtimeEventRaised;
    public event Action<SessionSnapshot>? TerminalActivationRequested;

    public IReadOnlyList<SessionDto> GetSessions() =>
        GetSessionSnapshots().Select(MapSession).ToList();

    public SessionDto? GetSession(string sessionId) =>
        GetSessionSnapshot(sessionId) is { } session ? MapSession(session) : null;

    public IReadOnlyList<ChatMessageDto> GetSessionMessages(string sessionId) =>
        GetSessionSnapshot(sessionId)?.RecentMessages.Select(MapMessage).ToList() ?? [];

    public IReadOnlyList<PendingActionDto> GetPendingActions() =>
        GetPendingActionSnapshots().Select(MapPendingAction).ToList();

    public PendingActionDto? GetPendingAction(string actionId) =>
        GetPendingActionSnapshot(actionId) is { } pending ? MapPendingAction(pending) : null;

    public IReadOnlyList<PendingResolutionDto> GetPendingHistory(int limit = 100)
    {
        if (limit <= 0)
            return [];

        lock (_gate)
        {
            var snapshot = _history.ToArray();
            if (snapshot.Length > limit)
                snapshot = snapshot[^limit..];
            return snapshot;
        }
    }

    public IReadOnlyList<SessionSnapshot> GetSessionSnapshots()
    {
        lock (_gate)
            return _sessions.Values.Select(static session => session.Clone()).ToList();
    }

    public SessionSnapshot? GetSessionSnapshot(string sessionId)
    {
        lock (_gate)
            return _sessions.TryGetValue(sessionId, out var session) ? session.Clone() : null;
    }

    public IReadOnlyList<HubPendingActionSnapshot> GetPendingActionSnapshots()
    {
        lock (_gate)
            return GetPendingActionSnapshotsLocked();
    }

    public HubPendingActionSnapshot? GetPendingActionSnapshot(string actionId)
    {
        lock (_gate)
        {
            if (FindPermissionByIdLocked(actionId) is { } permission)
                return ToPendingActionSnapshotLocked(permission);
            if (FindQuestionByIdLocked(actionId) is { } question)
                return ToPendingActionSnapshotLocked(question);
            return null;
        }
    }

    public void HandleEvent(HookEvent evt)
    {
        HandleEventCore(evt, blockingCompletion: null);
    }

    public async Task<string> HandleBlockingEventAsync(HookEvent evt, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        HandleEventCore(evt, completion);

        try
        {
            return await completion.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            if (RemoveCompletion(completion, out var timedOutActionId, out var timedOutSessionId, out var timedOutKind))
            {
                var timeoutResolution = new PendingResolutionDto(
                    timedOutActionId,
                    timedOutKind,
                    timedOutSessionId,
                    Source: null,
                    Decision: "timeout",
                    Actor: null,
                    Reason: "timeout",
                    ResolvedAtUtc: DateTimeOffset.UtcNow);
                lock (_gate)
                    RecordHistoryLocked(timeoutResolution);
                NotifyStateChanged(
                    affectedSessionId: timedOutSessionId,
                    affectedActionId: timedOutActionId,
                    normalizedEventName: null,
                    effect: new SideEffect.None(),
                    realtimeEventType: "pending.resolved",
                    timeoutResolution);
            }
            else
            {
                NotifyStateChanged(affectedSessionId: evt.SessionId, affectedActionId: null, normalizedEventName: null, effect: new SideEffect.None(), realtimeEventType: "pending.updated");
            }

            return IsQuestionEvent(evt)
                ? HookResponseBuilder.BuildQuestionDismissResponse(evt, "timeout")
                : HookResponseBuilder.BuildPermissionDenyResponse(evt, "timeout");
        }
    }

    public bool DismissSession(string sessionId)
    {
        var removed = false;
        lock (_gate)
        {
            removed = RemoveSessionLocked(sessionId, "session dismissed");
        }

        if (removed)
            NotifyStateChanged(sessionId, affectedActionId: null, normalizedEventName: "SessionEnd", effect: new SideEffect.None(), realtimeEventType: "session.removed");
        return removed;
    }

    public bool ActivateTerminal(string sessionId)
    {
        SessionSnapshot? session;
        lock (_gate)
            session = _sessions.TryGetValue(sessionId, out var existing) ? existing.Clone() : null;

        if (session == null)
            return false;

        TerminalActivationRequested?.Invoke(session);
        return true;
    }

    public bool AllowPermission(string actionId, bool always, string? actor = null) =>
        ResolvePermission(actionId,
            pending => HookResponseBuilder.BuildPermissionAllowResponse(pending.Event, pending.Request, always),
            decision: always ? "allow-always" : "allow",
            actor,
            reason: null);

    public bool DenyPermission(string actionId, string reason, string? actor = null) =>
        ResolvePermission(actionId,
            pending => HookResponseBuilder.BuildPermissionDenyResponse(pending.Event, reason),
            decision: "deny",
            actor,
            reason);

    public bool AnswerQuestion(string actionId, QuestionAnswerRequest request)
    {
        var actor = request.Actor;
        if (request.Answers is { Count: > 0 })
            return ResolveQuestionWithAnswerMap(actionId, request.Answers, actor);

        return AnswerCurrentQuestion(actionId, string.IsNullOrWhiteSpace(request.Answer) ? [] : [request.Answer], out _, actor);
    }

    public bool AnswerCurrentQuestion(string actionId, IReadOnlyList<string> answers, out bool resolved, string? actor = null)
    {
        resolved = false;
        PendingQuestion? completed = null;
        var realtimeEventType = "pending.updated";
        PendingResolutionDto? resolution = null;

        lock (_gate)
        {
            var pending = FindQuestionByIdLocked(actionId);
            if (pending == null)
                return false;

            if (!TryRecordQuestionAnswer(pending, answers))
                return false;

            if (AdvanceQuestionIfNeeded(pending))
            {
                resolved = false;
            }
            else
            {
                RemoveQuestionByIdLocked(pending.ActionId);
                completed = pending;
                resolved = true;
                realtimeEventType = "pending.resolved";
                resolution = new PendingResolutionDto(
                    pending.ActionId,
                    Kind: "question",
                    pending.Question.SessionId,
                    Source: GetSessionSourceKey(GetSessionLocked(pending.Question.SessionId)),
                    Decision: "answered",
                    actor,
                    Reason: null,
                    ResolvedAtUtc: DateTimeOffset.UtcNow);
            }
        }

        completed?.Completion.TrySetResult(BuildQuestionAnswerResponse(completed));
        NotifyStateChanged(completed?.Question.SessionId, actionId, normalizedEventName: null, effect: new SideEffect.None(), realtimeEventType, resolution);
        return true;
    }

    public bool DismissQuestion(string actionId, string reason, string? actor = null)
    {
        PendingQuestion? pending;
        lock (_gate)
        {
            pending = FindQuestionByIdLocked(actionId);
            if (pending == null || !RemoveQuestionByIdLocked(actionId))
                return false;
        }

        pending.Completion.TrySetResult(HookResponseBuilder.BuildQuestionDismissResponse(pending.Event, reason));
        var resolution = new PendingResolutionDto(
            pending.ActionId,
            Kind: "question",
            pending.Question.SessionId,
            Source: GetSessionSourceKey(GetSessionLocked(pending.Question.SessionId)),
            Decision: "dismissed",
            actor,
            reason,
            ResolvedAtUtc: DateTimeOffset.UtcNow);
        NotifyStateChanged(pending.Question.SessionId, actionId, normalizedEventName: null, effect: new SideEffect.None(), realtimeEventType: "pending.resolved", resolution);
        return true;
    }

    public bool RemoveExitedSessions(Func<SessionSnapshot, bool> isExited, string reason = "process exited")
    {
        var sessions = GetSessionSnapshots();
        var exitedSessionIds = sessions
            .Where(isExited)
            .Select(static session => session.SessionId)
            .ToArray();

        if (exitedSessionIds.Length == 0)
            return false;

        var removedAny = false;
        lock (_gate)
        {
            foreach (var sessionId in exitedSessionIds)
                removedAny = RemoveSessionLocked(sessionId, reason) || removedAny;
        }

        if (removedAny)
            NotifyStateChanged(exitedSessionIds[0], affectedActionId: null, normalizedEventName: "SessionEnd", effect: new SideEffect.None(), realtimeEventType: "session.removed");
        return removedAny;
    }

    public bool RemoveIdleSessions(TimeSpan idleTimeout, DateTime utcNow, string reason = "session idle timeout")
    {
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        var cutoff = utcNow - idleTimeout;
        string[] idleSessionIds;
        lock (_gate)
        {
            idleSessionIds = _sessions.Values
                .Where(session => IsIdleSession(session, cutoff))
                .Select(static session => session.SessionId)
                .ToArray();

            if (idleSessionIds.Length == 0)
                return false;

            foreach (var sessionId in idleSessionIds)
                RemoveSessionLocked(sessionId, reason);
        }

        if (idleSessionIds.Length > 0)
            NotifyStateChanged(idleSessionIds[0], affectedActionId: null, normalizedEventName: "SessionEnd", effect: new SideEffect.None(), realtimeEventType: "session.removed");
        return true;
    }

    private void HandleEventCore(HookEvent evt, TaskCompletionSource<string>? blockingCompletion)
    {
        string? sessionId;
        string? normalizedEventName;
        SideEffect effect;

        lock (_gate)
        {
            sessionId = ResolveSessionIdLocked(evt);
            var existing = sessionId != null && _sessions.TryGetValue(sessionId, out var s) ? s : null;
            var (newState, reducedEffect) = SessionSnapshot.ReduceEvent(existing, evt);
            newState = ApplyTranscriptMessages(existing, newState);
            sessionId = newState.SessionId;
            normalizedEventName = EventNormalizer.NormalizeEventName(newState.Source, evt.EventName);
            effect = normalizedEventName == "Stop" && reducedEffect is SideEffect.None && HasCompletionContent(newState)
                ? new SideEffect.PlaySound("complete")
                : reducedEffect;

            if (normalizedEventName == "SessionEnd")
            {
                RemoveSessionLocked(sessionId, "session ended");
            }
            else
            {
                _sessions[sessionId] = newState;
            }

            switch (effect)
            {
                case SideEffect.ShowApprovalCard approval when blockingCompletion != null:
                    if (_shouldAutoApprove?.Invoke(approval.Request) == true)
                    {
                        blockingCompletion.TrySetResult(HookResponseBuilder.BuildPermissionAllowResponse(evt, approval.Request, always: false));
                    }
                    else
                    {
                        _permissionQueue.Enqueue(new PendingPermission(NewPendingActionId("permission"), DateTime.UtcNow, approval.Request, blockingCompletion, evt));
                    }
                    break;
                case SideEffect.ShowQuestionCard question when blockingCompletion != null:
                    _questionQueue.Enqueue(new PendingQuestion(NewPendingActionId("question"), DateTime.UtcNow, question.Question, blockingCompletion, evt));
                    break;
            }
        }

        NotifyStateChanged(sessionId, affectedActionId: null, normalizedEventName, effect, ToRealtimeEventType(normalizedEventName, effect));
    }

    private bool ResolvePermission(string actionId, Func<PendingPermission, string> responseFactory, string decision, string? actor, string? reason)
    {
        PendingPermission? pending;
        PendingResolutionDto? resolution;
        lock (_gate)
        {
            pending = FindPermissionByIdLocked(actionId);
            if (pending == null || !RemovePermissionByIdLocked(actionId))
                return false;

            resolution = new PendingResolutionDto(
                pending.ActionId,
                Kind: "permission",
                pending.Request.SessionId,
                Source: GetSessionSourceKey(GetSessionLocked(pending.Request.SessionId)),
                decision,
                actor,
                reason,
                ResolvedAtUtc: DateTimeOffset.UtcNow);
            RecordHistoryLocked(resolution);
        }

        pending.Completion.TrySetResult(responseFactory(pending));
        NotifyStateChanged(pending.Request.SessionId, actionId, normalizedEventName: null, effect: new SideEffect.None(), realtimeEventType: "pending.resolved", resolution);
        return true;
    }

    private bool ResolveQuestionWithAnswerMap(string actionId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, string? actor)
    {
        PendingQuestion? pending;
        PendingResolutionDto? resolution;
        lock (_gate)
        {
            pending = FindQuestionByIdLocked(actionId);
            if (pending == null)
                return false;

            foreach (var (key, values) in answers)
            {
                var cleaned = CleanAnswers(values);
                if (cleaned.Length > 0)
                    pending.Answers[key] = cleaned;
            }

            if (pending.Answers.Count == 0 || !RemoveQuestionByIdLocked(actionId))
                return false;

            resolution = new PendingResolutionDto(
                pending.ActionId,
                Kind: "question",
                pending.Question.SessionId,
                Source: GetSessionSourceKey(GetSessionLocked(pending.Question.SessionId)),
                Decision: "answered",
                actor,
                Reason: null,
                ResolvedAtUtc: DateTimeOffset.UtcNow);
            RecordHistoryLocked(resolution);
        }

        pending.Completion.TrySetResult(BuildQuestionAnswerResponse(pending));
        NotifyStateChanged(pending.Question.SessionId, actionId, normalizedEventName: null, effect: new SideEffect.None(), realtimeEventType: "pending.resolved", resolution);
        return true;
    }

    private void RecordHistoryLocked(PendingResolutionDto resolution)
    {
        _history.Enqueue(resolution);
        while (_history.Count > MaxHistoryEntries)
            _history.Dequeue();
    }

    private bool RemoveCompletion(TaskCompletionSource<string> completion, out string? actionId, out string? sessionId, out string? kind)
    {
        actionId = null;
        sessionId = null;
        kind = null;
        var removed = false;
        lock (_gate)
        {
            var permissions = _permissionQueue.ToArray();
            _permissionQueue.Clear();
            foreach (var pending in permissions)
            {
                if (pending.Completion == completion)
                {
                    removed = true;
                    actionId = pending.ActionId;
                    sessionId = pending.Request.SessionId;
                    kind = "permission";
                }
                else
                {
                    _permissionQueue.Enqueue(pending);
                }
            }

            var questions = _questionQueue.ToArray();
            _questionQueue.Clear();
            foreach (var pending in questions)
            {
                if (pending.Completion == completion)
                {
                    removed = true;
                    actionId = pending.ActionId;
                    sessionId = pending.Question.SessionId;
                    kind = "question";
                }
                else
                {
                    _questionQueue.Enqueue(pending);
                }
            }
        }

        return removed;
    }

    private bool RemoveSessionLocked(string sessionId, string pendingResponseReason)
    {
        var removed = _sessions.Remove(sessionId);
        removed = RemovePendingActionsForSessionLocked(sessionId, pendingResponseReason) || removed;
        return removed;
    }

    private bool RemovePendingActionsForSessionLocked(string sessionId, string reason)
    {
        var removed = false;
        var permissions = _permissionQueue.ToArray();
        _permissionQueue.Clear();
        foreach (var pending in permissions)
        {
            if (string.Equals(pending.Request.SessionId, sessionId, StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(HookResponseBuilder.BuildPermissionDenyResponse(pending.Event, reason));
                removed = true;
            }
            else
            {
                _permissionQueue.Enqueue(pending);
            }
        }

        var questions = _questionQueue.ToArray();
        _questionQueue.Clear();
        foreach (var pending in questions)
        {
            if (string.Equals(pending.Question.SessionId, sessionId, StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(HookResponseBuilder.BuildQuestionDismissResponse(pending.Event, reason));
                removed = true;
            }
            else
            {
                _questionQueue.Enqueue(pending);
            }
        }

        return removed;
    }

    private string? ResolveSessionIdLocked(HookEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.SessionId))
            return evt.SessionId;

        if (_sessions.Count == 1)
            return _sessions.Keys.First();

        if (evt.TrackedPid is { } pid)
        {
            var matches = _sessions.Values.Where(s => s.Pid == pid).ToArray();
            if (matches.Length == 1)
                return matches[0].SessionId;
        }

        return null;
    }

    private IReadOnlyList<HubPendingActionSnapshot> GetPendingActionSnapshotsLocked()
    {
        var pending = new List<HubPendingActionSnapshot>(_permissionQueue.Count + _questionQueue.Count);
        pending.AddRange(_permissionQueue.Select(ToPendingActionSnapshotLocked));
        pending.AddRange(_questionQueue.Select(ToPendingActionSnapshotLocked));
        return pending.OrderBy(static action => action.CreatedAt).ToList();
    }

    private HubPendingActionSnapshot ToPendingActionSnapshotLocked(PendingPermission pending)
    {
        var session = GetSessionLocked(pending.Request.SessionId);
        return new HubPendingActionSnapshot(
            pending.ActionId,
            "permission",
            pending.CreatedAt,
            pending.Request.SessionId,
            GetSessionSourceKey(session),
            GetSessionProjectName(session),
            session?.WorkingDirectory,
            pending.Request,
            Question: null,
            CurrentQuestionIndex: 0,
            CurrentAnswerKey: null);
    }

    private HubPendingActionSnapshot ToPendingActionSnapshotLocked(PendingQuestion pending)
    {
        var session = GetSessionLocked(pending.Question.SessionId);
        return new HubPendingActionSnapshot(
            pending.ActionId,
            "question",
            pending.CreatedAt,
            pending.Question.SessionId,
            GetSessionSourceKey(session),
            GetSessionProjectName(session),
            session?.WorkingDirectory,
            Permission: null,
            pending.Question,
            pending.CurrentQuestionIndex,
            pending.CurrentAnswerKey);
    }

    private SessionSnapshot? GetSessionLocked(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var session) ? session : null;

    private static string GetSessionProjectName(SessionSnapshot? session) =>
        session?.ProjectName ?? session?.WorkingDirectory ?? "unknown";

    private static string GetSessionSourceKey(SessionSnapshot? session) =>
        string.IsNullOrWhiteSpace(session?.Source) ? "unknown" : session.Source;

    private PendingPermission? FindPermissionByIdLocked(string actionId) =>
        _permissionQueue.FirstOrDefault(pending => pending.ActionId == actionId);

    private PendingQuestion? FindQuestionByIdLocked(string actionId) =>
        _questionQueue.FirstOrDefault(pending => pending.ActionId == actionId);

    private bool RemovePermissionByIdLocked(string actionId) =>
        RemoveMatching(_permissionQueue, pending => pending.ActionId == actionId) > 0;

    private bool RemoveQuestionByIdLocked(string actionId) =>
        RemoveMatching(_questionQueue, pending => pending.ActionId == actionId) > 0;

    private static int RemoveMatching<T>(Queue<T> queue, Func<T, bool> predicate)
    {
        var removed = 0;
        var retained = new List<T>(queue.Count);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (predicate(item))
                removed++;
            else
                retained.Add(item);
        }

        foreach (var item in retained)
            queue.Enqueue(item);

        return removed;
    }

    private static bool TryRecordQuestionAnswer(PendingQuestion pending, IReadOnlyList<string> answers)
    {
        var cleanedAnswers = CleanAnswers(answers);
        if (cleanedAnswers.Length == 0)
            return false;

        var key = pending.CurrentAnswerKey;
        if (string.IsNullOrWhiteSpace(key))
            key = "answer";

        pending.Answers[key] = cleanedAnswers;
        return true;
    }

    private static string[] CleanAnswers(IEnumerable<string> answers) =>
        answers
            .Where(static answer => !string.IsNullOrWhiteSpace(answer))
            .Select(static answer => answer.Trim())
            .ToArray();

    private static bool AdvanceQuestionIfNeeded(PendingQuestion pending)
    {
        if (pending.Question.Questions is not { Count: > 0 } questions || pending.CurrentQuestionIndex >= questions.Count - 1)
            return false;

        pending.CurrentQuestionIndex++;
        return true;
    }

    private static string BuildQuestionAnswerResponse(PendingQuestion pending) =>
        HookResponseBuilder.BuildQuestionAnswerResponse(pending.Event, pending.Question, pending.Answers);

    private void NotifyStateChanged(
        string? affectedSessionId,
        string? affectedActionId,
        string? normalizedEventName,
        SideEffect effect,
        string? realtimeEventType,
        PendingResolutionDto? resolution = null)
    {
        var args = CreateStateChangedArgs(affectedSessionId, affectedActionId, normalizedEventName, effect, realtimeEventType, resolution);
        StateChanged?.Invoke(this, args);

        if (realtimeEventType != null)
            RealtimeEventRaised?.Invoke(this, new HubRealtimeEventArgs(realtimeEventType, CreateRealtimePayload(realtimeEventType, args)));

        if (realtimeEventType == "pending.updated" && !string.IsNullOrWhiteSpace(affectedSessionId))
            RealtimeEventRaised?.Invoke(this, new HubRealtimeEventArgs("session.updated", CreateRealtimePayload("session.updated", args)));
    }

    private HubStateChangedEventArgs CreateStateChangedArgs(
        string? affectedSessionId,
        string? affectedActionId,
        string? normalizedEventName,
        SideEffect effect,
        string? realtimeEventType,
        PendingResolutionDto? resolution)
    {
        lock (_gate)
        {
            return new HubStateChangedEventArgs(
                _sessions.Values.Select(static session => session.Clone()).ToList(),
                GetPendingActionSnapshotsLocked(),
                affectedSessionId,
                affectedActionId,
                normalizedEventName,
                effect,
                realtimeEventType,
                resolution);
        }
    }

    private static object? CreateRealtimePayload(string realtimeEventType, HubStateChangedEventArgs args) =>
        realtimeEventType switch
        {
            "session.updated" => args.Sessions.Select(MapSession).ToList(),
            "session.removed" => new { sessionId = args.AffectedSessionId },
            "pending.updated" => args.PendingActions.Select(MapPendingAction).ToList(),
            "pending.resolved" => new
            {
                actionId = args.AffectedActionId,
                resolution = args.Resolution,
                pending = args.PendingActions.Select(MapPendingAction).ToList()
            },
            _ => null
        };

    private static string? ToRealtimeEventType(string? normalizedEventName, SideEffect effect)
    {
        if (normalizedEventName == "SessionEnd")
            return "session.removed";

        return effect switch
        {
            SideEffect.ShowApprovalCard or SideEffect.ShowQuestionCard => "pending.updated",
            _ => "session.updated"
        };
    }

    private static bool HasCompletionContent(SessionSnapshot session) =>
        !string.IsNullOrWhiteSpace(session.CompletionText) ||
        !string.IsNullOrWhiteSpace(session.LastAssistantMessage);

    private static bool IsIdleSession(SessionSnapshot session, DateTime cutoffUtc) =>
        AsUtcDateTime(session.LastUpdatedAt) <= cutoffUtc;

    private static DateTime AsUtcDateTime(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static SessionSnapshot ApplyTranscriptMessages(SessionSnapshot? existing, SessionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TranscriptPath))
            return snapshot;

        var startPosition = existing?.TranscriptPath == snapshot.TranscriptPath
            ? existing.TranscriptPosition
            : 0;
        var result = TranscriptMessageReader.ReadNewMessages(snapshot.TranscriptPath, startPosition);
        if (result.Position == startPosition && result.Messages.Count == 0)
            return snapshot;

        var clone = snapshot.Clone();
        clone.TranscriptPosition = result.Position;
        foreach (var message in result.Messages)
            SessionSnapshot.AddRecentMessage(clone, message);

        clone.CompletionText ??= clone.LastAssistantMessage;
        return clone;
    }

    private static bool IsQuestionEvent(HookEvent evt)
    {
        var name = EventNormalizer.NormalizeEventName(evt.Source ?? "unknown", evt.EventName);
        if (HookToolClassifier.ShouldBlockQuestionTool(evt, name))
            return true;
        if (!name.StartsWith("Question", StringComparison.OrdinalIgnoreCase) && name != "Notification")
            return false;
        return ContainsAny(evt.RawJson, "question", "questions") || ContainsAny(evt.ToolInput, "question", "questions");
    }

    private static bool ContainsAny(System.Text.Json.JsonElement? element, params string[] names)
    {
        if (element is not { ValueKind: System.Text.Json.JsonValueKind.Object } obj)
            return false;

        foreach (var property in obj.EnumerateObject())
        {
            if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object && ContainsAny(property.Value, names))
                return true;
        }

        return false;
    }

    private static SessionDto MapSession(SessionSnapshot session) => new(
        session.SessionId,
        session.Source,
        SupportedSource.GetDisplayName(session.Source),
        session.ProjectName,
        session.WorkingDirectory,
        session.Status.ToString(),
        session.CurrentToolName,
        session.CurrentToolDescription,
        AsUtc(session.CreatedAt),
        AsUtc(session.LastUpdatedAt),
        session.Pid == 0 ? null : session.Pid,
        session.TrackedProcessStartedAtUtc is { } startedAt ? AsUtc(startedAt) : null,
        session.LastUserPrompt,
        session.LastAssistantMessage,
        session.CompletionText,
        session.TranscriptPath,
        session.TranscriptPosition,
        session.TerminalApp,
        session.TerminalSessionId,
        session.RecentMessages.Select(MapMessage).ToList(),
        session.ToolHistory.Select(MapToolHistory).ToList());

    private static PendingActionDto MapPendingAction(HubPendingActionSnapshot pending) => new(
        pending.ActionId,
        pending.Kind,
        pending.SessionId,
        pending.Source,
        SupportedSource.GetDisplayName(pending.Source),
        pending.ProjectName,
        pending.WorkingDirectory,
        AsUtc(pending.CreatedAt),
        pending.Permission is { } permission ? MapPermission(permission) : null,
        pending.Question is { } question ? MapQuestion(question, pending.CurrentQuestionIndex, pending.CurrentAnswerKey) : null);

    private static PermissionRequestDto MapPermission(PermissionRequest request) => new(
        request.SessionId,
        request.ToolName,
        request.ToolUseId,
        request.ToolInput,
        request.Description,
        request.HookEventName);

    private static QuestionDto MapQuestion(QuestionData question, int currentQuestionIndex, string? currentAnswerKey) => new(
        question.SessionId,
        question.Id,
        question.Question,
        question.Header,
        (question.Options ?? []).Select(MapQuestionOption).ToList(),
        question.MultiSelect,
        question.IsMultiQuestion,
        (question.Questions ?? []).Select(MapQuestionItem).ToList(),
        question.HookEventName,
        question.IsAskUserQuestion,
        question.IsCodexRequestUserInput,
        currentQuestionIndex,
        currentAnswerKey ?? question.Id ?? question.Question);

    private static QuestionItemDto MapQuestionItem(QuestionItem item) => new(
        item.Id,
        item.Question,
        item.Header,
        (item.Options ?? []).Select(MapQuestionOption).ToList(),
        item.MultiSelect,
        item.AllowFreeText);

    private static QuestionOptionDto MapQuestionOption(QuestionOption option) => new(
        option.Label,
        option.Description,
        option.Value);

    private static ChatMessageDto MapMessage(ChatMessage message) => new(
        message.IsUser,
        message.Text,
        AsUtc(message.Timestamp));

    private static ToolHistoryEntryDto MapToolHistory(ToolHistoryEntry entry) => new(
        entry.ToolName,
        AsUtc(entry.Timestamp),
        entry.Description,
        entry.Success);

    private static DateTimeOffset AsUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    private static string NewPendingActionId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed record PendingPermission(string ActionId, DateTime CreatedAt, PermissionRequest Request, TaskCompletionSource<string> Completion, HookEvent Event);

    private sealed class PendingQuestion
    {
        public PendingQuestion(string actionId, DateTime createdAt, QuestionData question, TaskCompletionSource<string> completion, HookEvent @event)
        {
            ActionId = actionId;
            CreatedAt = createdAt;
            Question = question;
            Completion = completion;
            Event = @event;
        }

        public string ActionId { get; }
        public DateTime CreatedAt { get; }
        public QuestionData Question { get; }
        public TaskCompletionSource<string> Completion { get; }
        public HookEvent Event { get; }
        public int CurrentQuestionIndex { get; set; }
        public Dictionary<string, IReadOnlyList<string>> Answers { get; } = new(StringComparer.Ordinal);
        public QuestionItem? CurrentItem => Question.Questions is { Count: > 0 } questions
            ? questions[Math.Clamp(CurrentQuestionIndex, 0, questions.Count - 1)]
            : null;
        public string CurrentQuestionText => CurrentItem?.Question ?? Question.Question;
        public string CurrentAnswerKey => CurrentItem?.Id ?? Question.Id ?? CurrentQuestionText;
    }
}
