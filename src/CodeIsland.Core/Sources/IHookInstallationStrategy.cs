namespace CodeIsland.Core.Sources;

/// <summary>
/// Strategy for installing hooks for a specific format.
/// </summary>
public interface IHookInstallationStrategy
{
    /// <summary>
    /// Installs hooks for the given source using the provided specification.
    /// </summary>
    /// <param name="sourceKey">CLI source key (e.g., "my-cli")</param>
    /// <param name="spec">Hook installation specification</param>
    /// <returns>True if installation succeeded, false otherwise</returns>
    bool Install(string sourceKey, HookInstallationSpec spec);

    /// <summary>
    /// Uninstalls hooks for the given source using the provided specification.
    /// </summary>
    /// <param name="sourceKey">CLI source key</param>
    /// <param name="spec">Hook installation specification</param>
    /// <returns>True if uninstallation succeeded, false otherwise</returns>
    bool Uninstall(string sourceKey, HookInstallationSpec spec);

    /// <summary>
    /// Checks if hooks are already installed for the given source.
    /// </summary>
    /// <param name="sourceKey">CLI source key</param>
    /// <param name="spec">Hook installation specification</param>
    /// <returns>True if hooks are installed, false otherwise</returns>
    bool IsInstalled(string sourceKey, HookInstallationSpec spec);
}
