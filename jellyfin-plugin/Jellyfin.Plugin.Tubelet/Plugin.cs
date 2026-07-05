using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Tubelet;

/// <summary>
/// Tubelet for Jellyfin. Metadata + images + SponsorBlock media segments for a Tubelet
/// library, plus cursor-based watched-state sync. The GUID must equal the server's
/// RepoEndpoints.PluginGuid so the repository manifest and installed plugin line up.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Tubelet";

    public override Guid Id => Guid.Parse("b7c0e5cc-2b6e-4f83-9c6e-3a1d47e05f10");

    public override string Description =>
        "Metadata, images and SponsorBlock media segments for a Tubelet library.";

    /// <summary>Jellyfin provider-id key stamped on matched Series/Episodes so renames can't break identity.</summary>
    public const string ProviderKey = "Tubelet";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        },
    ];
}
