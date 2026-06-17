using System.Text.Json;

namespace CodeOrbit.Core.Sources;

/// <summary>
/// Hook installation strategy for Codex CLI format.
/// Format: {hooks: {EventName: [{hooks: [{type, command, commandWindows?, timeout, statusMessage?}]}]}}
/// Used by: Codex CLI
/// </summary>
internal sealed class CodexHookStrategy : IHookInstallationStrategy
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

            // Build new hooks object
            var newHooks = BuildHooksObject(hooksObj, sourceKey, spec);

            // Merge into root
            var newRoot = MergeHooksIntoRoot(root, newHooks);

            HookInstallationUtils.WriteJsonFile(configPath, newRoot);

            // Codex requires config.toml to enable hooks
            if (spec.ExtraConfig != null)
            {
                InstallExtraConfig(spec.ExtraConfig);
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

            // Remove CodeOrbit hooks from each event
            var cleanedHooks = RemoveCodeOrbitHooks(hooksObj, sourceKey);

            // Merge back
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

            // Check if any event has CodeOrbit hook (in Codex's double-nested format)
            foreach (var eventProp in hooksObj.EnumerateObject())
            {
                if (eventProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in eventProp.Value.EnumerateArray())
                    {
                        if (entry.TryGetProperty("hooks", out var hooksArray) &&
                            hooksArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var hook in hooksArray.EnumerateArray())
                            {
                                if (hook.TryGetProperty("command", out var cmd))
                                {
                                    var cmdStr = cmd.GetString() ?? "";
                                    if (cmdStr.Contains("CodeOrbit-bridge.exe") ||
                                        cmdStr.Contains($"--source {sourceKey}"))
                                    {
                                        return true;
                                    }
                                }
                                // Also check commandWindows
                                if (hook.TryGetProperty("commandWindows", out var cmdWin))
                                {
                                    var cmdWinStr = cmdWin.GetString() ?? "";
                                    if (cmdWinStr.Contains("CodeOrbit-bridge.exe") ||
                                        cmdWinStr.Contains($"--source {sourceKey}"))
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

    private static JsonElement BuildHooksObject(JsonElement existing, string sourceKey, HookInstallationSpec spec)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        // Copy existing events (excluding CodeOrbit hooks)
        if (existing.ValueKind == JsonValueKind.Object)
        {
            foreach (var eventProp in existing.EnumerateObject())
            {
                var eventName = eventProp.Name;
                var entries = new List<JsonElement>();

                if (eventProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in eventProp.Value.EnumerateArray())
                    {
                        if (!IsCodeOrbitEntry(entry, sourceKey))
                        {
                            entries.Add(entry);
                        }
                    }
                }

                // Add new hook if this event is in spec
                if (spec.Events.Contains(eventName, StringComparer.OrdinalIgnoreCase))
                {
                    var timeout = GetTimeoutForEvent(eventName, spec.TimeoutSeconds);
                    entries.Add(CreateCodexEntry(sourceKey, timeout));
                }

                if (entries.Count > 0)
                {
                    writer.WritePropertyName(eventName);
                    WriteEntryArray(writer, entries);
                }
            }
        }

        // Add new events not in existing
        foreach (var eventName in spec.Events)
        {
            if (existing.ValueKind != JsonValueKind.Object || !existing.TryGetProperty(eventName, out _))
            {
                writer.WritePropertyName(eventName);
                var timeout = GetTimeoutForEvent(eventName, spec.TimeoutSeconds);
                WriteEntryArray(writer, new List<JsonElement>
                {
                    CreateCodexEntry(sourceKey, timeout)
                });
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    private static JsonElement RemoveCodeOrbitHooks(JsonElement hooksObj, string sourceKey)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        foreach (var eventProp in hooksObj.EnumerateObject())
        {
            var remaining = new List<JsonElement>();

            if (eventProp.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in eventProp.Value.EnumerateArray())
                {
                    if (!IsCodeOrbitEntry(entry, sourceKey))
                    {
                        remaining.Add(entry);
                    }
                }
            }

            if (remaining.Count > 0)
            {
                writer.WritePropertyName(eventProp.Name);
                WriteEntryArray(writer, remaining);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    private static bool IsCodeOrbitEntry(JsonElement entry, string sourceKey)
    {
        if (!entry.TryGetProperty("hooks", out var hooksArray) ||
            hooksArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var hook in hooksArray.EnumerateArray())
        {
            if (hook.TryGetProperty("command", out var cmd))
            {
                var cmdStr = cmd.GetString() ?? "";
                if (cmdStr.Contains("CodeOrbit-bridge.exe") || cmdStr.Contains($"--source {sourceKey}"))
                {
                    return true;
                }
            }
            if (hook.TryGetProperty("commandWindows", out var cmdWin))
            {
                var cmdWinStr = cmdWin.GetString() ?? "";
                if (cmdWinStr.Contains("CodeOrbit-bridge.exe") || cmdWinStr.Contains($"--source {sourceKey}"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonElement MergeHooksIntoRoot(JsonElement root, JsonElement hooks)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        // Copy all properties from root except hooks
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

        // Write new hooks
        writer.WritePropertyName("hooks");
        hooks.WriteTo(writer);

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    private static void WriteEntryArray(Utf8JsonWriter writer, List<JsonElement> entries)
    {
        writer.WriteStartArray();
        foreach (var entry in entries)
        {
            entry.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Creates a Codex hook entry with the double-nested structure.
    /// Includes both command and commandWindows for cross-platform support.
    /// </summary>
    private static JsonElement CreateCodexEntry(string sourceKey, int timeoutSeconds)
    {
        var command = HookInstallationUtils.GetHookCommand(sourceKey);
        var commandWindows = GetCodexWindowsCommand(sourceKey);

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName("hooks");
        writer.WriteStartArray();

        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteStringValue("command");
        writer.WritePropertyName("command");
        writer.WriteStringValue(command);
        writer.WritePropertyName("commandWindows");
        writer.WriteStringValue(commandWindows);
        writer.WritePropertyName("timeout");
        writer.WriteNumberValue(timeoutSeconds);
        writer.WritePropertyName("statusMessage");
        writer.WriteStringValue("CodeOrbit context injection");
        writer.WriteEndObject();

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    /// <summary>
    /// Returns unquoted Windows command for Codex.
    /// Codex on Windows runs hooks via cmd.exe /C, which strips outer quotes.
    /// Use 8.3 short path to avoid space issues.
    /// </summary>
    private static string GetCodexWindowsCommand(string sourceKey)
    {
        var bridgePath = HookInstallationUtils.GetBridgeExecutablePath();

        // Try to get 8.3 short path to avoid spaces
        if (bridgePath.Contains(' '))
        {
            var shortPath = TryGetShortPath(bridgePath);
            if (!string.IsNullOrEmpty(shortPath) && !shortPath.Contains(' '))
            {
                bridgePath = shortPath;
            }
        }

        return $"{bridgePath} --source {sourceKey}";
    }

    private static string? TryGetShortPath(string longPath)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(260);
            var length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
            if (length == 0)
                return null;
            if (length > buffer.Capacity)
            {
                buffer = new System.Text.StringBuilder((int)length);
                length = GetShortPathName(longPath, buffer, (uint)buffer.Capacity);
                if (length == 0)
                    return null;
            }
            return buffer.ToString();
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>
    /// Get timeout for specific event.
    /// Codex requires long timeout (86400s = 24h) for PreToolUse and PermissionRequest
    /// because these can block waiting for user approval.
    /// </summary>
    private static int GetTimeoutForEvent(string eventName, int defaultTimeout)
    {
        return eventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase) ||
               eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase)
            ? 86400
            : defaultTimeout;
    }

    private static void InstallExtraConfig(ExtraConfigSpec extraSpec)
    {
        var filePath = HookInstallationUtils.ExpandPath(extraSpec.FilePath);
        HookInstallationUtils.EnsureDirectoryExists(filePath);

        if (!filePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            return;

        var contents = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;

        // Already enabled (non-comment)
        if (System.Text.RegularExpressions.Regex.IsMatch(
                contents, @"^\s*hooks\s*=\s*true", System.Text.RegularExpressions.RegexOptions.Multiline))
            return;

        // Flip false to true
        if (System.Text.RegularExpressions.Regex.IsMatch(
                contents, @"^\s*hooks\s*=\s*false", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            var flipped = System.Text.RegularExpressions.Regex.Replace(
                contents, @"^\s*hooks\s*=\s*false", "hooks = true",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            File.WriteAllText(filePath, flipped);
            return;
        }

        // Migrate legacy codex_hooks
        if (System.Text.RegularExpressions.Regex.IsMatch(
                contents, @"^\s*codex_hooks\s*=\s*(true|false)", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            var migrated = System.Text.RegularExpressions.Regex.Replace(
                contents, @"^\s*codex_hooks\s*=\s*(true|false)", "hooks = true",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            File.WriteAllText(filePath, migrated);
            return;
        }

        // Insert into [features] section or append
        var newline = contents.Contains("\r\n") ? "\r\n" : "\n";
        var lines = contents.Replace("\r\n", "\n").Split('\n').ToList();
        var featureIndex = lines.FindIndex(line => line.Trim() == "[features]");

        if (featureIndex >= 0)
        {
            lines.Insert(featureIndex + 1, "hooks = true");
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add(string.Empty);
            lines.Add("[features]");
            lines.Add("hooks = true");
        }

        File.WriteAllText(filePath, string.Join(newline, lines));
    }
}
