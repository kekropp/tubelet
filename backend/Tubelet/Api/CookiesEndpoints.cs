using System.Runtime.InteropServices;
using Tubelet.Data;
using Tubelet.Pipeline;
using Tubelet.Realtime;

namespace Tubelet.Api;

/// <summary>
/// Cookie-jar management (DESIGN §6). Upload/paste a Netscape cookies.txt → stored 0600 at
/// {cache}/cookies/cookies.txt, then a metadata-only yt-dlp call validates it and surfaces the
/// logged-in identity. The jar is <b>write-only</b>: only its status (present/valid/age) is ever
/// served back. A failing jar raises a persistent banner.
/// </summary>
public static class CookiesEndpoints
{
    private static string JarPath(AppPaths paths) => Path.Combine(paths.CookiesDir, "cookies.txt");

    public static void MapCookiesApi(this WebApplication app)
    {
        // Upload/paste. Accepts multipart (field "file") or a raw text/plain body.
        app.MapPost("/api/v1/cookies", async (HttpRequest req, AppPaths paths, Database db, YtDlp ytdlp, Broadcaster bc) =>
        {
            var text = await ReadUpload(req).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "empty cookie file" });
            if (!LooksLikeNetscape(text))
                return Results.BadRequest(new { error = "not a Netscape cookies.txt (expected tab-separated cookie lines)" });

            var jar = JarPath(paths);
            await File.WriteAllTextAsync(jar, text).ConfigureAwait(false);
            Restrict(jar);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var conn = db.Open()) Database.SetSetting(conn, "cookies_uploaded_at", now.ToString());

            var status = await ValidateAndStore(paths, db, ytdlp, bc).ConfigureAwait(false);
            return Results.Ok(status);
        }).DisableAntiforgery();

        app.MapGet("/api/v1/cookies", (AppPaths paths, Database db) => Results.Ok(Status(paths, db)));

        app.MapPost("/api/v1/cookies/validate", async (AppPaths paths, Database db, YtDlp ytdlp, Broadcaster bc) =>
        {
            if (!File.Exists(JarPath(paths))) return Results.NotFound(new { error = "no cookie jar uploaded" });
            return Results.Ok(await ValidateAndStore(paths, db, ytdlp, bc).ConfigureAwait(false));
        });

        app.MapDelete("/api/v1/cookies", (AppPaths paths, Database db) =>
        {
            try { File.Delete(JarPath(paths)); } catch (IOException) { }
            using var conn = db.Open();
            foreach (var k in new[] { "cookies_uploaded_at", "cookies_validated_at", "cookies_valid", "cookies_identity", "cookies_message" })
                Database.SetSetting(conn, k, "");
            return Results.NoContent();
        });
    }

    private static async Task<CookieStatusDoc> ValidateAndStore(AppPaths paths, Database db, YtDlp ytdlp, Broadcaster bc)
    {
        var check = await ytdlp.ValidateCookiesAsync(JarPath(paths)).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var conn = db.Open())
        {
            Database.SetSetting(conn, "cookies_validated_at", now.ToString());
            Database.SetSetting(conn, "cookies_valid", check.Ok ? "1" : "0");
            Database.SetSetting(conn, "cookies_identity", check.Identity ?? "");
            Database.SetSetting(conn, "cookies_message", check.Error ?? "");
        }
        if (!check.Ok)
            await bc.SystemBanner("cookies", "Cookie jar rejected by YouTube — downloads will run without cookies. Re-upload a fresh cookies.txt (Settings → Cookies).").ConfigureAwait(false);
        return Status(paths, db);
    }

    private static CookieStatusDoc Status(AppPaths paths, Database db)
    {
        using var conn = db.Open();
        var present = File.Exists(JarPath(paths));
        return new CookieStatusDoc(
            Present: present,
            Valid: present && Database.GetSetting(conn, "cookies_valid") == "1",
            Identity: Nz(Database.GetSetting(conn, "cookies_identity")),
            UploadedAt: long.TryParse(Database.GetSetting(conn, "cookies_uploaded_at"), out var u) ? u : null,
            ValidatedAt: long.TryParse(Database.GetSetting(conn, "cookies_validated_at"), out var v) ? v : null,
            Message: Nz(Database.GetSetting(conn, "cookies_message")));
    }

    private static async Task<string> ReadUpload(HttpRequest req)
    {
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync().ConfigureAwait(false);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is not null)
            {
                using var r = new StreamReader(file.OpenReadStream());
                return await r.ReadToEndAsync().ConfigureAwait(false);
            }
            return form["cookies"].ToString();
        }
        using var reader = new StreamReader(req.Body);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    // Netscape cookies.txt: either the canonical header, or TAB-separated fields on a data line.
    private static bool LooksLikeNetscape(string text) =>
        text.Contains("# Netscape HTTP Cookie File", StringComparison.OrdinalIgnoreCase)
        || text.Contains("# HTTP Cookie File", StringComparison.OrdinalIgnoreCase)
        || text.Split('\n').Any(l => !l.StartsWith('#') && l.Count(c => c == '\t') >= 6);

    private static void Restrict(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
    }

    private static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
