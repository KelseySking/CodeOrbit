namespace CodeIsland.Core.Sources;

/// <summary>
/// Detection engine that matches process ancestry against plugin-defined detection rules.
/// </summary>
public static class PluginProcessDetector
{
    private static Lazy<IReadOnlyList<DetectionRule>> _rules = new(LoadDetectionRules);

    /// <summary>
    /// Detects CLI source from process list using plugin detection rules.
    /// This overload accepts a simple process list with name and executable path.
    /// </summary>
    public static string? DetectFromProcessList(IEnumerable<(string Name, string? ExecutablePath)> processes)
    {
        var processInfos = processes
            .Select(p => new ProcessInfo(0, 0, p.Name, p.ExecutablePath, DateTime.UtcNow))
            .ToList();

        return DetectFromAncestry(processInfos);
    }

    /// <summary>
    /// Detects CLI source from process ancestry using plugin detection rules.
    /// Returns null if no plugin matches.
    /// </summary>
    public static string? DetectFromAncestry(IReadOnlyList<ProcessInfo> ancestry)
    {
        // Sort by priority (descending) and check each rule
        var rules = _rules.Value.OrderByDescending(r => r.Priority);

        foreach (var rule in rules)
        {
            if (rule.Matches(ancestry))
                return rule.SourceKey;
        }

        return null;
    }

    /// <summary>
    /// Invalidates the detection rules cache.
    /// Call this when plugins change to reload detection rules.
    /// </summary>
    public static void InvalidateCache()
    {
        _rules = new Lazy<IReadOnlyList<DetectionRule>>(LoadDetectionRules);
    }

    private static IReadOnlyList<DetectionRule> LoadDetectionRules()
    {
        var rules = new List<DetectionRule>();

        // Load detection rules from all plugins
        var loader = new SourcePluginLoader();
        var plugins = loader.LoadPlugins();

        foreach (var plugin in plugins)
        {
            // Skip if plugin doesn't implement detection interface
            if (plugin is not IPluginSourceAdapter pluginAdapter)
                continue;

            var detectionRule = pluginAdapter.GetDetectionRule();
            if (detectionRule != null)
            {
                rules.Add(detectionRule);
            }
        }

        return rules;
    }
}

/// <summary>
/// Extended interface for plugin source adapters that support detection.
/// </summary>
public interface IPluginSourceAdapter : ICodeIslandSourceAdapter
{
    /// <summary>
    /// Gets the detection rule for this plugin, or null if none defined.
    /// </summary>
    DetectionRule? GetDetectionRule();

    /// <summary>
    /// Gets the hook installation spec for this plugin, or null if none defined.
    /// </summary>
    HookInstallationSpec? GetHookInstallationSpec();
}
