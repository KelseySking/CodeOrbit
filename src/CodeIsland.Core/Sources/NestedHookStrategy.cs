using System.Text.Json;

namespace CodeIsland.Core.Sources;

/// <summary>
/// Hook installation strategy for nested object format.
/// Format: {hooks: {EventName: [{command, timeout}]}}
/// Used by: Gemini
/// </summary>
internal sealed class NestedHookStrategy : IHookInstallationStrategy
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

            // Remove CodeIsland hooks from each event
            var cleanedHooks = RemoveCodeIslandHooks(hooksObj, sourceKey);

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

            // Check if any event has CodeIsland hook
            foreach (var eventProp in hooksObj.EnumerateObject())
            {
                if (eventProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hook in eventProp.Value.EnumerateArray())
                    {
                        if (hook.TryGetProperty("command", out var cmd))
                        {
                            var cmdStr = cmd.GetString() ?? "";
                            if (cmdStr.Contains("CodeIsland.Bridge") || cmdStr.Contains($"--source {sourceKey}"))
                            {
                                return true;
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

        // Copy existing events (excluding CodeIsland hooks)
        if (existing.ValueKind == JsonValueKind.Object)
        {
            foreach (var eventProp in existing.EnumerateObject())
            {
                var eventName = eventProp.Name;
                var hooks = new List<JsonElement>();

                if (eventProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hook in eventProp.Value.EnumerateArray())
                    {
                        if (hook.TryGetProperty("command", out var cmd))
                        {
                            var cmdStr = cmd.GetString() ?? "";
                            if (!cmdStr.Contains("CodeIsland.Bridge") && !cmdStr.Contains($"--source {sourceKey}"))
                            {
                                hooks.Add(hook);
                            }
                        }
                    }
                }

                // Add new hook if this event is in spec
                if (spec.Events.Contains(eventName, StringComparer.OrdinalIgnoreCase))
                {
                    hooks.Add(CreateHookEntry(HookInstallationUtils.GetHookCommand(sourceKey), spec.TimeoutSeconds));
                }

                if (hooks.Count > 0)
                {
                    writer.WritePropertyName(eventName);
                    WriteHookArray(writer, hooks);
                }
            }
        }

        // Add new events not in existing
        foreach (var eventName in spec.Events)
        {
            if (existing.ValueKind != JsonValueKind.Object || !existing.TryGetProperty(eventName, out _))
            {
                writer.WritePropertyName(eventName);
                WriteHookArray(writer, new List<JsonElement>
                {
                    CreateHookEntry(HookInstallationUtils.GetHookCommand(sourceKey), spec.TimeoutSeconds)
                });
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    private static JsonElement RemoveCodeIslandHooks(JsonElement hooksObj, string sourceKey)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        foreach (var eventProp in hooksObj.EnumerateObject())
        {
            var remaining = new List<JsonElement>();

            if (eventProp.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var hook in eventProp.Value.EnumerateArray())
                {
                    if (hook.TryGetProperty("command", out var cmd))
                    {
                        var cmdStr = cmd.GetString() ?? "";
                        if (!cmdStr.Contains("CodeIsland.Bridge") && !cmdStr.Contains($"--source {sourceKey}"))
                        {
                            remaining.Add(hook);
                        }
                    }
                }
            }

            if (remaining.Count > 0)
            {
                writer.WritePropertyName(eventProp.Name);
                WriteHookArray(writer, remaining);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
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

    private static void WriteHookArray(Utf8JsonWriter writer, List<JsonElement> hooks)
    {
        writer.WriteStartArray();
        foreach (var hook in hooks)
        {
            hook.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    private static JsonElement CreateHookEntry(string command, int timeoutSeconds)
    {
        var json = $@"{{
            ""command"": ""{command}"",
            ""timeout"": {timeoutSeconds}
        }}";
        return JsonDocument.Parse(json).RootElement;
    }

    private static void InstallExtraConfig(ExtraConfigSpec extraSpec)
    {
        // Same as FlatHookStrategy
        var filePath = HookInstallationUtils.ExpandPath(extraSpec.FilePath);
        HookInstallationUtils.EnsureDirectoryExists(filePath);

        if (filePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            var lines = File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : new List<string>();
            var sectionLine = extraSpec.Section ?? "";
            var sectionIndex = lines.FindIndex(l => l.Trim() == sectionLine);

            if (sectionIndex == -1 && !string.IsNullOrEmpty(sectionLine))
            {
                lines.Add("");
                lines.Add(sectionLine);
                sectionIndex = lines.Count - 1;
            }

            var keyLine = $"{extraSpec.Key} = {extraSpec.Value}";
            var keyExists = lines.Any(l => l.Trim().StartsWith($"{extraSpec.Key} ="));

            if (!keyExists)
            {
                if (sectionIndex >= 0)
                {
                    lines.Insert(sectionIndex + 1, keyLine);
                }
                else
                {
                    lines.Add(keyLine);
                }
            }

            File.WriteAllLines(filePath, lines);
        }
    }
}
