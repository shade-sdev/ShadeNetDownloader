using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Services;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

/// <summary>One toast: a short message with an optional action button (e.g. "Undo", "Open").</summary>
public sealed partial class Toast : ObservableObject
{
    public Toast(string message, ToastKind kind, string? actionText, Action? action, TimeSpan duration)
    {
        Message = message;
        Kind = kind;
        ActionText = actionText;
        Action = action;
        Duration = duration;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    public string? ActionText { get; }
    public Action? Action { get; }
    public TimeSpan Duration { get; }
    public bool HasAction => ActionText != null && Action != null;

    public SymbolRegular Icon => Kind switch
    {
        ToastKind.Success => SymbolRegular.CheckmarkCircle24,
        ToastKind.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };
}

/// <summary>A single bottom-centre toast at a time; a new one replaces the old one. Auto-hides.</summary>
public sealed partial class ToastService : ObservableObject
{
    readonly DispatcherTimer _timer = new();
    Dispatcher? _dispatcher;

    [ObservableProperty] Toast? _current;

    public ToastService() => _timer.Tick += (_, _) => Dismiss();

    public void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null,
        TimeSpan? duration = null)
    {
        void Do()
        {
            var toast = new Toast(message, kind, actionText, action, duration ?? TimeSpan.FromSeconds(5));
            _timer.Stop();
            Current = toast;
            _timer.Interval = toast.Duration;
            _timer.Start();
        }

        _dispatcher ??= Dispatcher.CurrentDispatcher;
        if (_dispatcher.CheckAccess()) Do();
        else _dispatcher.BeginInvoke(Do);
    }

    [RelayCommand]
    public void Dismiss()
    {
        _timer.Stop();
        Current = null;
    }

    [RelayCommand]
    void RunAction()
    {
        var action = Current?.Action;
        Dismiss();
        action?.Invoke();
    }
}
