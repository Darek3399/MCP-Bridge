using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace McpBridge.Services;

public sealed class StringOnlyConverter : IValueConverter
{
    public static readonly StringOnlyConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as string ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRoundSmall = 3;

    public static readonly DependencyProperty ClipCornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "ClipCornerRadius",
            typeof(double),
            typeof(WindowChromeHelper),
            new PropertyMetadata(0.0, OnClipCornerRadiusChanged));

    public static double GetClipCornerRadius(DependencyObject obj) =>
        (double)obj.GetValue(ClipCornerRadiusProperty);

    public static void SetClipCornerRadius(DependencyObject obj, double value) =>
        obj.SetValue(ClipCornerRadiusProperty, value);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    public static void Configure(
        Window window,
        Border outerBorder,
        FrameworkElement clipTarget,
        double cornerRadius = 5.0)
    {
        var borderColor = outerBorder.BorderBrush is SolidColorBrush sb
            ? ToColorRef(sb.Color)
            : 0x00151311;

        var nativeBorderApplied = false;

        void ApplyNativeFrame()
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                var darkMode = 1;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

                var preference = DwmwcpRoundSmall;
                DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));

                var chrome = WindowChrome.GetWindowChrome(window);
                if (chrome != null)
                {
                    if (chrome.IsFrozen)
                    {
                        chrome = (WindowChrome)chrome.Clone();
                        WindowChrome.SetWindowChrome(window, chrome);
                    }
                    if (chrome.CornerRadius != new CornerRadius(0))
                        chrome.CornerRadius = new CornerRadius(0);
                }

                var color = borderColor;
                if (DwmSetWindowAttribute(handle, DwmwaBorderColor, ref color, sizeof(int)) == 0)
                {
                    nativeBorderApplied = true;
                    if (chrome != null && chrome.GlassFrameThickness != new Thickness(1))
                        chrome.GlassFrameThickness = new Thickness(1);

                    outerBorder.BorderThickness = new Thickness(0);
                    outerBorder.CornerRadius = new CornerRadius(0);
                    clipTarget.Clip = null;
                }
            }
            catch { }
        }

        void UpdateFallbackClip()
        {
            if (nativeBorderApplied)
            {
                outerBorder.CornerRadius = new CornerRadius(0);
                clipTarget.Clip = null;
                return;
            }

            var isMaximized = window.WindowState == WindowState.Maximized;
            outerBorder.CornerRadius = new CornerRadius(0);
            clipTarget.Clip = null;
        }

        window.SourceInitialized += (_, _) =>
        {
            ApplyNativeFrame();
            UpdateFallbackClip();
        };
        window.StateChanged += (_, _) => UpdateFallbackClip();
        clipTarget.SizeChanged += (_, _) => UpdateFallbackClip();
        window.Loaded += (_, _) =>
        {
            ApplyNativeFrame();
            UpdateFallbackClip();
        };
    }

    public static void ClipToCornerRadius(FrameworkElement target, double cornerRadius)
    {
        SetClipCornerRadius(target, cornerRadius);
    }

    private static void OnClipCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        var radius = (double)e.NewValue;

        element.SizeChanged -= Element_SizeChanged;
        element.Loaded -= Element_Loaded;

        if (radius > 0.0)
        {
            element.SizeChanged += Element_SizeChanged;
            element.Loaded += Element_Loaded;
            ApplyCurrentClip(element);
        }
        else
        {
            element.Clip = null;
        }
    }

    private static void Element_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
            ApplyCurrentClip(element);
    }

    private static void Element_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            ApplyCurrentClip(element);
    }

    private static void ApplyCurrentClip(FrameworkElement element)
    {
        var radius = GetClipCornerRadius(element);
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (radius <= 0.0 || width <= 0 || height <= 0) return;
        element.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }
}