using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Tubelet;

/// <summary>
/// Plugin configuration. One user-facing field — the Tubelet server URL — plus a
/// persisted delta cursor so the sync task resumes where it left off across restarts.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Base URL of the Tubelet server, e.g. http://tubelet:8000 (no trailing slash needed).</summary>
    public string ServerUrl { get; set; } = "http://tubelet:8000";

    /// <summary>Opaque cursor for /api/jf/v1/changes; advanced by the sync task. Empty = full initial sync.</summary>
    public string SyncCursor { get; set; } = string.Empty;
}
