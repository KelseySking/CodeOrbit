using System.Text.Json;

namespace CodeOrbit.Core.Sources;

/// <summary>
/// Utilities for hook installation strategies.
/// </summary>
internal static class HookInstallationUtils
{
    /// <summary>
    /// Expands path markers (~/, $HOME, %APPDATA%, etc.) to actual paths.
    /// </summary>
    public static string ExpandPath(string path)
    {
        if (path.StartsWith("~/") || path.StartsWith("$HOME/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Replace("~/", home + Path.DirectorySeparatorChar)
                       .Replace("$HOME/", home + Path.DirectorySeparatorChar);
        }

        if (path.StartsWith("%APPDATA%/"))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return path.Replace("%APPDATA%/", appData + Path.DirectorySeparatorChar);
        }

        if (path.StartsWith("%USERPROFILE%/"))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Replace("%USERPROFILE%/", userProfile + Path.DirectorySeparatorChar);
        }

        return path;
    }

    /// <summary>
    /// Ensures the directory for the given file path exists.
    /// </summary>
    public static void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Reads a JSON file, or returns empty object if file doesn't exist.
    /// </summary>
    public static JsonElement ReadJsonFile(string filePath)
    {
        if (!File.Exists(filePath))
            return JsonDocument.Parse("{}").RootElement;

        try
        {
            var content = File.ReadAllText(filePath);
            return JsonDocument.Parse(content).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    /// <summary>
    /// Writes a JSON element to file with pretty formatting.
    /// </summary>
    public static void WriteJsonFile(string filePath, JsonElement element)
    {
        EnsureDirectoryExists(filePath);

        var options = new JsonWriterOptions { Indented = true };
        using var stream = File.Create(filePath);
        using var writer = new Utf8JsonWriter(stream, options);
        element.WriteTo(writer);
    }

    /// <summary>
    /// Gets the hook command for a given source key.
    /// This is the Bridge executable path with --source parameter.
    /// Delegates to ConfigInstaller to get the correct runtime path.
    /// </summary>
    public static string GetHookCommand(string sourceKey)
    {
        // Use ConfigInstaller's private GetHookCommand via reflection
        // This ensures we get the correct runtime bridge.exe path with proper quoting
        var method = typeof(Services.ConfigInstaller).GetMethod(
            "GetHookCommand",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (method != null)
        {
            return (string?)method.Invoke(null, new object?[] { sourceKey }) ?? $"CodeOrbit.Bridge --source {sourceKey}";
        }

        return $"CodeOrbit.Bridge --source {sourceKey}";
    }

    /// <summary>
    /// Gets the Bridge executable path from ConfigInstaller.
    /// </summary>
    public static string GetBridgeExecutablePath()
    {
        var property = typeof(Services.ConfigInstaller).GetProperty(
            "RuntimeBridgeExePath",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        if (property != null)
        {
            return (string?)property.GetValue(null) ?? "CodeOrbit-bridge.exe";
        }

        return "CodeOrbit-bridge.exe";
    }

    /// <summary>
    /// Merges two JSON objects, with target taking precedence for conflicts.
    /// </summary>
    public static JsonElement MergeJsonObjects(JsonElement source, JsonElement target)
    {
        if (source.ValueKind != JsonValueKind.Object || target.ValueKind != JsonValueKind.Object)
            return target;

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        // Write all properties from source
        foreach (var property in source.EnumerateObject())
        {
            if (!target.TryGetProperty(property.Name, out _))
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
        }

        // Write all properties from target (overrides source)
        foreach (var property in target.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();

        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement;
    }
}
