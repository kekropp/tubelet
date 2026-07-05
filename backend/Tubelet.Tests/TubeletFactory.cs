using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tubelet.Tests;

/// <summary>
/// Boots the real app against a throwaway data directory with fixtures seeded.
/// One instance (= one database) per test class via IClassFixture.
/// </summary>
public sealed class TubeletFactory : WebApplicationFactory<Program>
{
    public string Root { get; } = Directory.CreateTempSubdirectory("tubelet-test-").FullName;

    public TubeletFactory()
    {
        // A stub yt-dlp that always fails fast and never touches the network. The live
        // coordinator claims fixture jobs and gets a transient failure offline, instead of
        // resolving a real yt-dlp on PATH and hitting YouTube.
        var stub = Path.Combine(Root, "yt-dlp-stub");
        File.WriteAllText(stub, "#!/usr/bin/env bash\necho 'ERROR: stub yt-dlp (offline test)' >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _stub = stub;
    }

    private readonly string _stub;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TUBELET_CACHE", Path.Combine(Root, "cache"));
        builder.UseSetting("TUBELET_MEDIA", Path.Combine(Root, "youtube"));
        builder.UseSetting("TUBELET_FIXTURES", "1");
        builder.UseSetting("TUBELET_YTDLP", _stub);
        builder.UseSetting("TUBELET_SB_ENDPOINT", "http://127.0.0.1:9/");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
