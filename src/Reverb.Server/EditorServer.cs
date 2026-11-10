using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Reverb.Server;

/// <summary>
/// The editor's web server: serves the frontend and the WebSocket for one notebook. Binds to
/// 127.0.0.1 by default. The WebSocket needs the random token from the URL and refuses pages from
/// other origins, since anyone who can talk to it can run code on this machine.
/// </summary>
public sealed class EditorServer : IAsyncDisposable
{
    const int MaxMessageBytes = 16 * 1024 * 1024;

    readonly WebApplication app;

    EditorServer(WebApplication app, NotebookHost notebook, string token, int port, string host)
    {
        this.app = app;
        Notebook = notebook;
        Token = token;
        Port = port;
        var shownHost = host is "0.0.0.0" or "::" or "*" ? "127.0.0.1" : host;
        BaseUrl = $"http://{(shownHost.Contains(':') ? $"[{shownHost}]" : shownHost)}:{port}/";
    }

    public NotebookHost Notebook { get; }
    public string Token { get; }
    public int Port { get; }

    /// <summary>The address without the token, e.g. <c>http://127.0.0.1:5123/</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>The address to open in a browser, token included.</summary>
    public string Url => $"{BaseUrl}?token={Token}";

    public static async Task<EditorServer> StartAsync(EditorOptions options)
    {
        var token = options.Token ?? NewToken();
        var notebook = await NotebookHost.OpenAsync(options);
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var address = options.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(options.Host);
            builder.WebHost.ConfigureKestrel(k => k.Listen(address, options.Port));
            var app = builder.Build();

            var assets = new FrontendAssets();
            HashSet<string>? allowedHosts = null;
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            app.Use(async (ctx, next) =>
            {
                // Refuse requests addressed to another host name: stops DNS rebinding from a web page.
                if (allowedHosts is not null && !allowedHosts.Contains(ctx.Request.Host.Value ?? ""))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(ctx);
            });
            app.Map("/ws", ctx => AcceptAsync(ctx, notebook, token, allowedHosts));
            app.MapGet("/{**path}", (HttpContext ctx, string? path) => assets.ServeAsync(ctx, path));

            await app.StartAsync();
            var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            if (IPAddress.IsLoopback(address))
                allowedHosts = new(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}" };
            return new EditorServer(app, notebook, token, port, options.Host);
        }
        catch
        {
            await notebook.DisposeAsync();
            throw;
        }
    }

    static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    internal static bool TokenMatches(string? given, string token) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Browsers always send Origin on WebSocket requests, so a page from any other site is turned
    /// away. Clients that aren't browsers send none and only need the token.
    /// </summary>
    internal static bool OriginAllowed(string? origin, string requestHost) =>
        string.IsNullOrEmpty(origin) || string.Equals(origin, $"http://{requestHost}", StringComparison.OrdinalIgnoreCase);

    static async Task AcceptAsync(HttpContext ctx, NotebookHost notebook, string token, HashSet<string>? allowedHosts)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!TokenMatches(ctx.Request.Query["token"], token) || !OriginAllowed(ctx.Request.Headers.Origin, ctx.Request.Host.Value ?? ""))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var outgoing = Channel.CreateUnbounded<ServerMessage>(new UnboundedChannelOptions { SingleReader = true });
        var client = notebook.Connect(m => outgoing.Writer.TryWrite(m));
        var sending = SendLoopAsync(socket, outgoing.Reader, ctx.RequestAborted);
        try
        {
            await ReceiveLoopAsync(socket, notebook, client, ctx.RequestAborted);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // The tab went away.
        }
        finally
        {
            notebook.Disconnect(client);
            outgoing.Writer.TryComplete();
            await sending;
        }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    static async Task ReceiveLoopAsync(WebSocket socket, NotebookHost notebook, ClientSession client, CancellationToken cancel)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (socket.State == WebSocketState.Open)
        {
            buffer.Clear();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(8192), cancel);
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big.", CancellationToken.None);
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            ClientMessage? message;
            try
            {
                message = JsonSerializer.Deserialize(buffer.WrittenSpan, BrowserJson.Default.ClientMessage);
            }
            catch (JsonException)
            {
                continue;
            }
            if (message is not null) notebook.Receive(client, message);
        }
    }

    static async Task SendLoopAsync(WebSocket socket, ChannelReader<ServerMessage> messages, CancellationToken cancel)
    {
        try
        {
            await foreach (var m in messages.ReadAllAsync(cancel))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(m, BrowserJson.Default.ServerMessage);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancel);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        await Notebook.DisposeAsync();
    }
}

/// <summary>The built frontend, embedded in this assembly under <c>wwwroot/</c>.</summary>
/// <summary>Files from the built frontend, for other parts of Reverb (<c>reverb export</c> uses the export page's script).</summary>
public static class EmbeddedFrontend
{
    /// <summary>The text of a built frontend file, e.g. "export/export.js", or null if it isn't there.</summary>
    public static string? ReadText(string path)
    {
        var assembly = typeof(EmbeddedFrontend).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/').Equals("wwwroot/" + path, StringComparison.Ordinal));
        if (name is null) return null;
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}

sealed class FrontendAssets
{
    readonly Assembly assembly = typeof(FrontendAssets).Assembly;
    readonly Dictionary<string, string> names;
    readonly FileExtensionContentTypeProvider types = new();

    public FrontendAssets()
    {
        // Resource names from Windows builds use backslashes.
        names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("wwwroot", StringComparison.Ordinal))
            .ToDictionary(n => n["wwwroot/".Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);
    }

    public async Task ServeAsync(HttpContext ctx, string? path)
    {
        path = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (!names.TryGetValue(path, out var resource))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = types.TryGetContentType(path, out var type) ? type : "application/octet-stream";
        // Built assets have content hashes in their names; the page itself must always be fresh.
        ctx.Response.Headers.CacheControl = path.StartsWith("assets/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        await stream.CopyToAsync(ctx.Response.Body);
    }
}
