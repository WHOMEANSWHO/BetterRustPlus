using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using BetterRustPlus.Alerts;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// Override deliberately through ASPNETCORE_URLS for a secured deployment.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5088");
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024);
var options = builder.Configuration.GetSection("Alerts").Get<AlertOptions>() ?? new();
var configError = AlertEngine.ValidateConfiguration(options);
if (configError is not null) throw new InvalidOperationException(configError);
var sourceOptions = builder.Configuration.GetSection("RustSource").Get<RustSourceOptions>() ?? new();
var sourceError = sourceOptions.Validate(options);
if (sourceError is not null) throw new InvalidOperationException(sourceError);
var botOptions = builder.Configuration.GetSection("DiscordBot").Get<DiscordBotOptions>() ?? new();
var botError = botOptions.Validate();
if (botError is not null) throw new InvalidOperationException(botError);
foreach (var target in options.Targets.Values)
{
    if (target.DiscordChannelId is { } channel)
    {
        if (!string.IsNullOrWhiteSpace(target.WebhookUrl) || !botOptions.Enabled || channel == 0 || !botOptions.AllowedChannelIds.Contains(channel))
            throw new InvalidOperationException("Bot delivery requires an enabled bot and an allowed channel; use one route per target.");
    }
    else if (!DiscordSender.IsAllowedWebhook(target.WebhookUrl))
        throw new InvalidOperationException("Invalid Discord webhook target. Use an official HTTPS Discord webhook URL.");
}
var key = Environment.GetEnvironmentVariable("BETTERRUSTPLUS_INGEST_KEY");
bool keyConfigured = key is { Length: >= 32 };
var keyHash = keyConfigured ? SHA256.HashData(Encoding.UTF8.GetBytes(key!)) : null;
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IAlertSender, DiscordSender>();
builder.Services.AddSingleton<AlertEngine>();
builder.Services.AddSingleton(sourceOptions);
builder.Services.AddSingleton<RustSourceState>();
builder.Services.AddHostedService<RustSourceWorker>();
builder.Services.AddSingleton(botOptions);
builder.Services.AddSingleton<DiscordBotState>();
builder.Services.AddHostedService<DiscordBotWorker>();
builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.AddFixedWindowLimiter("ingest", limit =>
    {
        limit.PermitLimit = 60;
        limit.Window = TimeSpan.FromMinutes(1);
        limit.QueueLimit = 0;
        limit.AutoReplenishment = true;
    });
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/events") || context.Request.Path.StartsWithSegments("/team"))
    {
        if (!keyConfigured) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "Protected endpoints are not configured." }); return; }
        var header = context.Request.Headers.Authorization.ToString();
        if (header.Length > 1024 || !header.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), keyHash!))
        { context.Response.StatusCode = 401; return; }
    }
    await next(context);
});
app.UseRateLimiter();
app.MapGet("/health", (RustSourceState source, DiscordBotState bot) => Results.Ok(new
{
    status = "running",
    processId = Environment.ProcessId,
    mode = options.DryRun ? "dry-run" : "discord-delivery-enabled",
    ingestionConfigured = keyConfigured && options.AllowedServerIds.Length > 0,
    gameSourceConnected = source.Snapshot.IsFresh,
    gameSource = source.Snapshot,
    discordBot = bot.State,
    detail = "Optional paired Rust+ source. Only exposed team, cargo and configured alarm observations; no universal airdrop or Deep Sea detector."
}));
app.MapGet("/team", (RustSourceState source) => Results.Ok(new
{
    available = source.Snapshot.IsFresh,
    sourceState = source.Snapshot.State,
    observedAt = source.Team.ObservedAt,
    members = source.Team.Members,
    detail = "Last received paired-team positions; not player tracking outside the paired team. Offline/dead positions may be last known."
}));
app.MapPost("/events", async (GameEvent observation, AlertEngine engine, HttpContext context, CancellationToken ct) =>
{
    var result = await engine.ProcessAsync(observation, ct);
    if (result.Status == "busy") context.Response.Headers.RetryAfter = "1";
    return Results.Json(result, statusCode: result.Status switch
    {
        "rejected" or "expired" => 400,
        "busy" => 503,
        "delivery-failed" => 502,
        _ => 200
    });
}).RequireRateLimiting("ingest");
app.Run();

internal sealed class DiscordSender(AlertOptions options, DiscordBotState bot) : IAlertSender, IDisposable
{
    // Do not use an HTTP logger: webhook addresses contain credentials.
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public static bool IsAllowedWebhook(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.Host == "discord.com" || uri.Host == "discordapp.com")
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)
        && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 4;

    public async Task<DeliveryResult> SendAsync(string target, AlertMessage message, CancellationToken cancellationToken)
    {
        if (options.DryRun) return new(false, "Live sending is disabled.");
        if (!options.Targets.TryGetValue(target, out var destination)) return new(false, "Invalid target.");
        if (destination.DiscordChannelId is { } channel)
            return await bot.SendAsync(channel, message, cancellationToken);
        if (!IsAllowedWebhook(destination.WebhookUrl)) return new(false, "Invalid target.");
        using var response = await http.PostAsJsonAsync(destination.WebhookUrl, new
        {
            content = message.Content,
            allowed_mentions = new { parse = message.MentionEveryone ? new[] { "everyone" } : Array.Empty<string>() }
        }, cancellationToken);
        // Never return Discord response bodies, request URLs or exceptions to callers.
        return response.IsSuccessStatusCode ? new(true) : new(false, "Discord rejected delivery.");
    }
    public void Dispose() => http.Dispose();
}
