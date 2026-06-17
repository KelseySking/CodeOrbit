namespace CodeOrbit.Core.Sources;

/// <summary>
/// Discovers and loads CLI source plugins from JSON files.
/// </summary>
public sealed class SourcePluginLoader
{
    private readonly string _pluginDirectory;
    private readonly Action<string>? _logError;
    private readonly Action<string>? _logWarning;

    public SourcePluginLoader(
        string? pluginDirectory = null,
        Action<string>? logError = null,
        Action<string>? logWarning = null)
    {
        _pluginDirectory = pluginDirectory ?? GetDefaultPluginDirectory();
        _logError = logError;
        _logWarning = logWarning;
    }

    public static string GetDefaultPluginDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodeOrbit",
            "sources");
    }

    /// <summary>
    /// Gets the bundled plugin directory path.
    /// Bundled plugins are located relative to the application base directory.
    /// Can be overridden via CODEORBIT_BUNDLED_PLUGINS_DIR environment variable.
    /// </summary>
    public static string GetBundledPluginDirectory()
    {
        // Allow environment variable override for Bridge.exe to use RuntimeHost's directory
        var envOverride = Environment.GetEnvironmentVariable("CODEORBIT_BUNDLED_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        // For single-file deployments, Assembly.Location returns empty string
        // Always use AppContext.BaseDirectory which points to the exe directory
        return Path.Combine(AppContext.BaseDirectory, "bundled-plugins");
    }

    /// <summary>
    /// Loads all valid plugins from the plugin directory.
    /// Invalid plugins are skipped with logging.
    /// Bundled plugins are loaded first and have priority over user plugins.
    /// </summary>
    public IReadOnlyList<ICodeOrbitSourceAdapter> LoadPlugins()
    {
        var adapters = new List<ICodeOrbitSourceAdapter>();
        var loadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Load bundled plugins first (highest priority)
        LoadBundledPlugins(adapters, loadedKeys);

        // 2. Load user plugins (can't override bundled)
        LoadUserPlugins(adapters, loadedKeys);

        return adapters;
    }

    /// <summary>
    /// Returns the set of source keys that come from bundled plugins.
    /// </summary>
    public HashSet<string> GetBundledSourceKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundledDir = GetBundledPluginDirectory();
        if (!Directory.Exists(bundledDir))
            return keys;

        try
        {
            foreach (var filePath in Directory.GetFiles(bundledDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var result = TryLoadPluginFromFile(filePath, []);
                if (result.Success && result.Adapter != null)
                    keys.Add(result.Adapter.SourceKey);
            }
        }
        catch
        {
            // Best effort
        }

        return keys;
    }

    private void LoadBundledPlugins(List<ICodeOrbitSourceAdapter> adapters, HashSet<string> loadedKeys)
    {
        var bundledDir = GetBundledPluginDirectory();
        if (!Directory.Exists(bundledDir))
            return;

        try
        {
            var bundledFiles = Directory.GetFiles(bundledDir, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var filePath in bundledFiles)
            {
                var result = TryLoadPluginFromFile(filePath, loadedKeys);

                if (result.Success && result.Adapter != null)
                {
                    adapters.Add(result.Adapter);
                    loadedKeys.Add(result.Adapter.SourceKey);
                }
                else if (result.ErrorMessage != null)
                {
                    var fileName = Path.GetFileName(filePath);
                    _logError?.Invoke($"Bundled plugin '{fileName}': {result.ErrorMessage}");
                }
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Failed to load bundled plugins: {ex.Message}");
        }
    }

    private void LoadUserPlugins(List<ICodeOrbitSourceAdapter> adapters, HashSet<string> loadedKeys)
    {
        // Ensure directory exists
        try
        {
            if (!Directory.Exists(_pluginDirectory))
            {
                Directory.CreateDirectory(_pluginDirectory);
                return; // Empty directory, no plugins
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Failed to create plugin directory '{_pluginDirectory}': {ex.Message}");
            return;
        }

        // Discover *.json files
        string[] pluginFiles;
        try
        {
            pluginFiles = Directory.GetFiles(_pluginDirectory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Failed to enumerate plugin files in '{_pluginDirectory}': {ex.Message}");
            return;
        }

        // Load each file
        foreach (var filePath in pluginFiles)
        {
            var result = TryLoadPluginFromFile(filePath, loadedKeys);

            if (result.Success && result.Adapter != null)
            {
                adapters.Add(result.Adapter);
                loadedKeys.Add(result.Adapter.SourceKey);
            }
            else if (result.ErrorMessage != null)
            {
                var fileName = Path.GetFileName(filePath);

                // Log errors vs warnings based on severity
                if (result.ValidationError == PluginValidationError.DuplicateSourceKey)
                {
                    _logWarning?.Invoke($"Plugin '{fileName}': {result.ErrorMessage} (skipped)");
                }
                else
                {
                    _logError?.Invoke($"Plugin '{fileName}': {result.ErrorMessage} (skipped)");
                }
            }
        }
    }

    /// <summary>
    /// Attempts to load a single plugin from a file.
    /// </summary>
    public PluginLoadResult TryLoadPluginFromFile(string filePath, IReadOnlyCollection<string> existingKeys)
    {
        try
        {
            // Read file content
            string jsonContent;
            try
            {
                jsonContent = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                return new PluginLoadResult(
                    false,
                    null,
                    $"Failed to read file: {ex.Message}",
                    null);
            }

            // Parse JSON
            var parseResult = SourcePluginJsonParser.Parse(jsonContent, existingKeys);

            if (!parseResult.Success || parseResult.Metadata == null)
            {
                return new PluginLoadResult(
                    false,
                    null,
                    parseResult.Error,
                    parseResult.ValidationError);
            }

            // Create adapter
            var metadata = parseResult.Metadata;
            var adapter = new PluginSourceAdapter(
                metadata.SourceKey,
                metadata.DisplayName,
                metadata.IconName,
                metadata.PermissionResponseStyle,
                metadata.EventMappings,
                metadata.Detection,
                metadata.HookInstallation,
                filePath);

            return new PluginLoadResult(true, adapter, null, null);
        }
        catch (Exception ex)
        {
            return new PluginLoadResult(
                false,
                null,
                $"Unexpected error loading plugin: {ex.Message}",
                null);
        }
    }
}
