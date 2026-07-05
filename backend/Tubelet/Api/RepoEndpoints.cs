using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Tubelet.Contracts;

namespace Tubelet.Api;

/// <summary>
/// /repo — the container *is* the Jellyfin plugin repository. The Docker build bakes the
/// versioned plugin zips plus a host-independent <c>versions.json</c> into {app}/repo
/// (see jellyfin-plugin/pack-repo.sh). The manifest itself is generated per-request so its
/// <c>sourceUrl</c>s are absolute against whatever host Jellyfin used to reach us — the same
/// image works behind any hostname. Until a real build is baked, a stub manifest is served so
/// the "add repo" flow can already be exercised.
/// </summary>
public static partial class RepoEndpoints
{
    public const string PluginGuid = "b7c0e5cc-2b6e-4f83-9c6e-3a1d47e05f10";
    private const string PluginName = "Tubelet";
    private const string PluginDescription =
        "Metadata, images and SponsorBlock media segments for a Tubelet library.";

    /// <summary>Host-independent version metadata baked next to the zips by pack-repo.sh.</summary>
    private sealed record BakedVersion(
        string Version, string Changelog, string TargetAbi, string Zip, string Checksum, string Timestamp);

    public static void MapRepo(this WebApplication app)
    {
        var repoDir = app.Configuration["TUBELET_REPO_DIR"]
                      ?? Path.Combine(AppContext.BaseDirectory, "repo");

        // Serve the baked zips statically; manifest.json is always generated dynamically below.
        if (Directory.Exists(repoDir))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = "/repo",
                FileProvider = new PhysicalFileProvider(repoDir),
                ServeUnknownFileTypes = true,
            });
        }

        app.MapGet("/repo/manifest.json", (HttpRequest req) =>
        {
            var versions = LoadBakedVersions(repoDir);
            var baseUrl = $"{req.Scheme}://{req.Host}";

            var package = new RepoPackage(
                Guid: PluginGuid,
                Name: PluginName,
                Description: PluginDescription,
                Overview: "Tubelet for Jellyfin",
                Owner: "tubelet",
                Category: "Metadata",
                Versions: versions.Select(v => new RepoVersion(
                    Version: v.Version,
                    Changelog: v.Changelog,
                    TargetAbi: v.TargetAbi,
                    SourceUrl: $"{baseUrl}/repo/{v.Zip}",
                    Checksum: v.Checksum,
                    Timestamp: v.Timestamp)).ToArray());

            return Results.Ok(new[] { package });
        });
    }

    private static IReadOnlyList<BakedVersion> LoadBakedVersions(string repoDir)
    {
        var path = Path.Combine(repoDir, "versions.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), RepoJsonContext.Default.BakedVersionArray)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Local source-gen context so versions.json parsing stays reflection-free (AOT-friendly).
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(
        PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(BakedVersion[]))]
    private partial class RepoJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
