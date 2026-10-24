using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.Util;

/// <summary>
/// The app's motion, in one place: short ease-out glides (150-320 ms) that all follow Settings > Appearance > Motion (and
/// Windows' "Animation effects" in its default). With motion off every change still happens, at once.
/// <para>
/// An animation here never holds on to the property it animates: the final value is set as the property's own value first,
/// and the animation only plays the way there, so later changes (layout, bindings) are never fought by a finished animation.
/// </para>
/// </summary>
public static class Animate
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(220));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(320));

    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>A small overshoot, for things that "arrive" (a badge that pops).</summary>
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    public static bool Enabled => Motion.Allowed;

    static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    /// <summary>A number glides from what it shows now to <paramref name="to"/> (or is set at once when motion is off).</summary>
    public static void To<T>(T target, DependencyProperty property, double to, Duration? duration = null, IEasingFunction? ease = null,
        Action? done = null) where T : DependencyObject, IAnimatable
    {
        var from = (double)target.GetValue(property);   // (mid-animation: where it is now)
        target.SetValue(property, to);
        if (!Enabled || double.IsNaN(from) || Math.Abs(from - to) < 0.01)
        {
            target.BeginAnimation(property, null);
            done?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(from, to, duration ?? Base) { EasingFunction = ease ?? EaseOut, FillBehavior = FillBehavior.Stop };
        if (done != null) animation.Completed += (_, _) => done();
        target.BeginAnimation(property, animation);
    }

    /// <summary>Content arrives: it fades in while sliding the last few pixels into place (down to up by default).</summary>
    public static void FadeSlideIn(FrameworkElement element, double offsetY = 12, double offsetX = 0, Duration? duration = null)
    {
        if (!Enabled || !element.IsVisible) return;
        var d = duration ?? Base;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });

        if (Translation(element) is not { } move) return;
        if (offsetY != 0)
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        if (offsetX != 0)
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offsetX, 0, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>A quick swell and settle, to say "this just changed".</summary>
    public static void Pop(FrameworkElement element, double scale = 1.12)
    {
        if (!Enabled || !element.IsVisible) return;
        if (Scaling(element) is not { } grow) return;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(scale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), EaseOut));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), Spring));
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>The element's own move transform: made when it has none, null when it already has another kind (left alone).</summary>
    static TranslateTransform? Translation(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform { IsFrozen: false } t:
                return t;
            case TransformGroup { IsFrozen: false } group:
                return group.Children.OfType<TranslateTransform>().FirstOrDefault(t => !t.IsFrozen);
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
                var made = new TranslateTransform();
                element.RenderTransform = made;
                return made;
            default:
                return null;
        }
    }

    static ScaleTransform? Scaling(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case ScaleTransform { IsFrozen: false } s:
                return s;
            case TransformGroup { IsFrozen: false } group:
                return group.Children.OfType<ScaleTransform>().FirstOrDefault(s => !s.IsFrozen);
            case null:
            case MatrixTransform { Matrix.IsIdentity: true }:
                var made = new ScaleTransform();
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = made;
                return made;
            default:
                return null;
        }
    }
}
