using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using McpBridge.Services;

namespace McpBridge.Controls;

/// <summary>Small dependency-free SVG path renderer for the application's local icon set.</summary>
public sealed class SvgIcon : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(SvgIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(System.Windows.Media.Brush), typeof(SvgIcon), new FrameworkPropertyMetadata(System.Windows.Media.Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public System.Windows.Media.Brush Foreground
    {
        get => (System.Windows.Media.Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var source = Source;
        if (string.IsNullOrWhiteSpace(source) || ActualWidth <= 0 || ActualHeight <= 0) return;

        try
        {
            var path = Path.GetFullPath(Path.Combine(AppPaths.InstallDirectory, source!.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) return;

            var document = XDocument.Load(path);
            var root = document.Root;
            if (root == null) return;

            var viewBox = (string?)root.Attribute("viewBox") ?? "0 0 24 24";
            var values = viewBox.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != 4 || values[2] <= 0 || values[3] <= 0) return;

            var scale = Math.Min(ActualWidth / values[2], ActualHeight / values[3]);
            var offsetX = (ActualWidth - values[2] * scale) / 2.0;
            var offsetY = (ActualHeight - values[3] * scale) / 2.0;
            var transform = new Matrix(scale, 0, 0, scale, offsetX - values[0] * scale, offsetY - values[1] * scale);

            drawingContext.PushTransform(new MatrixTransform(transform));
            foreach (var element in root.Descendants().Where(element => element.Name.LocalName == "path"))
            {
                var data = (string?)element.Attribute("d");
                if (string.IsNullOrWhiteSpace(data)) continue;
                var geometry = Geometry.Parse(data);
                var fillValue = (string?)element.Attribute("fill");
                var strokeValue = (string?)element.Attribute("stroke");
                var fill = ParseBrush(fillValue, Foreground);
                var stroke = ParseBrush(strokeValue, null);
                var strokeWidth = ParseDouble((string?)element.Attribute("stroke-width"), 1.5);
                var pen = stroke == null ? null : new System.Windows.Media.Pen(stroke, strokeWidth);
                drawingContext.DrawGeometry(fill, pen, geometry);
            }
            drawingContext.Pop();
        }
        catch
        {
            // A decorative icon must never prevent the main window from opening.
        }
    }

    private static System.Windows.Media.Brush? ParseBrush(string? value, System.Windows.Media.Brush? fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(value, "currentColor", StringComparison.OrdinalIgnoreCase)) return fallback;
        try { return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!; }
        catch { return fallback; }
    }

    private static double ParseDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }
}