using ShadeNet.Core.Models;

namespace ShadeNet.Core.Engine;

/// <summary>Base for failures that the runner understands and maps to a <see cref="JobErrorKind"/>.</summary>
public class DownloadException : Exception
{
    public JobErrorKind Kind { get; }

    public DownloadException(JobErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

public sealed class HttpStatusException : DownloadException
{
    public int StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public HttpStatusException(int statusCode, TimeSpan? retryAfter = null)
        : base(statusCode is 403 or 404 or 410 ? JobErrorKind.AddressExpired : JobErrorKind.HttpStatus,
            $"The server answered {statusCode} {Reason(statusCode)}.")
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>Worth another attempt (server hiccup or rate limiting) rather than failing the job.</summary>
    public bool IsTransient => StatusCode is 408 or 429 or 500 or 502 or 503 or 504;

    static string Reason(int code) => code switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        408 => "Request Timeout",
        410 => "Gone",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "",
    };
}

/// <summary>The server returned a whole-file response to a ranged request.</summary>
public sealed class RangeIgnoredException : DownloadException
{
    public RangeIgnoredException() : base(JobErrorKind.RangeIgnored, "The server ignored the Range header.") { }
}

/// <summary>ETag / Last-Modified / size no longer match what the job was started with.</summary>
public sealed class FileChangedException : DownloadException
{
    public FileChangedException(string detail = "The file on the server has changed since the download started.")
        : base(JobErrorKind.FileChanged, detail) { }
}

/// <summary>Overlapping bytes from two connections differ - multi-connection mode is unsafe on this server.</summary>
public sealed class DataCorruptionException : DownloadException
{
    public DataCorruptionException() : base(JobErrorKind.DataCorruption,
        "Data received on different connections did not match; falling back to a single connection.") { }
}

/// <summary>
/// A link that names a file (<c>.zip</c>, <c>.exe</c>, ...) was answered with an HTML page, usually because the site
/// wants the browser's session cookie and redirected to its landing page instead.
/// </summary>
public sealed class WebPageException : DownloadException
{
    public WebPageException(string? redirectedTo)
        : base(JobErrorKind.WebPage,
            "The server sent a web page instead of the file"
            + (redirectedTo != null ? $" (it redirected to {redirectedTo})" : "")
            + ". The site probably needs your browser's cookies, for example a sign-in or session.") { }
}

public sealed class DiskException : DownloadException
{
    public DiskException(string message, Exception inner) : base(JobErrorKind.Disk, message, inner) { }
}
