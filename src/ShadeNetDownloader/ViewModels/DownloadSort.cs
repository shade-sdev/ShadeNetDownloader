using System.Collections;
using System.IO;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.ViewModels;

/// <summary>
/// The order of a download list: by name (numbers in names count as numbers: "part 2" before "part 10"), by file type
/// (then name), by size (an unknown size counts as the smallest) or by the date added. Equal ones: the one added last first.
/// </summary>
public sealed class DownloadSortComparer : IComparer, IComparer<DownloadItemViewModel>
{
    readonly DownloadSortKey _by;
    readonly bool _descending;

    public DownloadSortComparer(DownloadSortKey by, bool descending)
    {
        _by = by;
        _descending = descending;
    }

    public int Compare(object? x, object? y) => Compare(x as DownloadItemViewModel, y as DownloadItemViewModel);

    public int Compare(DownloadItemViewModel? a, DownloadItemViewModel? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        var result = _by switch
        {
            DownloadSortKey.Name => NaturalCompare(a.FileName, b.FileName),
            DownloadSortKey.FileType => NaturalCompare(TypeOf(a.FileName), TypeOf(b.FileName)) is var t && t != 0 ? t : NaturalCompare(a.FileName, b.FileName),
            DownloadSortKey.Size => (a.TotalBytes ?? -1).CompareTo(b.TotalBytes ?? -1),
            _ => a.CreatedAt.CompareTo(b.CreatedAt),
        };
        if (_descending) result = -result;
        return result != 0 ? result : b.Sequence.CompareTo(a.Sequence);   // equal ones: the one added last first
    }

    /// <summary>The file type a name says: its extension, lower case, without the dot ("" when it has none).</summary>
    public static string TypeOf(string fileName) => Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();

    /// <summary>Text compared without regard to case, with runs of digits compared as numbers.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var digits = string.CompareOrdinal(na, nb);
                if (digits != 0) return digits;
                continue;
            }

            var c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0) return c;
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
