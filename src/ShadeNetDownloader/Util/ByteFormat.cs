using System.Globalization;

namespace ShadeNetDownloader.Util;

public static class ByteFormat
{
    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>"1.5 MB", "980 KB", "12 B" (binary multiples, the way Windows Explorer shows sizes).</summary>
    public static string Size(long bytes)
    {
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        var text = i == 0 ? v.ToString("0", CultureInfo.CurrentCulture)
            : v >= 100 ? v.ToString("0", CultureInfo.CurrentCulture)
            : v >= 10 ? v.ToString("0.0", CultureInfo.CurrentCulture)
            : v.ToString("0.00", CultureInfo.CurrentCulture);
        return $"{text} {Units[i]}";
    }

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

    public static string Eta(TimeSpan? eta)
    {
        if (eta is not { } t) return "--";
        if (t.TotalSeconds < 1) return "<1 s";
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{(int)t.TotalDays} d {t.Hours} h";
    }
}
