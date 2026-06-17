namespace CodeOrbit.Core.Sources;

/// <summary>
/// Factory for creating hook installation strategies based on format.
/// </summary>
public static class HookStrategyFactory
{
    /// <summary>
    /// Creates a hook installation strategy for the given format.
    /// </summary>
    /// <param name="format">Hook format (flat, nested, codex, claude-matcher)</param>
    /// <returns>Strategy instance, or null if format is unsupported</returns>
    public static IHookInstallationStrategy? Create(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        return format.ToLowerInvariant() switch
        {
            HookFormats.Flat => new FlatHookStrategy(),
            HookFormats.Nested => new NestedHookStrategy(),
            HookFormats.Codex => new CodexHookStrategy(),
            HookFormats.ClaudeMatcher => new ClaudeMatcherStrategy(),
            _ => null
        };
    }
}
