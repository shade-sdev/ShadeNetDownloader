using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Views;

public partial class SettingsView : UserControl
{
    SettingsViewModel? _watched;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Watch(null);
        Loaded += (_, _) => Watch(DataContext as SettingsViewModel);
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Watch(e.NewValue as SettingsViewModel);

    void Watch(SettingsViewModel? vm)
    {
        if (ReferenceEquals(vm, _watched)) return;
        if (_watched != null) _watched.PropertyChanged -= OnVmPropertyChanged;
        _watched = vm;
        if (_watched != null) _watched.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>Another section: it slides in.</summary>
    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Section) && IsLoaded) Animate.FadeSlideIn(Sections, offsetY: 10);
    }
}
