using System.Text;

namespace ShadeNet.Core.Hls;

/// <summary>Helpers for WebVTT subtitle files.</summary>
public static class WebVtt
{
    /// <summary>
    /// Joins segmented WebVTT (as HLS delivers it): one header, then every segment's cues in order. A segment's own header
    /// block (including <c>X-TIMESTAMP-MAP</c>) is dropped: the cue times are kept as they are.
    /// </summary>
    public static string Merge(IEnumerable<string> pieces)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var piece in pieces)
        {
            var lines = piece.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿').Split('\n');
            var i = 0;
            if (lines.Length > 0 && lines[0].StartsWith("WEBVTT", StringComparison.Ordinal))
            {
                while (i < lines.Length && lines[i].Trim().Length > 0) i++;   // the header block ends at the first blank line
            }

            var body = string.Join('\n', lines.Skip(i)).Trim('\n');
            if (body.Length > 0) sb.Append(body).Append("\n\n");
        }

        return sb.ToString();
    }
}
