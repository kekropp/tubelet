using System.Runtime.InteropServices;

namespace Tubelet.Pipeline;

/// <summary>
/// Resolves the yt-dlp binary and caches its version. Preference order:
/// <c>TUBELET_YTDLP</c> env → self-downloaded <c>{cache}/bin/yt-dlp</c> → <c>yt-dlp</c> on PATH.
/// A self-downloaded binary is preferred over PATH so the self-update path (weekly cron /
/// dev bootstrap) can fix YouTube breakage without a new image.
/// </summary>
public sealed class YtDlpLocator(AppPaths paths, IConfiguration config, ILogger<YtDlpLocator> log)
{
    private const string LatestUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private readonly object _lock = new();
    private string? _resolvedPath;
    private string? _version;

    /// <summary>The binary to spawn. Throws if none is resolvable.</summary>
    public string Path => Resolve() ?? throw new FileNotFoundException(
        "yt-dlp not found. Set TUBELET_YTDLP, put it on PATH, or download it (Settings → Maintenance).");

    /// <summary>Best-effort resolution; null if nothing is available yet.</summary>
    public string? Resolve()
    {
        lock (_lock)
        {
            if (_resolvedPath is not null && File.Exists(_resolvedPath)) return _resolvedPath;
            _resolvedPath = null;
            _version = null;

            var env = config["TUBELET_YTDLP"];
            var cached = System.IO.Path.Combine(paths.BinDir, Exe("yt-dlp"));
            foreach (var candidate in new[] { env, File.Exists(cached) ? cached : null, OnPath() })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    _resolvedPath = candidate;
                    return candidate;
                }
            }
            return null;
        }
    }

    /// <summary>yt-dlp --version, cached. Null if unresolved or the call fails (offline/first boot).</summary>
    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        if (_version is not null) return _version;
        var path = Resolve();
        if (path is null) return null;
        try
        {
            var r = await Proc.RunAsync(path, ["--version"], ct).ConfigureAwait(false);
            if (r.ExitCode == 0) _version = r.Stdout.Trim();
        }
        catch (Exception e)
        {
            log.LogWarning(e, "yt-dlp --version failed");
        }
        return _version;
    }

    /// <summary>
    /// Download the latest release binary to <c>{cache}/bin/yt-dlp</c>, verify with --version,
    /// and prefer it thereafter. Used by the dev bootstrap now and the self-update cron in phase 5.
    /// </summary>
    public async Task<string> DownloadLatestAsync(HttpClient http, CancellationToken ct = default)
    {
        var asset = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos"
            : "yt-dlp_linux";
        var dest = System.IO.Path.Combine(paths.BinDir, Exe("yt-dlp"));
        var tmp = dest + ".part";

        log.LogInformation("Downloading yt-dlp ({Asset}) → {Dest}", asset, dest);
        await using (var src = await http.GetStreamAsync(LatestUrl + asset, ct).ConfigureAwait(false))
        await using (var f = File.Create(tmp))
            await src.CopyToAsync(f, ct).ConfigureAwait(false);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var verify = await Proc.RunAsync(tmp, ["--version"], ct).ConfigureAwait(false);
        if (verify.ExitCode != 0)
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"Downloaded yt-dlp failed --version: {verify.Stderr}");
        }

        File.Move(tmp, dest, overwrite: true);
        lock (_lock) { _resolvedPath = dest; _version = verify.Stdout.Trim(); }
        log.LogInformation("yt-dlp {Version} installed", _version);
        return _version!;
    }

    private static string Exe(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

    private static string? OnPath()
    {
        var name = Exe("yt-dlp");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var full = System.IO.Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
