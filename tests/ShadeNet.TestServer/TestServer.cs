using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ShadeNet.TestServer;

/// <summary>One request as the server saw it (for assertions on what the client sent).</summary>
public sealed record RequestRecord(
    string PathAndQuery, string Host, string? Cookie, string? Referer, string? UserAgent, string? Range, string? IfRange, string ConnectionId);

/// <summary>
/// In-process HTTP server that serves deterministic, verifiable content so downloads can be checked byte for byte.
/// <code>
/// GET /file/{size}      the workhorse; switches below are query parameters
///   name=x.bin          Content-Disposition filename          namestar=é.txt   filename* (RFC 5987)
///   etag=abc            strong ETag (changes with Epoch)      ranges=0         ignore Range, plain 200
///   kbps=N              throttle each response to N KiB/s     connlatency=MS   delay first request on a new TCP connection
///   corrupt=1           wrong bytes for ranged requests starting after byte 0
///   lie=1               206 for Range 0-0, but ranges after byte 0 get a full 200
///   badrange=1          206 with a Content-Range that starts at the wrong offset
///   token=T             403 unless T is in ValidTokens (expiring links)
///   dropafter=N&amp;drops=K   the first K requests are cut after N body bytes
///   stallafter=N&amp;stalls=K the first K requests go silent after N body bytes
///   fail=503&amp;failcount=K&amp;retryafter=S  the first K requests answer with that status
/// GET /chunked/{size}   no Content-Length (unknown size), always 200
/// GET /status/{code}    answers with that status
/// GET /redir?to=URL&amp;code=302   redirect
/// GET /gated/{size}/{name}  the file only with a "session=ok" cookie, else 302 to /landing (an HTML page)
/// GET /login/{size}/{name}  302 to /gated with Set-Cookie: session=ok
/// GET /once/{size}/{name}   the file on the first request for this address, 302 to /landing afterwards
/// </code>
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly ConcurrentDictionary<string, byte> _seenConnections = new();
    readonly ConcurrentDictionary<string, int> _counters = new();
    readonly ConcurrentQueue<RequestRecord> _log = new();
    readonly ConcurrentDictionary<string, string> _pages = new();
    long _bytesServed;
    int _requestCount;

    TestServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
        Port = new Uri(baseUrl).Port;
    }

    public string BaseUrl { get; }
    public int Port { get; }

    /// <summary>Same server under another host name (for cross-host redirect tests).</summary>
    public string AltBaseUrl => $"http://localhost:{Port}";

    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ConnectionsSeen => _seenConnections.Count;
    public long BytesServed => Interlocked.Read(ref _bytesServed);
    public IReadOnlyCollection<RequestRecord> Requests => _log.ToArray();

    /// <summary>Bump to make the "remote file" change: new content and (if an etag was requested) a new ETag.</summary>
    public int Epoch;

    /// <summary>Tokens accepted for URLs carrying <c>token=</c>; remove one to expire the link.</summary>
    public HashSet<string> ValidTokens { get; } = new();

    public static async Task<TestServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        TestServer? self = null;
        app.MapGet("/file/{size:long}", (HttpContext ctx, long size) => self!.ServeAsync(ctx, size, chunked: false));
        app.MapGet("/chunked/{size:long}", (HttpContext ctx, long size) => self!.ServeAsync(ctx, size, chunked: true));
        app.MapGet("/status/{code:int}", (HttpContext ctx, int code) =>
        {
            self!.Record(ctx);
            ctx.Response.StatusCode = code;
            return Task.CompletedTask;
        });
        // Like gofile and similar hosts: the file link only works with the session cookie, otherwise it bounces to an HTML page.
        app.MapGet("/gated/{size:long}/{name}", (HttpContext ctx, long size) =>
        {
            if (!ctx.Request.Headers.Cookie.ToString().Contains("session=ok", StringComparison.Ordinal))
            {
                self!.Record(ctx);
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers.Location = "/landing";
                return Task.CompletedTask;
            }

            return self!.ServeAsync(ctx, size, chunked: false);
        });
        // A login redirect: the session cookie arrives on the 302 and must be sent on the next hop.
        app.MapGet("/login/{size:long}/{name}", (HttpContext ctx, long size, string name) =>
        {
            self!.Record(ctx);
            ctx.Response.Headers.Append("Set-Cookie", "session=ok; Path=/");
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = $"/gated/{size}/{name}{ctx.Request.QueryString}";   // switches (kbps=...) survive the redirect
            return Task.CompletedTask;
        });
        // A one-time link: the first request gets the file, every later request for the same address gets a web page.
        app.MapGet("/once/{size:long}/{name}", (HttpContext ctx, long size, string name) =>
        {
            var key = "once:" + ctx.Request.Path + ctx.Request.QueryString;
            if (self!._counters.AddOrUpdate(key, 1, (_, n) => n + 1) == 1) return self.ServeAsync(ctx, size, chunked: false);

            self.Record(ctx);
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = "/landing";
            return Task.CompletedTask;
        });
        app.MapGet("/pages/{id}", async (HttpContext ctx, string id) =>
        {
            self!.Record(ctx);
            if (!self._pages.TryGetValue(id, out var html))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        });
        app.MapGet("/landing", async (HttpContext ctx) =>
        {
            self!.Record(ctx);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<!doctype html><html><body><h1>Please sign in</h1></body></html>");
        });
        app.MapGet("/loop", (HttpContext ctx) =>
        {
            self!.Record(ctx);
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = "/loop";
            return Task.CompletedTask;
        });
        app.MapGet("/redir", (HttpContext ctx) =>
        {
            self!.Record(ctx);
            ctx.Response.StatusCode = int.TryParse(ctx.Request.Query["code"], out var c) ? c : 302;
            ctx.Response.Headers.Location = ctx.Request.Query["to"].ToString();
            return Task.CompletedTask;
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new TestServer(app, address.TrimEnd('/'));
        return self;
    }

    /// <summary>Serves <paramref name="html"/> at a new address (an "owner page" that lists download links).</summary>
    public string AddPage(string html)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _pages[id] = html;
        return $"{BaseUrl}/pages/{id}";
    }

    public string FileUrl(long size, string? name = null, bool ranges = true, string? etag = null,
        int? kbps = null, int? connLatencyMs = null, bool corrupt = false, bool lie = false, string? token = null,
        long? dropAfter = null, int drops = 0, long? stallAfter = null, int stalls = 0,
        int? failStatus = null, int failCount = 0, int? retryAfter = null, bool badRange = false,
        string? nameStar = null, string? unique = null)
    {
        var q = new List<string>();

        void Add(string k, object? v)
        {
            if (v != null) q.Add($"{k}={Uri.EscapeDataString(v.ToString()!)}");
        }

        if (token != null) ValidTokens.Add(token);
        Add("name", name);
        Add("namestar", nameStar);
        if (!ranges) Add("ranges", 0);
        Add("etag", etag);
        Add("kbps", kbps);
        Add("connlatency", connLatencyMs);
        if (corrupt) Add("corrupt", 1);
        if (lie) Add("lie", 1);
        if (badRange) Add("badrange", 1);
        Add("token", token);
        Add("dropafter", dropAfter);
        if (drops > 0) Add("drops", drops);
        Add("stallafter", stallAfter);
        if (stalls > 0) Add("stalls", stalls);
        Add("fail", failStatus);
        if (failCount > 0) Add("failcount", failCount);
        Add("retryafter", retryAfter);
        // Failure counters are per URL; a unique tag keeps tests that share a server independent.
        Add("u", unique ?? Guid.NewGuid().ToString("N")[..8]);
        return $"{BaseUrl}/file/{size}" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    public string ChunkedUrl(long size, string? name = null) =>
        $"{BaseUrl}/chunked/{size}" + (name != null ? "?name=" + Uri.EscapeDataString(name) : "");

    void Record(HttpContext ctx)
    {
        Interlocked.Increment(ref _requestCount);
        var h = ctx.Request.Headers;
        _log.Enqueue(new RequestRecord(
            ctx.Request.Path + ctx.Request.QueryString, ctx.Request.Host.Host,
            h.Cookie.ToString().NullIfEmpty(), h.Referer.ToString().NullIfEmpty(), h.UserAgent.ToString().NullIfEmpty(),
            h.Range.ToString().NullIfEmpty(), h["If-Range"].ToString().NullIfEmpty(), ctx.Connection.Id));
    }

    int Bump(HttpContext ctx, string what) =>
        _counters.AddOrUpdate(what + "|" + ctx.Request.Path + ctx.Request.QueryString, 1, (_, n) => n + 1);

    async Task ServeAsync(HttpContext ctx, long size, bool chunked)
    {
        Record(ctx);
        var req = ctx.Request;
        var res = ctx.Response;
        var q = req.Query;

        string? Q(string k) => q.TryGetValue(k, out var v) ? v.ToString() : null;
        int QInt(string k) => int.TryParse(Q(k), out var v) ? v : 0;
        long QLong(string k) => long.TryParse(Q(k), out var v) ? v : -1;

        var useRanges = Q("ranges") != "0" && !chunked;
        var name = Q("name");
        var etag = Q("etag");
        var token = Q("token");
        var epoch = Epoch;
        var kbps = QInt("kbps");

        if (token != null && !ValidTokens.Contains(token))
        {
            res.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (Q("fail") != null && Bump(ctx, "fail") <= QInt("failcount"))
        {
            res.StatusCode = QInt("fail");
            if (Q("retryafter") != null) res.Headers.RetryAfter = Q("retryafter");
            return;
        }

        if (name != null) res.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        if (Q("namestar") != null)
            res.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(Q("namestar")!)}";
        if (etag != null) res.Headers.ETag = epoch == 0 ? $"\"{etag}\"" : $"\"{etag}-{epoch}\"";
        res.Headers.LastModified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).ToString("R");
        res.ContentType = "application/octet-stream";
        if (useRanges) res.Headers.AcceptRanges = "bytes";

        if (QInt("connlatency") > 0 && _seenConnections.TryAdd(ctx.Connection.Id, 0))
            await Task.Delay(QInt("connlatency"), ctx.RequestAborted);
        else
            _seenConnections.TryAdd(ctx.Connection.Id, 0);

        long start = 0, end = size - 1;
        var ranged = useRanges && req.Headers.Range.Count > 0
                     && TryParseRange(req.Headers.Range.ToString(), size, out start, out end);

        if (ranged && Q("lie") == "1" && start > 0)
        {
            ranged = false;
            start = 0;
            end = size - 1;
        }

        if (ranged && req.Headers.TryGetValue("If-Range", out var ifRange))
        {
            // RFC 9110: serve the range only if the validator still matches, otherwise the whole (changed) file.
            var v = ifRange.ToString();
            if (v != res.Headers.ETag.ToString() && v != res.Headers.LastModified.ToString())
            {
                ranged = false;
                start = 0;
                end = size - 1;
            }
        }

        if (useRanges && req.Headers.Range.Count > 0 && size == 0)
        {
            res.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            res.Headers.ContentRange = "bytes */0";
            return;
        }

        if (ranged)
        {
            res.StatusCode = StatusCodes.Status206PartialContent;
            // badrange: claim to start 1 byte later than what was asked (and was sent).
            var claimed = Q("badrange") == "1" && start > 0 ? start + 1 : start;
            res.Headers.ContentRange = $"bytes {claimed}-{end}/{size}";
        }

        var length = end - start + 1;
        if (!chunked) res.ContentLength = length;

        // Faults only count against requests that are long enough to reach them (so the 1-byte probe is not "the first").
        var drop = Q("dropafter") != null && length > QLong("dropafter") && Bump(ctx, "drop") <= QInt("drops") ? QLong("dropafter") : -1;
        var stall = Q("stallafter") != null && length > QLong("stallafter") && Bump(ctx, "stall") <= QInt("stalls") ? QLong("stallafter") : -1;
        var corruptThis = Q("corrupt") == "1" && ranged && start > 0;

        var buffer = new byte[kbps > 0 ? 16 * 1024 : 64 * 1024];
        var offset = start;
        var sw = Stopwatch.StartNew();
        while (offset <= end)
        {
            var n = (int)Math.Min(buffer.Length, end - offset + 1);
            if (drop >= 0) n = (int)Math.Min(n, Math.Max(0, drop - (offset - start)));
            if (stall >= 0) n = (int)Math.Min(n, Math.Max(0, stall - (offset - start)));

            if (n > 0)
            {
                ContentGen.Fill(buffer.AsSpan(0, n), offset, epoch);
                if (corruptThis)
                    for (var i = 0; i < n; i++) buffer[i] ^= 0xFF;
                await res.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                Interlocked.Add(ref _bytesServed, n);
                offset += n;
            }

            if (drop >= 0 && offset - start >= drop)
            {
                await res.Body.FlushAsync(ctx.RequestAborted);
                ctx.Abort();
                return;
            }

            if (stall >= 0 && offset - start >= stall)
            {
                await res.Body.FlushAsync(ctx.RequestAborted);
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (kbps > 0)
            {
                var due = TimeSpan.FromSeconds((offset - start) / (kbps * 1024.0));
                var wait = due - sw.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ctx.RequestAborted);
            }
        }
    }

    static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;

        var a = spec[..dash];
        var b = spec[(dash + 1)..];
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
            return size > 0;
        }

        if (!long.TryParse(a, out start)) return false;
        if (b.Length > 0 && long.TryParse(b, out var e)) end = Math.Min(e, size - 1);
        return start <= end && start < size;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

static class StringExt
{
    public static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}

/// <summary>Deterministic pseudo-random bytes addressable by file offset.</summary>
public static class ContentGen
{
    public static void Fill(Span<byte> buffer, long offset, int seed = 0)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var x = ((ulong)(offset + i) + (ulong)seed * 0x5851F42D4C957F2DUL) * 0x9E3779B97F4A7C15UL;
            buffer[i] = (byte)(x >> 56);
        }
    }

    public static string Sha256Hex(long size, int seed = 0)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[64 * 1024];
        for (long offset = 0; offset < size; offset += buffer.Length)
        {
            var n = (int)Math.Min(buffer.Length, size - offset);
            Fill(buffer.AsSpan(0, n), offset, seed);
            sha.TransformBlock(buffer, 0, n, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public static string Sha256HexOfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
