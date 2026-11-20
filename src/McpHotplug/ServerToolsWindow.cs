using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using McpHotplug.Services;

namespace McpHotplug;

public sealed class ServerToolsWindow : Window
{
    private static readonly Brush CategoryBrush = new SolidColorBrush(Color.FromRgb(232, 193, 112));
    private static readonly Brush ToolBrush = new SolidColorBrush(Color.FromRgb(44, 229, 208));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(127, 135, 144));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(245, 166, 35));

    private readonly McpInfrastructureService _infrastructure;
    private readonly string _serverId;
    private readonly string _serverName;
    private readonly HashSet<string> _initialDisabledSet;
    private readonly ObservableCollection<ToolNodeVm> _rootNodes = new();
    private readonly CancellationTokenSource _windowCts = new();

    private readonly Stack<List<string>> _undoStack = new();
    private readonly Stack<List<string>> _redoStack = new();
    private List<string> _lastCommittedSnapshot = new();

    private readonly CheckBox _masterCheckBox;
    private readonly Button _undoButton;
    private readonly Button _okButton;
    private readonly StackPanel _loadingPanel;
    private readonly RotateTransform _spinnerRotate;
    private readonly TextBlock _statsText;
    private readonly TextBlock _offlineWarningText;
    private readonly TextBlock _emptyStateText;
    private readonly TreeView _treeView;

    private bool _isUpdatingChecks;
    private bool _skipClosePrompt;
    private int _lastTreeSignature = -1;

    public string ServerId => _serverId;
    public List<string> ResultDisabledTools { get; private set; } = new();

    public ServerToolsWindow(
        McpInfrastructureService infrastructure,
        string serverId,
        string serverName,
        IEnumerable<string>? initialDisabledTools)
    {
        _infrastructure = infrastructure;
        _serverId = serverId;
        _serverName = serverName;
        _initialDisabledSet = new HashSet<string>(
            (initialDisabledTools ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.Ordinal);
        _lastCommittedSnapshot = _initialDisabledSet.OrderBy(s => s, StringComparer.Ordinal).ToList();

        Title = I18n.Tr("Инструменты: ", "Tools: ") + serverName;
        Width = 700;
        Height = 650;
        MinWidth = 540;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 29));
        Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239));

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = new Border
        {
            Background = Application.Current.TryFindResource("TopBarBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(32, 35, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(20, 22, 25)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.Children.Add(new TextBlock
        {
            Text = Title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
            FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0)
        });
        var closeBtn = new Button
        {
            Style = Application.Current.TryFindResource("ChromeCloseButtonStyle") as Style, Height = 48,
            Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"), Stroke = new SolidColorBrush(Color.FromRgb(140, 148, 158)), StrokeThickness = 1.3 }
        };
        WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 1);
        topGrid.Children.Add(closeBtn);
        topBar.Child = topGrid;
        rootGrid.Children.Add(topBar);

        var contentGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(contentGrid, 1);
        rootGrid.Children.Add(contentGrid);

        var headerCard = CreateCardBorder();
        var headerStack = new StackPanel();
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _masterCheckBox = new CheckBox
        {
            IsThreeState = true, IsChecked = true,
            Content = I18n.Tr("Все инструменты включены", "All tools enabled"),
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        _masterCheckBox.Click += MasterCheckBox_Click;
        topRow.Children.Add(_masterCheckBox);

        var rightControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _spinnerRotate = new RotateTransform(0, 8, 8);
        var spinnerIcon = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16, Data = Geometry.Parse("M 8,2 A 6,6 0 1 1 2,8"),
            Stroke = ToolBrush, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = _spinnerRotate, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };
        _loadingPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };
        _loadingPanel.Children.Add(spinnerIcon);
        _loadingPanel.Children.Add(new TextBlock { Text = I18n.Tr("Загрузка инструментов… (пока недоступны)", "Loading tools… (unavailable yet)"), FontSize = 11, Foreground = ToolBrush, VerticalAlignment = VerticalAlignment.Center });

        _undoButton = new Button
        {
            Content = I18n.Tr("↶ Отменить (Ctrl+Z)", "↶ Undo (Ctrl+Z)"),
            Padding = new Thickness(10, 4, 10, 4), FontSize = 11, IsEnabled = false, Margin = new Thickness(0, 0, 12, 0)
        };
        _undoButton.Click += (_, _) => Undo();

        _statsText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Foreground = ToolBrush, VerticalAlignment = VerticalAlignment.Center };
        rightControls.Children.Add(_loadingPanel);
        rightControls.Children.Add(_undoButton);
        rightControls.Children.Add(_statsText);
        Grid.SetColumn(rightControls, 1);
        topRow.Children.Add(rightControls);
        headerStack.Children.Add(topRow);
        headerCard.Child = headerStack;
        contentGrid.Children.Add(headerCard);

        var treeCard = CreateCardBorder();
        treeCard.Padding = new Thickness(8);
        var treeHost = new Grid();

        _treeView = new TreeView
        {
            ItemsSource = _rootNodes, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brushes.White, FocusVisualStyle = null, ItemTemplate = CreateNodeTemplate()
        };
        VirtualizingPanel.SetIsVirtualizing(_treeView, true);
        VirtualizingPanel.SetVirtualizationMode(_treeView, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_treeView, true);

        _treeView.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
        _treeView.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brushes.Transparent;
        _treeView.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        _treeView.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.White;
        _treeView.Resources[SystemColors.ControlTextBrushKey] = Brushes.White;

        var itemStyle = new Style(typeof(TreeViewItem));
        itemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(ToolNodeVm.IsExpanded)) { Mode = BindingMode.TwoWay }));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 3, 2, 3)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        itemStyle.Setters.Add(new EventSetter(TreeViewItem.SelectedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TreeViewItem tvi) tvi.IsSelected = false;
        })));
        _treeView.ItemContainerStyle = itemStyle;

        _emptyStateText = new TextBlock
        {
            Text = I18n.Tr("Загрузка дерева инструментов…", "Loading tool tree…"),
            Foreground = MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20), TextAlignment = TextAlignment.Center
        };

        treeHost.Children.Add(_treeView);
        treeHost.Children.Add(_emptyStateText);
        treeCard.Child = treeHost;
        Grid.SetRow(treeCard, 2);
        contentGrid.Children.Add(treeCard);

        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _offlineWarningText = new TextBlock
        {
            Text = I18n.Tr("Все инструменты отключены — сервер перейдёт в статус ОФЛАЙН (нейросеть увидит только список имён).", "All tools are disabled — server will become OFFLINE (the AI will only see tool names)."),
            Foreground = WarningBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), Visibility = Visibility.Collapsed
        };
        bottomGrid.Children.Add(_offlineWarningText);

        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _okButton = new Button { Content = I18n.Tr("ОК", "OK"), MinWidth = 96, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style pStyle) _okButton.Style = pStyle;
        _okButton.Click += OkButton_Click;

        var cancelButton = new Button { Content = I18n.Tr("Отмена", "Cancel"), MinWidth = 96, Padding = new Thickness(14, 8, 14, 8) };
        cancelButton.Click += CancelButton_Click;

        buttonsPanel.Children.Add(_okButton);
        buttonsPanel.Children.Add(cancelButton);
        Grid.SetColumn(buttonsPanel, 1);
        bottomGrid.Children.Add(buttonsPanel);

        Grid.SetRow(bottomGrid, 4);
        contentGrid.Children.Add(bottomGrid);

        var outerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(17, 19, 21)),
            BorderThickness = new Thickness(1), Child = rootGrid
        };
        WindowChromeHelper.Configure(this, outerBorder, rootGrid, 5.0);
        Content = outerBorder;

        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += Window_Closing;
        Closed += (_, _) => _windowCts.Cancel();
        Loaded += async (_, _) => await LoadAndWatchTreeAsync();
    }

    private HierarchicalDataTemplate CreateNodeTemplate()
    {
        var template = new HierarchicalDataTemplate(typeof(ToolNodeVm)) { ItemsSource = new Binding(nameof(ToolNodeVm.Children)) };

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        panel.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ToolNodeVm.NodeToolTip)));

        var checkBox = new FrameworkElementFactory(typeof(CheckBox));
        checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        checkBox.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        checkBox.SetValue(FrameworkElement.FocusableProperty, false);
        checkBox.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ToolNodeVm.IsChecked)) { Mode = BindingMode.TwoWay });
        checkBox.SetBinding(CheckBox.IsThreeStateProperty, new Binding(nameof(ToolNodeVm.IsCategory)));
        checkBox.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(NodeCheckBox_Click));

        var nameText = new FrameworkElementFactory(typeof(TextBlock));
        nameText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        nameText.SetBinding(TextBlock.TextProperty, new Binding(nameof(ToolNodeVm.Title)));
        nameText.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ToolNodeVm.TitleBrush)));
        nameText.SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(ToolNodeVm.TitleWeight)));
        nameText.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(ToolNodeVm.TitleFont)));

        var countText = new FrameworkElementFactory(typeof(TextBlock));
        countText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        countText.SetValue(TextBlock.ForegroundProperty, MutedBrush);
        countText.SetValue(TextBlock.FontSizeProperty, 11.0);
        countText.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        countText.SetBinding(TextBlock.TextProperty, new Binding(nameof(ToolNodeVm.CountBadge)));

        panel.AppendChild(checkBox);
        panel.AppendChild(nameText);
        panel.AppendChild(countText);
        template.VisualTree = panel;
        return template;
    }

    private void SetLoadingState(bool isLoading)
    {
        _okButton.IsEnabled = !isLoading && _rootNodes.Count > 0;
        _treeView.Visibility = isLoading || _rootNodes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (isLoading)
        {
            _masterCheckBox.IsEnabled = false;
            if (_loadingPanel.Visibility != Visibility.Visible)
            {
                _loadingPanel.Visibility = Visibility.Visible;
                var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
                _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
        }
        else
        {
            _masterCheckBox.IsEnabled = _rootNodes.Count > 0;
            if (_loadingPanel.Visibility != Visibility.Collapsed)
            {
                _spinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                _loadingPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static bool IsTreeLoading(McpServerToolTree? tree)
    {
        if (tree == null) return false;
        if (string.Equals(tree.ToolsLoadState, "error", StringComparison.OrdinalIgnoreCase)) return false;
        return tree.IsDiscovering ||
            string.Equals(tree.ToolsLoadState, "loading", StringComparison.OrdinalIgnoreCase) ||
            (tree.HasMetaPattern && !tree.HasDiscoveredMeta && string.Equals(tree.RawHealth, "online", StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadAndWatchTreeAsync()
    {
        SetLoadingState(true);
        try
        {
            var tree = await _infrastructure.GetServerToolsTreeAsync(_serverId);
            if (_windowCts.IsCancellationRequested) return;
            var needsPolling = IsTreeLoading(tree);
            if (!needsPolling)
                PopulateTree(tree, preserveLocalChanges: false);
            else
            {
                _emptyStateText.Text = I18n.Tr(
                    "Загрузка дерева инструментов…\nИнструменты станут доступны после завершения загрузки.",
                    "Loading tool tree…\nTools will become available once loading completes.");
                _emptyStateText.Visibility = Visibility.Visible;
            }

            while (needsPolling && !_windowCts.IsCancellationRequested)
            {
                SetLoadingState(true);
                await Task.Delay(750, _windowCts.Token);
                if (_windowCts.IsCancellationRequested) break;
                tree = await _infrastructure.GetServerToolsTreeAsync(_serverId);
                if (_windowCts.IsCancellationRequested || tree == null) break;
                needsPolling = IsTreeLoading(tree);
                if (!needsPolling)
                    PopulateTree(tree, preserveLocalChanges: _rootNodes.Count > 0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_rootNodes.Count == 0)
            {
                _emptyStateText.Text = I18n.Tr("Не удалось загрузить инструменты: ", "Failed to load tools: ") + ex.Message;
                _emptyStateText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (!_windowCts.IsCancellationRequested) SetLoadingState(false);
        }
    }

    private static int ComputeSignature(McpServerToolTree tree)
    {
        var hash = tree.Nodes.Count * 397 ^ tree.TotalLeafCount * 17 ^ (tree.HasDiscoveredMeta ? 1 : 0);
        foreach (var n in tree.Nodes)
            hash = unchecked(hash * 31 + n.Path.GetHashCode() + n.Children.Count * 13);
        return hash;
    }

    private void PopulateTree(McpServerToolTree? tree, bool preserveLocalChanges)
    {
        if (tree != null &&
            string.Equals(tree.ToolsLoadState, "error", StringComparison.OrdinalIgnoreCase) &&
            tree.HasMetaPattern && !tree.HasDiscoveredMeta)
        {
            _rootNodes.Clear();
            var errDetail = string.IsNullOrWhiteSpace(tree.ToolsLoadError) ? string.Empty : ("\n" + tree.ToolsLoadError);
            _emptyStateText.Text = I18n.Tr(
                "Не удалось полностью загрузить дерево инструментов «" + _serverName + "»." + errDetail,
                "Failed to load the complete tool tree for '" + _serverName + "'." + errDetail);
            _emptyStateText.Visibility = Visibility.Visible;
            _treeView.Visibility = Visibility.Collapsed;
            _masterCheckBox.IsEnabled = false;
            _okButton.IsEnabled = false;
            UpdateSummary();
            return;
        }

        if (tree == null || tree.Nodes.Count == 0)
        {
            _rootNodes.Clear();
            _emptyStateText.Text = I18n.Tr(
                "Инструменты для «" + _serverName + "» ещё не получены.\nПодключите сервер хотя бы один раз — дерево сохранится в локальный кэш.",
                "No tools have been cached for '" + _serverName + "' yet.\nConnect the server at least once so its tool tree is cached locally.");
            _emptyStateText.Visibility = Visibility.Visible;
            _treeView.Visibility = Visibility.Collapsed;
            _masterCheckBox.IsEnabled = false;
            _okButton.IsEnabled = false;
            UpdateSummary();
            return;
        }

        var signature = ComputeSignature(tree);
        if (preserveLocalChanges && signature == _lastTreeSignature && _rootNodes.Count > 0) return;
        _lastTreeSignature = signature;

        var activeDisabled = preserveLocalChanges && _rootNodes.Count > 0
            ? new HashSet<string>(CollectDisabledPaths(), StringComparer.Ordinal)
            : _initialDisabledSet;

        _rootNodes.Clear();
        _emptyStateText.Visibility = Visibility.Collapsed;
        _treeView.Visibility = Visibility.Visible;
        _masterCheckBox.IsEnabled = true;
        _okButton.IsEnabled = true;

        foreach (var node in tree.Nodes)
            _rootNodes.Add(BuildVm(node, null, 0, activeDisabled));

        _lastCommittedSnapshot = CollectDisabledPaths();
        UpdateSummary();
        _undoButton.IsEnabled = _undoStack.Count > 0;
    }

    private ToolNodeVm BuildVm(McpToolTreeNode dto, ToolNodeVm? parent, int level, HashSet<string> disabledSet)
    {
        var isCategory = string.Equals(dto.Kind, "category", StringComparison.OrdinalIgnoreCase);
        var vm = new ToolNodeVm(dto.Path, dto.DisplayName, dto.Description, isCategory, level, parent);
        if (isCategory && dto.Children.Count > 0)
        {
            var branchDisabled = IsPathDisabledInSet(dto.Path, disabledSet);
            var childSet = branchDisabled ? new HashSet<string>(disabledSet) { "*" } : disabledSet;
            foreach (var child in dto.Children)
                vm.Children.Add(BuildVm(child, vm, level + 1, childSet));
            RecalculateNode(vm);
        }
        else
        {
            vm.IsChecked = !IsPathDisabledInSet(dto.Path, disabledSet);
        }
        return vm;
    }

    private static bool IsPathDisabledInSet(string path, HashSet<string> disabledSet)
    {
        if (disabledSet.Contains("*") || disabledSet.Contains(path)) return true;
        var idx = path.IndexOf('/');
        while (idx > 0)
        {
            if (disabledSet.Contains(path.Substring(0, idx))) return true;
            idx = path.IndexOf('/', idx + 1);
        }
        return false;
    }

    private void CommitUserEdit()
    {
        var current = CollectDisabledPaths();
        if (current.Count != _lastCommittedSnapshot.Count || current.Zip(_lastCommittedSnapshot, (a, b) => a != b).Any(x => x))
        {
            _undoStack.Push(new List<string>(_lastCommittedSnapshot));
            _redoStack.Clear();
            _lastCommittedSnapshot = current;
            _undoButton.IsEnabled = true;
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0 || _rootNodes.Count == 0) return;
        _redoStack.Push(new List<string>(_lastCommittedSnapshot));
        ApplyDisabledSnapshot(_undoStack.Pop());
        _lastCommittedSnapshot = CollectDisabledPaths();
        UpdateSummary();
        _undoButton.IsEnabled = _undoStack.Count > 0;
    }

    private void Redo()
    {
        if (_redoStack.Count == 0 || _rootNodes.Count == 0) return;
        _undoStack.Push(new List<string>(_lastCommittedSnapshot));
        ApplyDisabledSnapshot(_redoStack.Pop());
        _lastCommittedSnapshot = CollectDisabledPaths();
        UpdateSummary();
        _undoButton.IsEnabled = _undoStack.Count > 0;
    }

    public void ApplyExternalDisabledToolsUpdate(IEnumerable<string> nextDisabledTools)
    {
        var normalized = (nextDisabledTools ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        _initialDisabledSet.Clear();
        foreach (var path in normalized) _initialDisabledSet.Add(path);

        if (_rootNodes.Count == 0)
        {
            _lastCommittedSnapshot = normalized;
            return;
        }

        var current = CollectDisabledPaths();
        if (current.Count == normalized.Count && !current.Zip(normalized, (a, b) => a != b).Any(x => x))
            return;

        _undoStack.Push(new List<string>(current));
        _redoStack.Clear();
        ApplyDisabledSnapshot(normalized);
        _lastCommittedSnapshot = CollectDisabledPaths();
        UpdateSummary();
        _undoButton.IsEnabled = _undoStack.Count > 0;
    }

    private void ApplyDisabledSnapshot(IEnumerable<string> disabledPaths)
    {
        var set = new HashSet<string>(disabledPaths, StringComparer.Ordinal);
        _isUpdatingChecks = true;
        try
        {
            void Apply(ToolNodeVm node)
            {
                if (!node.IsCategory || node.Children.Count == 0) { node.IsChecked = !IsPathDisabledInSet(node.Path, set); return; }
                foreach (var child in node.Children) Apply(child);
                RecalculateNode(node);
            }
            foreach (var root in _rootNodes) Apply(root);
        }
        finally { _isUpdatingChecks = false; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.Z) { if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) Redo(); else Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo(); e.Handled = true; }
        }
    }

    private void MasterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_rootNodes.Count == 0) return;
        var target = _masterCheckBox.IsChecked == true;
        _isUpdatingChecks = true;
        try { foreach (var root in _rootNodes) SetSubtreeChecked(root, target); }
        finally { _isUpdatingChecks = false; }
        CommitUserEdit();
        UpdateSummary();
    }

    private void NodeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingChecks || sender is not CheckBox cb || cb.DataContext is not ToolNodeVm vm) return;
        var target = vm.IsChecked == true;
        _isUpdatingChecks = true;
        try
        {
            SetSubtreeChecked(vm, target);
            var parent = vm.Parent;
            while (parent != null) { RecalculateNode(parent); parent = parent.Parent; }
        }
        finally { _isUpdatingChecks = false; }
        CommitUserEdit();
        UpdateSummary();
    }

    private static void SetSubtreeChecked(ToolNodeVm node, bool state)
    {
        node.IsChecked = state;
        foreach (var child in node.Children) SetSubtreeChecked(child, state);
        if (node.Children.Count > 0) RecalculateNode(node);
    }

    private static void RecalculateNode(ToolNodeVm node)
    {
        if (node.Children.Count == 0) return;
        var allTrue = node.Children.All(c => c.IsChecked == true);
        var allFalse = node.Children.All(c => c.IsChecked == false);
        node.IsChecked = allTrue ? true : (allFalse ? false : null);
    }

    private void UpdateSummary()
    {
        var (enabled, total) = CountLeaves(_rootNodes);
        _statsText.Text = total > 0 ? (enabled + " / " + total) : "0";
        _offlineWarningText.Visibility = total > 0 && enabled == 0 ? Visibility.Visible : Visibility.Collapsed;

        _isUpdatingChecks = true;
        try { _masterCheckBox.IsChecked = total == 0 ? false : (enabled == total ? true : (enabled == 0 ? false : (bool?)null)); }
        finally { _isUpdatingChecks = false; }
    }

    private static (int Enabled, int Total) CountLeaves(IEnumerable<ToolNodeVm> nodes)
    {
        int enabled = 0, total = 0;
        foreach (var node in nodes)
        {
            if (!node.IsCategory) { total++; if (node.IsChecked == true) enabled++; }
            else if (node.Children.Count > 0) { var sub = CountLeaves(node.Children); enabled += sub.Enabled; total += sub.Total; }
        }
        return (enabled, total);
    }

    private List<string> CollectDisabledPaths()
    {
        var result = new List<string>();
        void Visit(ToolNodeVm node)
        {
            if (!node.IsCategory) { if (node.IsChecked != true) result.Add(node.Path); return; }
            if (node.Children.Count == 0) { if (node.IsChecked == false) result.Add(node.Path); return; }
            if (node.IsChecked == false) { result.Add(node.Path); return; }
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in _rootNodes) Visit(root);
        return result.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private bool HasUnsavedChanges()
    {
        if (_rootNodes.Count == 0) return false;
        var current = CollectDisabledPaths();
        return current.Count != _initialDisabledSet.Count || current.Any(p => !_initialDisabledSet.Contains(p));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResultDisabledTools = _rootNodes.Count > 0 ? CollectDisabledPaths() : _initialDisabledSet.OrderBy(s => s, StringComparer.Ordinal).ToList();
        _skipClosePrompt = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _skipClosePrompt = true;
        DialogResult = false;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_skipClosePrompt || !HasUnsavedChanges()) return;
        var choice = ShowSavePrompt();
        if (choice == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        _skipClosePrompt = true;
        if (choice == MessageBoxResult.Yes) { ResultDisabledTools = CollectDisabledPaths(); DialogResult = true; }
        else DialogResult = false;
    }

    private MessageBoxResult ShowSavePrompt()
    {
        var result = MessageBoxResult.Cancel;
        var dlg = new Window
        {
            Width = 420, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 29)), Foreground = Brushes.White, ShowInTaskbar = false
        };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = I18n.Tr("Сохранить изменения инструментов перед закрытием?", "Save tool changes before closing?"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveBtn = new Button { Content = I18n.Tr("Сохранить", "Save"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style pStyle) saveBtn.Style = pStyle;
        saveBtn.Click += (_, _) => { result = MessageBoxResult.Yes; dlg.Close(); };
        var discardBtn = new Button { Content = I18n.Tr("Не сохранять", "Don't save"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        discardBtn.Click += (_, _) => { result = MessageBoxResult.No; dlg.Close(); };
        var stayBtn = new Button { Content = I18n.Tr("Отмена", "Cancel"), Padding = new Thickness(14, 7, 14, 7) };
        stayBtn.Click += (_, _) => { result = MessageBoxResult.Cancel; dlg.Close(); };
        btns.Children.Add(saveBtn); btns.Children.Add(discardBtn); btns.Children.Add(stayBtn);
        root.Children.Add(btns);
        dlg.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 26, 29)), BorderBrush = new SolidColorBrush(Color.FromRgb(44, 229, 208)), BorderThickness = new Thickness(1), Child = root };
        dlg.ShowDialog();
        return result;
    }

    private static Border CreateCardBorder() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 40)), BorderBrush = new SolidColorBrush(Color.FromRgb(44, 48, 54)),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(14, 12, 14, 12)
    };

    private sealed class ToolNodeVm : INotifyPropertyChanged
    {
        private bool? _isChecked;
        private bool _isExpanded;

        public ToolNodeVm(string path, string title, string description, bool isCategory, int level, ToolNodeVm? parent)
        {
            Path = path; Title = title; Description = description; IsCategory = isCategory; Parent = parent;
            _isExpanded = isCategory && level == 0;
        }

        public string Path { get; }
        public string Title { get; }
        public string Description { get; }
        public bool IsCategory { get; }
        public ToolNodeVm? Parent { get; }
        public ObservableCollection<ToolNodeVm> Children { get; } = new();

        public Brush TitleBrush => IsCategory ? CategoryBrush : ToolBrush;
        public FontWeight TitleWeight => IsCategory ? FontWeights.SemiBold : FontWeights.Medium;
        public FontFamily TitleFont => IsCategory ? new FontFamily("Segoe UI") : new FontFamily("Consolas, Segoe UI");
        public string? NodeToolTip => string.IsNullOrWhiteSpace(Description) ? null : Description;

        public string CountBadge
        {
            get
            {
                if (!IsCategory || Children.Count == 0) return string.Empty;
                var leaves = CountSubLeaves(this);
                return "(" + leaves.Enabled + "/" + leaves.Total + ")";
            }
        }

        private static (int Enabled, int Total) CountSubLeaves(ToolNodeVm node)
        {
            int e = 0, t = 0;
            foreach (var c in node.Children)
            {
                if (!c.IsCategory) { t++; if (c.IsChecked == true) e++; }
                else if (c.Children.Count > 0) { var sub = CountSubLeaves(c); e += sub.Enabled; t += sub.Total; }
            }
            return (e, t);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        }

        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountBadge)));
                Parent?.NotifyChildChanged();
            }
        }

        private void NotifyChildChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountBadge)));
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}