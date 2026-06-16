using System.Text.Json;

namespace CodeOrbit.Core.Sources;

/// <summary>
/// Hook installation strategy for Claude matcher format.
/// Format: {hooks: {EventName: [{matcher, hooks: [{type, command, timeout}]}]}}
/// Used by: Claude
///
/// Note: This is a simplified implementation for Phase 2A.
/// Full matcher group support will be added in Phase 2B.
/// </summary>
internal sealed class ClaudeMatcherStrategy : IHookInstallationStrategy
{
    public bool Install(string sourceKey, HookInstallationSpec spec)
    {
        try
        {
            var configPath = HookInstallationUtils.ExpandPath(spec.ConfigPath);
            var root = HookInstallationUtils.ReadJsonFile(configPath);

            // Get or create hooks object
            var hooksObj = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hooks", out var existing)
                ? existing
                : JsonDocument.Parse("{}").RootElement;

            // Build new hooks with matcher groups
            var newHooks = BuildMatcherHooksObject(hooksObj, sourceKey, spec);

            // Merge into root
            var newRoot = MergeHooksIntoRoot(root, newHooks);

            HookInstallationUtils.WriteJsonFile(configPath, newRoot);

            if (spec.ExtraConfig != null)
            {
                // Claude doesn't use extra config, but handle it anyway
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Uninstall(string sourceKey, HookInstallationSpec spec)
    {
        try
        {
            var configPath = HookInstallationUtils.ExpandPath(spec.ConfigPath);
            if (!File.Exists(configPath))
                return true;

            var root = HookInstallationUtils.ReadJsonFile(configPath);
            if (!root.TryGetProperty("hooks", out var hooksObj))
                return true;

            // Remove CodeOrbit hooks from matcher groups
            var cleanedHooks = RemoveCodeOrbitHooks(hooksObj, sourceKey);

            var newRoot = MergeHooksIntoRoot(root, cleanedHooks);
            HookInstallationUtils.WriteJsonFile(configPath, newRoot);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsInstalled(string sourceKey, HookInstallationSpec spec)
    {
        try
        {
            var configPath = HookInstallationUtils.ExpandPath(spec.ConfigPath);
            if (!File.Exists(configPath))
                return false;

            var root = HookInstallationUtils.ReadJsonFile(configPath);
            if (!root.TryGetProperty("hooks", out var hooksObj))
                return false;

            // Check matcher groups
            foreach (var eventProp in hooksObj.EnumerateObject())
            {
                if (eventProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var matcherGroup in eventProp.Value.EnumerateArray())
                    {
                        if (matcherGroup.TryGetProperty("hooks", out var hooks) &&
                            hooks.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var hook in hooks.EnumerateArray())
                            {
                                if (hook.TryGetProperty("command", out var cmd))
                                {
                                    var cmdStr = cmd.GetString() ?? "";
                                    if (cmdStr.Contains("CodeOrbit.Bridge") || cmdStr.Contains($"--source {sourceKey}"))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement BuildMatcherHooksObject(JsonElement existing, string sourceKey, HookInstallationSpec spec)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        var hookCommand = HookInstallationUtils.GetHookCommand(sourceKey);

        // Process each event from spec
        foreach (var eventName in spec.Events)
        {
            writer.WritePropertyName(eventName);
            writer.WriteStartArray();

            // Default matcher group (matches all)
            writer.WriteStartObject();
            writer.WritePropertyName("matcher");
            writer.WriteStringValue(""); // Empty string matches all, consistent with ConfigInstaller
            writer.WritePropertyName("hooks");
            writer.WriteStartArray();

            // Add CodeOrbit hook with correct timeout for each event
            writer.WriteStartObject();
            writer.WritePropertyName("type");
            writer.WriteStringValue("command"); // Correct type for Claude
            writer.WritePropertyName("command");
            writer.WriteStringValue(hookCommand);
            writer.WritePropertyName("timeout");
            writer.WriteNumberValue(GetTimeoutForEvent(eventName, spec.TimeoutSeconds));
            writer.WriteEndObject();

            writer.WriteEndArray(); // hooks
            writer.WriteEndObject(); // matcher group
            writer.WriteEndArray(); // event array
        }

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    /// <summary>
    /// Gets the appropriate timeout for a Claude event.
    /// PreToolUse, PermissionRequest, and Notification need 86400s for long-running operations.
    /// Other events use the default timeout from spec.
    /// </summary>
    private static int GetTimeoutForEvent(string eventName, int defaultTimeout)
    {
        // Match ConfigInstaller.GetClaudeHookTimeout logic
        if (eventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("Notification", StringComparison.OrdinalIgnoreCase))
        {
            return 86400;
        }

        return defaultTimeout;
    }

    private static JsonElement RemoveCodeOrbitHooks(JsonElement hooksObj, string sourceKey)
    {
        // Simplified: just return empty object
        // Full implementation would preserve non-CodeOrbit matcher groups
        return JsonDocument.Parse("{}").RootElement;
    }

    private static JsonElement MergeHooksIntoRoot(JsonElement root, JsonElement hooks)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name != "hooks")
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
        }

        writer.WritePropertyName("hooks");
        hooks.WriteTo(writer);

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }
}
