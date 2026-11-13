using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace McpBridge.Services;

public sealed class DependencyStatusView : UserControl
{
    private readonly DataGrid _grid;
    private readonly TextBlock _sectionEyebrow;
    private readonly TextBlock _title;
    private readonly TextBlock _summary;
    private readonly TextBlock _featureSummary;
    private readonly TextBlock _note;
    private readonly Button _refreshButton;
    private readonly Func<Task>? _onRefresh;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Style _cellToolTipStyle;

    private readonly DataGridTextColumn _componentColumn;
    private readonly DataGridTextColumn _minimumColumn;
    private readonly DataGridTextColumn _detectedColumn;
    private readonly DataGridTextColumn _featureColumn;
    private readonly DataGridTextColumn _statusColumn;
    private readonly DataGridTextColumn _detailsColumn;

    private McpDependencyReport? _lastReport;

    public DependencyStatusView(Func<Task>? onRefresh = null)
    {
        _onRefresh = onRefresh;

        Background = ResourceBrush("WindowBackgroundBrush", "#181A1D");
        Foreground = Brush("#EAECEF");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        SetValue(TextOptions.TextRenderingModeProperty, TextRenderingMode.Auto);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerContent = new StackPanel();
        _sectionEyebrow = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#68707A"),
            Margin = new Thickness(0, 0, 0, 7)
        };
        _title = new TextBlock
        {
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F2F5F8"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        _summary = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C7D0D9"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        _featureSummary = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#89939D")
        };

        headerContent.Children.Add(_sectionEyebrow);
        headerContent.Children.Add(_title);
        headerContent.Children.Add(_summary);
        headerContent.Children.Add(_featureSummary);

        var headerCard = new Border
        {
            Margin = new Thickness(0, 0, 0, 14),
            Child = headerContent
        };
        ApplySharedCardStyle(headerCard, new Thickness(18, 16, 18, 16));
        Grid.SetRow(headerCard, 0);
        root.Children.Add(headerCard);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = true,
            CanUserResizeColumns = true,
            CanUserResizeRows = false,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brush("#202328"),
            Foreground = Brush("#D6DCE2"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            RowBackground = Brush("#24282E"),
            AlternatingRowBackground = Brush("#202328"),
            BorderThickness = new Thickness(0),
            RowHeight = 42,
            ColumnHeaderHeight = 38,
            Margin = new Thickness(0),
            EnableRowVirtualization = true
        };

        _grid.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        _grid.SetValue(TextOptions.TextRenderingModeProperty, TextRenderingMode.Auto);
        ScrollViewer.SetCanContentScroll(_grid, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);

        _grid.ColumnHeaderStyle = CreateColumnHeaderStyle();
        _grid.RowStyle = CreateRowStyle();
        _grid.CellStyle = CreateCellStyle();
        _cellToolTipStyle = CreateCellToolTipStyle();

        _componentColumn = Column("Name", 155, 120);
        _minimumColumn = Column("MinimumVersion", 105, 82);
        _detectedColumn = Column("InstalledVersion", 110, 88);
        _featureColumn = Column("Feature", 210, 155);
        _statusColumn = Column("State", 145, 125);
        _detailsColumn = Column("Details", 290, 200);
        _detailsColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);

        _grid.Columns.Add(_componentColumn);
        _grid.Columns.Add(_minimumColumn);
        _grid.Columns.Add(_detectedColumn);
        _grid.Columns.Add(_featureColumn);
        _grid.Columns.Add(_statusColumn);
        _grid.Columns.Add(_detailsColumn);

        var tableCard = new Border
        {
            ClipToBounds = true,
            Child = _grid
        };
        ApplySharedCardStyle(tableCard, new Thickness(2));
        Grid.SetRow(tableCard, 1);
        root.Children.Add(tableCard);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _note = new TextBlock
        {
            FontSize = 10.5,
            Foreground = Brush("#89939D"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0)
        };
        footer.Children.Add(_note);

        _refreshButton = new Button
        {
            Padding = new Thickness(16, 8, 16, 8),
            MinWidth = 142,
            Background = Brush("#263C3B"),
            Foreground = Brush("#D8FFFA"),
            BorderBrush = Brush("#2CE5D0"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Style = CreateRefreshButtonStyle()
        };
        _refreshButton.Click += RefreshButton_Click;
        Grid.SetColumn(_refreshButton, 1);
        footer.Children.Add(_refreshButton);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        _sectionEyebrow.Text = I18n.Tr("КОМПОНЕНТЫ СИСТЕМЫ", "SYSTEM COMPONENTS");
        _title.Text = I18n.Tr("Компоненты и доступные функции", "Components and available features");
        _note.Text = I18n.Tr(
            "Отсутствие необязательного компонента отключает только связанную функцию, а не всё приложение.",
            "A missing optional component disables only its related feature, not the whole application.");
        _refreshButton.Content = I18n.Tr("Повторить проверку", "Check again");

        _componentColumn.Header = I18n.Tr("Компонент", "Component");
        _minimumColumn.Header = I18n.Tr("Минимум", "Minimum");
        _detectedColumn.Header = I18n.Tr("Обнаружено", "Detected");
        _featureColumn.Header = I18n.Tr("Для чего нужен", "Used by");
        _statusColumn.Header = I18n.Tr("Состояние", "Status");
        _detailsColumn.Header = I18n.Tr("Пояснение", "Details");

        if (_lastReport != null)
            SetReport(_lastReport);
    }

    public void SetReport(McpDependencyReport report)
    {
        _lastReport = report;
        _summary.Text = report.Summary;
        _featureSummary.Text = string.Join("   ·   ", new[]
        {
            FeatureLabel(I18n.Tr("HTTP MCP", "HTTP MCP"), report.HttpMcpReady),
            FeatureLabel(I18n.Tr("Файловая система", "Filesystem"), report.FilesystemReady),
            FeatureLabel("STDIO", report.StdioReady),
            FeatureLabel("Python / uvx", report.PythonStdioReady),
            FeatureLabel("Tailscale", report.TailscaleReady)
        });
        _grid.ItemsSource = report.Items;
    }

    public Task RefreshLocalizedReportAsync() => RefreshReportAsync(synchronizeInfrastructure: false);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshReportAsync(synchronizeInfrastructure: true);

    private async Task RefreshReportAsync(bool synchronizeInfrastructure)
    {
        await _refreshGate.WaitAsync();
        _refreshButton.IsEnabled = false;
        _refreshButton.Content = I18n.Tr("Проверяю…", "Checking…");

        try
        {
            var report = await Task.Run(() =>
            {
                McpDependencyResolver.InvalidateCache();
                return McpDependencyResolver.CheckDependencies(forceRefresh: true);
            });

            SetReport(report);

            if (synchronizeInfrastructure && _onRefresh != null)
                await _onRefresh();
        }
        catch (Exception exception)
        {
            _summary.Text = I18n.Tr(
                "Не удалось обновить статус компонентов: ",
                "Could not refresh component status: ") + exception.Message;
        }
        finally
        {
            _refreshButton.IsEnabled = true;
            _refreshButton.Content = I18n.Tr("Повторить проверку", "Check again");
            _refreshGate.Release();
        }
    }

    private static string FeatureLabel(string feature, bool available) =>
        feature + ": " + (available ? I18n.Tr("доступно", "available") : I18n.Tr("недоступно", "unavailable"));

    private DataGridTextColumn Column(string binding, double width, double minimumWidth)
    {
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush("#D6DCE2")));
        textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI")));
        textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        textStyle.Setters.Add(new Setter(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display));
        textStyle.Setters.Add(new Setter(TextOptions.TextRenderingModeProperty, TextRenderingMode.Auto));
        textStyle.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(binding)
        {
            Converter = new CellToolTipConverter(_cellToolTipStyle)
        }));
        textStyle.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 350));
        textStyle.Setters.Add(new Setter(ToolTipService.ShowDurationProperty, 30000));
        textStyle.Setters.Add(new EventSetter(
            FrameworkElement.ToolTipOpeningEvent,
            new ToolTipEventHandler(CellTextBlock_ToolTipOpening)));

        return new DataGridTextColumn
        {
            Header = binding,
            Binding = new Binding(binding),
            Width = new DataGridLength(width),
            MinWidth = minimumWidth,
            ElementStyle = textStyle
        };
    }

    private void CellTextBlock_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock textBlock ||
            string.IsNullOrWhiteSpace(textBlock.Text) ||
            !IsTextBlockTrimmed(textBlock))
        {
            e.Handled = true;
        }
    }

    private static bool IsTextBlockTrimmed(TextBlock textBlock)
    {
        if (textBlock.ActualWidth <= 0 || string.IsNullOrEmpty(textBlock.Text))
            return false;

        var typeface = new Typeface(
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch);

        double pixelsPerDip = 1.0;
        try { pixelsPerDip = VisualTreeHelper.GetDpi(textBlock).PixelsPerDip; }
        catch { }

        var formattedText = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentCulture,
            textBlock.FlowDirection,
            typeface,
            textBlock.FontSize,
            Brushes.Black,
            pixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace > textBlock.ActualWidth + 1.0;
    }

    private static Style CreateColumnHeaderStyle()
    {
        // Keep the native WPF DataGridColumnHeader template.
        // Its built-in PART_LeftHeaderGripper / PART_RightHeaderGripper
        // are wired internally by DataGridColumnHeader for real resizing.
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#1A1D21")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#8F9AA5")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#303740")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 8, 0)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display));
        style.Setters.Add(new Setter(TextOptions.TextRenderingModeProperty, TextRenderingMode.Auto));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#232A30")));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#D6DCE2")));
        style.Triggers.Add(hover);

        return style;
    }

    private static Style CreateRowStyle()
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#D6DCE2")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#2A3038")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#292E35")));
        style.Triggers.Add(hover);

        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#263C3B")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#F2F5F8")));
        style.Triggers.Add(selected);
        return style;
    }

    private static Style CreateCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#D6DCE2")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 0, 8, 0)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

        var template = new ControlTemplate(typeof(DataGridCell));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        presenter.SetValue(FrameworkElement.MarginProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(presenter);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#263C3B")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#F2F5F8")));
        style.Triggers.Add(selected);
        return style;
    }

    private static Style CreateCellToolTipStyle()
    {
        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#171B20")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#EAECEF")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#46505B")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display));
        style.Setters.Add(new Setter(TextOptions.TextRenderingModeProperty, TextRenderingMode.Auto));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 480.0));
        style.Setters.Add(new Setter(System.Windows.Controls.ToolTip.HasDropShadowProperty, true));

        var contentTemplate = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.ForegroundProperty, Brush("#EAECEF"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.MaxWidthProperty, 460.0);
        contentTemplate.VisualTree = text;
        style.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, contentTemplate));

        var template = new ControlTemplate(typeof(ToolTip));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        border.AppendChild(presenter);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        return style;
    }

    private static Style CreateRefreshButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#263C3B")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#D8FFFA")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#2CE5D0")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 8, 16, 8)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2B4543")));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#48F0DE")));
        style.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.OpacityProperty, 0.55));
        style.Triggers.Add(disabled);
        return style;
    }

    private void ApplySharedCardStyle(Border border, Thickness padding)
    {
        var sharedStyle = TryFindResource("CardBorderStyle") as Style;
        if (sharedStyle != null)
        {
            border.Style = sharedStyle;
        }
        else
        {
            border.Background = ResourceBrush("PanelGradient", "#24282E");
            border.BorderBrush = ResourceBrush("PanelBorderBrush", "#2C3036");
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(2);
        }

        border.Padding = padding;
    }

    private Brush ResourceBrush(string key, string fallback) =>
        TryFindResource(key) as Brush ?? Brush(fallback);

    private sealed class CellToolTipConverter : IValueConverter
    {
        private readonly Style _style;

        public CellToolTipConverter(Style style) => _style = style;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value?.ToString();
            return string.IsNullOrWhiteSpace(text)
                ? null!
                : new ToolTip { Content = text, Style = _style };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
