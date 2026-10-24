using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ShadeNetDownloader.Services;

/// <summary>
/// What is drawn behind the whole window, over its material: a picture (blurred and faded into the theme's background as
/// asked) and a tint or gradient. The main window binds a layer under everything to this; <see cref="AppThemeService"/>
/// updates it whenever the look changes.
/// </summary>
public sealed partial class BackgroundLayer : ObservableObject
{
    /// <summary>A picture is decoded no wider than this: plenty for a background, and a 50-megapixel photo stays light.</summary>
    const int MaxDecodeWidth = 3840;

    string? _imagePath;

    [ObservableProperty] ImageSource? _image;
    [ObservableProperty] Stretch _imageStretch = Stretch.UniformToFill;
    [ObservableProperty] double _blur;
    [ObservableProperty] Thickness _blurMargin;
    [ObservableProperty] double _dim;
    [ObservableProperty] Brush? _tint;
    [ObservableProperty] double _tintOpacity;

    /// <summary>Why the picture could not be shown (gone, not a picture), or null.</summary>
    [ObservableProperty] string? _imageError;

    public bool HasImage => Image != null;
    public bool HasTint => Tint != null && TintOpacity > 0;

    partial void OnImageChanged(ImageSource? value) => OnPropertyChanged(nameof(HasImage));
    partial void OnTintChanged(Brush? value) => OnPropertyChanged(nameof(HasTint));
    partial void OnTintOpacityChanged(double value) => OnPropertyChanged(nameof(HasTint));

    public void Update(AppearanceSettings look)
    {
        // the tint: one colour, or a gradient between two
        if (look.Tint != null && ThemeColor.TryParse(look.Tint, out var from))
        {
            var to = look.Tint2 != null && ThemeColor.TryParse(look.Tint2, out var second) ? second : from;
            var brush = new LinearGradientBrush(ThemeColor.WithAlpha(from, 255), ThemeColor.WithAlpha(to, 255), look.TintAngle);
            brush.Freeze();
            Tint = brush;
            TintOpacity = look.TintStrength;
        }
        else
        {
            Tint = null;
            TintOpacity = 0;
        }

        // the picture: read again only when it is another one
        if (!string.Equals(look.Image, _imagePath, StringComparison.OrdinalIgnoreCase))
        {
            _imagePath = look.Image;
            Image = null;
            ImageError = null;
            if (look.Image != null)
            {
                Image = TryLoad(look.Image, out var error);
                ImageError = error;
            }
        }

        ImageStretch = look.ImageFit switch
        {
            ImageFit.Fit => Stretch.Uniform,
            ImageFit.Center => Stretch.None,
            ImageFit.Stretch => Stretch.Fill,
            _ => Stretch.UniformToFill,
        };
        Blur = look.ImageBlur;
        // a blurred picture fades out at its edges: drawn a little larger than the window, so the edges are out of sight
        BlurMargin = new Thickness(-look.ImageBlur * 2);
        Dim = look.ImageDim;
    }

    /// <summary>A picture file as a frozen image, or null with the reason.</summary>
    public static ImageSource? TryLoad(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "The background picture is gone. Choose another one.";
                return null;
            }

            var width = 0;
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;   // (the file is not kept open)
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (width > MaxDecodeWidth) image.DecodePixelWidth = MaxDecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or FileFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            error = "The background picture could not be read. Choose another one.";
            return null;
        }
    }
}
