namespace CodeOrbit.Core.Sources;

/// <summary>
/// Specification for installing hooks for a plugin-defined CLI source.
/// </summary>
public sealed record HookInstallationSpec(
    string Format,
    string ConfigPath,
    IReadOnlyList<string> Events,
    int TimeoutSeconds,
    ExtraConfigSpec? ExtraConfig);

/// <summary>
/// Extra configuration file specification (e.g., Codex's config.toml).
/// </summary>
public sealed record ExtraConfigSpec(
    string FilePath,
    string? Section,
    string Key,
    string Value);

/// <summary>
/// Supported hook installation formats.
/// </summary>
public static class HookFormats
{
    /// <summary>
    /// Flat array format: [{event, command, timeout}]
    /// Used by: Cursor, Trae
    /// </summary>
    public const string Flat = "flat";

    /// <summary>
    /// Nested format: {hooks: {EventName: [{command, timeout}]}}
    /// Used by: Gemini
    /// </summary>
    public const string Nested = "nested";

    /// <summary>
    /// Codex format: {hooks: {EventName: [{hooks: [{type, command, commandWindows?, timeout, statusMessage?}]}]}}
    /// Used by: Codex CLI
    /// </summary>
    public const string Codex = "codex";

    /// <summary>
    /// Claude matcher format: {hooks: {EventName: [{matcher, hooks: [...]}]}}
    /// Used by: Claude
    /// </summary>
    public const string ClaudeMatcher = "claude-matcher";

    /// <summary>
    /// All supported formats.
    /// </summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Flat,
        Nested,
        Codex,
        ClaudeMatcher
    };

    /// <summary>
    /// Checks if a format is supported.
    /// </summary>
    public static bool IsSupported(string format)
    {
        return Supported.Contains(format);
    }
}
