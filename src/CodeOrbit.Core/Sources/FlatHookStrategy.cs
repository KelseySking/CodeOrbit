using System.Text.Json;

namespace CodeOrbit.Core.Sources;

/// <summary>
/// Hook installation strategy for flat array format.
/// Format: [{event, command, timeout}]
/// Used by: Cursor, Trae
/// </summary>
internal sealed class FlatHookStrategy : IHookInstallationStrategy
{
    public bool Install(string sourceKey, HookInstallationSpec spec)
    {
        try
        {
            var configPath = HookInstallationUtils.ExpandPath(spec.ConfigPath);
            var existing = HookInstallationUtils.ReadJsonFile(configPath);

            // Parse existing hooks array
            var existingHooks = new List<JsonElement>();
            if (existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in existing.EnumerateArray())
                {
                    // Keep non-CodeOrbit hooks
                    if (item.TryGetProperty("command", out var cmd))
                    {
                        var cmdStr = cmd.GetString() ?? "";
                        if (!cmdStr.Contains("CodeOrbit.Bridge") && !cmdStr.Contains($"--source {sourceKey}"))
                        {
                            existingHooks.Add(item);
                        }
                    }
                }
            }

            // Add new hooks
            var hookCommand = HookInstallationUtils.GetHookCommand(sourceKey);
            foreach (var eventName in spec.Events)
            {
                existingHooks.Add(CreateHookEntry(eventName, hookCommand, spec.TimeoutSeconds));
            }

            // Write back
            var arrayJson = SerializeHooksArray(existingHooks);
            HookInstallationUtils.WriteJsonFile(configPath, arrayJson);

            // Handle extra config if specified
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
                return true; // Already uninstalled

            var existing = HookInstallationUtils.ReadJsonFile(configPath);

            // Remove CodeOrbit hooks
            var remainingHooks = new List<JsonElement>();
            if (existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in existing.EnumerateArray())
                {
                    if (item.TryGetProperty("command", out var cmd))
                    {
                        var cmdStr = cmd.GetString() ?? "";
                        if (!cmdStr.Contains("CodeOrbit.Bridge") && !cmdStr.Contains($"--source {sourceKey}"))
                        {
                            remainingHooks.Add(item);
                        }
                    }
                }
            }

            // Write back
            var arrayJson = SerializeHooksArray(remainingHooks);
            HookInstallationUtils.WriteJsonFile(configPath, arrayJson);

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

            var existing = HookInstallationUtils.ReadJsonFile(configPath);
            if (existing.ValueKind != JsonValueKind.Array)
                return false;

            // Check if any CodeOrbit hook exists
            foreach (var item in existing.EnumerateArray())
            {
                if (item.TryGetProperty("command", out var cmd))
                {
                    var cmdStr = cmd.GetString() ?? "";
                    if (cmdStr.Contains("CodeOrbit.Bridge") || cmdStr.Contains($"--source {sourceKey}"))
                    {
                        return true;
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

    private static JsonElement CreateHookEntry(string eventName, string command, int timeoutSeconds)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName("event");
        writer.WriteStringValue(eventName);
        writer.WritePropertyName("command");
        writer.WriteStringValue(command);
        writer.WritePropertyName("timeout");
        writer.WriteNumberValue(timeoutSeconds);
        writer.WriteEndObject();

        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    private static JsonElement SerializeHooksArray(List<JsonElement> hooks)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartArray();
        foreach (var hook in hooks)
        {
            hook.WriteTo(writer);
        }
        writer.WriteEndArray();

        writer.Flush();
        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }

    private static void InstallExtraConfig(ExtraConfigSpec extraSpec)
    {
        var filePath = HookInstallationUtils.ExpandPath(extraSpec.FilePath);
        HookInstallationUtils.EnsureDirectoryExists(filePath);

        // For TOML files (like Codex config.toml), append the key=value
        if (filePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            var lines = File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : new List<string>();

            // Check if section exists
            var sectionLine = extraSpec.Section ?? "";
            var sectionIndex = lines.FindIndex(l => l.Trim() == sectionLine);

            if (sectionIndex == -1 && !string.IsNullOrEmpty(sectionLine))
            {
                // Add section
                lines.Add("");
                lines.Add(sectionLine);
                sectionIndex = lines.Count - 1;
            }

            // Check if key already exists
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
