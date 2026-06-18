using System.Text.Json;
using CodeOrbit.Core.Models;
using CodeOrbit.Hub;
using Xunit;

namespace CodeOrbit.Hub.Tests;

public class CodeOrbitHubStateTests
{
    [Fact]
    public void HandleEvent_StoresSessionSnapshot()
    {
        var state = new CodeOrbitHubState();
        var evt = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "session-1",
            ["cwd"] = @"D:\Work\sample"
        });

        state.HandleEvent(evt);

        var session = Assert.Single(state.GetSessions());
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("sample", session.ProjectName);
    }

    [Fact]
    public async Task AllowPermission_CompletesBlockingResponseAndClearsPendingAction()
    {
        var state = new CodeOrbitHubState();
        var evt = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "session-1",
            ["tool_name"] = "Bash",
            ["tool_input"] = new { command = "dotnet test", requires_approval = true }
        });

        var responseTask = state.HandleBlockingEventAsync(evt, TimeSpan.FromSeconds(5), CancellationToken.None);
        var pending = Assert.Single(state.GetPendingActions());

        Assert.Equal("permission", pending.Kind);
        Assert.NotNull(pending.Permission!.ToolInput);
        Assert.Equal("dotnet test", pending.Permission.ToolInput!["command"]);
        Assert.True(state.AllowPermission(pending.ActionId, always: false));

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(response);
        Assert.Equal("allow", doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecision")
            .GetString());
        Assert.Empty(state.GetPendingActions());
    }

    [Fact]
    public async Task AnswerCurrentQuestion_AdvancesMultiQuestionThenCompletesResponse()
    {
        var state = new CodeOrbitHubState();
        var evt = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PermissionRequest",
            ["session_id"] = "session-1",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new
            {
                questions = new object[]
                {
                    new { id = "first", question = "First?", options = new[] { "A", "B" } },
                    new { id = "second", question = "Second?", options = new[] { "C", "D" } }
                }
            }
        });

        var responseTask = state.HandleBlockingEventAsync(evt, TimeSpan.FromSeconds(5), CancellationToken.None);
        var actionId = Assert.Single(state.GetPendingActions()).ActionId;

        Assert.True(state.AnswerCurrentQuestion(actionId, ["A"], out var firstResolved));
        Assert.False(firstResolved);
        var advanced = Assert.Single(state.GetPendingActions());
        Assert.Equal(1, advanced.Question!.CurrentQuestionIndex);
        Assert.Equal("second", advanced.Question.CurrentAnswerKey);

        Assert.True(state.AnswerCurrentQuestion(actionId, ["D"], out var finalResolved));
        Assert.True(finalResolved);

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(response);
        var answers = doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("updatedInput")
            .GetProperty("answers");
        Assert.Equal("A", answers.GetProperty("first").GetString());
        Assert.Equal("D", answers.GetProperty("second").GetString());
        Assert.Empty(state.GetPendingActions());
    }

    [Fact]
    public void RemoveIdleSessions_RemovesSessionsPastTimeout()
    {
        var state = new CodeOrbitHubState();
        var now = DateTime.UtcNow;
        var old = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "old-session"
        });
        var fresh = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "fresh-session"
        });

        state.HandleEvent(old);
        state.HandleEvent(fresh);
        SetLastUpdatedAt(state, "old-session", now - TimeSpan.FromMinutes(31));
        SetLastUpdatedAt(state, "fresh-session", now - TimeSpan.FromMinutes(29));

        Assert.True(state.RemoveIdleSessions(TimeSpan.FromMinutes(30), now));

        var session = Assert.Single(state.GetSessions());
        Assert.Equal("fresh-session", session.SessionId);
    }

    [Fact]
    public async Task RemoveIdleSessions_CompletesPendingHookResponse()
    {
        var state = new CodeOrbitHubState();
        var now = DateTime.UtcNow;
        var evt = MakeEvent(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "idle-session",
            ["tool_name"] = "Bash",
            ["tool_input"] = new { command = "dotnet test", requires_approval = true }
        });

        var responseTask = state.HandleBlockingEventAsync(evt, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Single(state.GetPendingActions());
        SetLastUpdatedAt(state, "idle-session", now - TimeSpan.FromMinutes(31));

        Assert.True(state.RemoveIdleSessions(TimeSpan.FromMinutes(30), now));

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(response);
        Assert.Equal("deny", doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecision")
            .GetString());
        Assert.Empty(state.GetPendingActions());
        Assert.Empty(state.GetSessions());
    }

    private static HookEvent MakeEvent(Dictionary<string, object?> payload, string source = "claude")
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return HookEvent.FromJson(doc.RootElement, source)!;
    }

    private static void SetLastUpdatedAt(CodeOrbitHubState state, string sessionId, DateTime lastUpdatedAtUtc)
    {
        var field = typeof(CodeOrbitHubState).GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not access sessions for test setup.");
        var sessions = (Dictionary<string, SessionSnapshot>)field.GetValue(state)!;
        sessions[sessionId].LastUpdatedAt = DateTime.SpecifyKind(lastUpdatedAtUtc, DateTimeKind.Utc);
    }
}
