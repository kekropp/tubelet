using Microsoft.Extensions.FileProviders;
using Tubelet;
using Tubelet.Api;
using Tubelet.Data;
using Tubelet.Pipeline;
using Tubelet.Realtime;
using Tubelet.Scheduling;
using Tubelet.Sponsorblock;

var builder = WebApplication.CreateBuilder(args);

var mediaDir = builder.Configuration["TUBELET_MEDIA"]
               ?? Path.Combine(builder.Environment.ContentRootPath, "data", "youtube");
var cacheDir = builder.Configuration["TUBELET_CACHE"]
               ?? Path.Combine(builder.Environment.ContentRootPath, "data", "cache");
var paths = new AppPaths(mediaDir, cacheDir);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new Database(Path.Combine(paths.CacheDir, "tubelet.db")));
builder.Services.AddSingleton<Broadcaster>();
// Hub payloads use the same snake_case policy as the REST API so the frontend sees one shape.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

// Download pipeline (DESIGN §4). Subprocess wrappers + coordinator + intake expansion.
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<SbClient>();
builder.Services.AddSingleton<YtDlpLocator>();
builder.Services.AddSingleton<YtDlp>();
builder.Services.AddSingleton<Ffmpeg>();
builder.Services.AddSingleton<MediaArt>();
builder.Services.AddSingleton(new RateGate(NetworkOptions.Defaults.OpsPerHour!.Value));
builder.Services.AddSingleton<PipelineSignal>();
builder.Services.AddSingleton<JobControl>();
builder.Services.AddSingleton<IntakeExpander>();
builder.Services.AddSingleton<SubscriptionScanner>();
builder.Services.AddSingleton<SbRefresher>();
builder.Services.AddHostedService<DownloadCoordinator>();
builder.Services.AddHostedService<Scheduler>();
builder.Services.AddSingleton<Janitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Janitor>());
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // The naming policy must live on the endpoint options — a policy inside the
    // source-gen context is overridden by the runtime web defaults (camelCase).
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
});

var app = builder.Build();

var db = app.Services.GetRequiredService<Database>();
Migrator.Migrate(db);
using (var conn = db.Open())
{
    var reset = Queries.ResetStuckJobs(conn);
    if (reset > 0)
        app.Logger.LogInformation("Recovered {Count} job(s) left mid-flight by a previous run", reset);
}
if (app.Configuration["TUBELET_FIXTURES"] == "1")
{
    FixtureSeeder.Seed(db);
    app.Logger.LogInformation("Fixture data seeded (TUBELET_FIXTURES=1)");
}
if (int.TryParse(app.Configuration["TUBELET_FIXTURES_BULK"], out var bulk) && bulk > 0)
{
    FixtureSeeder.SeedBulk(db, bulk);
    app.Logger.LogInformation("Bulk fixture catalog seeded ({Count} videos) for perf smoke test", bulk);
}

// Thumbnails/art: immutable content addressed by id — cache hard. ETags come free.
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/cache",
    FileProvider = new PhysicalFileProvider(paths.CacheDir),
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable",
});
// Media: range requests give in-browser preview seek for free.
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/media",
    FileProvider = new PhysicalFileProvider(paths.MediaDir),
    ServeUnknownFileTypes = true,
    DefaultContentType = "video/mp4",
});

app.MapJfApi();
app.MapIntakeApi();
app.MapQueueApi();
app.MapLibraryApi();
app.MapSubscriptionApi();
app.MapPlaylistApi();
app.MapSystemApi();
app.MapCookiesApi();
app.MapRepo();
app.MapHub<EventsHub>("/hub");

// Liveness probe for Docker HEALTHCHECK — deliberately trivial (no DB work on every poll).
app.MapGet("/healthz", () => Results.Ok("ok"));

// SPA: serve the built Vue app from wwwroot (populated by `npm run build`) with a fallback
// so client-side routes resolve to index.html. Falls back to a plain notice if not built yet.
var indexHtml = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
if (File.Exists(indexHtml))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}
else
{
    app.MapGet("/", () => Results.Text(
        "Tubelet backend is running. Build the frontend (cd frontend && npm run build) to serve the web UI.",
        "text/plain"));
}

app.Run();

public partial class Program;
