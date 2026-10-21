using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Models;

public enum NavKind
{
    All,
    Downloading,
    Queued,
    Paused,
    Completed,
    Failed,
    Category,
    Settings,
}

/// <summary>One entry in the sidebar: a status filter, a category, or the settings page.</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(NavKind kind, string title, SymbolRegular icon, string? category = null)
    {
        Kind = kind;
        Title = title;
        Icon = icon;
        Category = category;
    }

    public NavKind Kind { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }

    /// <summary>For <see cref="NavKind.Category"/> items: the category name.</summary>
    public string? Category { get; }

    [ObservableProperty] bool _isSelected;

    /// <summary>Number of downloads in this view (0 hides the badge).</summary>
    [ObservableProperty] int _count;
}
