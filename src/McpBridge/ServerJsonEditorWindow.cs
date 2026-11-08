using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using McpBridge.Models;
using McpBridge.Services;
using WpfClipboard = System.Windows.Clipboard;

namespace McpBridge;

public sealed class ServerJsonEditorWindow : Window
{
    private static readonly Brush SyntaxBrush = new SolidColorBrush(Color.FromRgb(44, 229, 208));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(127, 135, 144));

    private readonly McpInfrastructureService _infrastructure;
    private readonly TextBox _jsonTextEditor;
    private readonly TextBlock _jsonSyntaxOverlay;
    private readonly ScrollViewer _jsonEditorScroll;
    private bool _settingEditorText;
    
    private readonly TextBox _step1Text;
    private readonly TextBox _step2Text;
    private readonly TextBox _step3Text;
    private readonly TextBox _step4Text;
    private readonly TextBox _summaryText;
    private readonly TextBox _diagBox;
    private readonly TextBox _verificationActivityText;
    private readonly ProgressBar _verificationProgressBar;
    private readonly Button _verifyButton;
    private readonly Button _verifyAndSaveButton;
    private readonly Button _saveButton;
    
    private readonly string? _editingId;
    private string? _trustedExternalOrigin;
    private readonly bool _enabled;
    private readonly bool _requiresStdio;
    private readonly bool _isFilesystemEditor;
    private bool _isVerifying;

    public List<CustomMcpServerEntry> ResultServers { get; } = new();
    public List<RootPathItemViewModel> ResultFilesystemRoots { get; } = new();

    public ServerJsonEditorWindow(
        McpInfrastructureService infrastructure,
        CustomMcpServerEntry? existing = null,
        bool requiresStdio = false,
        IEnumerable<RootPathItemViewModel>? filesystemRoots = null)
    {
        _infrastructure = infrastructure;
        _editingId = existing?.Id;
        _trustedExternalOrigin = existing?.TrustedExternalOrigin;
        _enabled = existing?.Enabled ?? true;
        _requiresStdio = requiresStdio;
        _isFilesystemEditor = filesystemRoots != null;

        var baseEntry = existing ?? new CustomMcpServerEntry();
        var initialJson = _isFilesystemEditor
            ? BuildFilesystemEditorJson(baseEntry, filesystemRoots!)
            : FormatJsonForEditor(existing != null
                ? McpServerConfigJson.Serialize(existing)
                : McpServerConfigJson.CreateDefault());

        Title = existing == null
            ? I18n.Tr("Добавить MCP-сервер", "Add MCP Server")
            : I18n.Tr("Настройка MCP-сервера: ", "Configure MCP Server: ") + existing.Name;

        Width = 820;
        Height = 820;
        MinWidth = 680;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 29));
        Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239));

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;

        var chrome = new WindowChrome
        {
            CaptionHeight = 52,
            ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, chrome);

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = new Border
        {
            Background = Application.Current.TryFindResource("TopBarBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(22, 25, 29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(20, 22, 25)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var topBarGrid = new Grid();
        topBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0)
        };
        Grid.SetColumn(titleText, 0);
        topBarGrid.Children.Add(titleText);

        var windowButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(windowButtonsPanel, 1);

        var minButton = new Button { 
            Style = Application.Current.TryFindResource("ChromeButtonStyle") as Style,
            Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 10,0"), Stroke = new SolidColorBrush(Color.FromRgb(140, 148, 158)), StrokeThickness = 1.3 }
        };
        WindowChrome.SetIsHitTestVisibleInChrome(minButton, true);
        minButton.Click += (s, e) => WindowState = WindowState.Minimized;

        var maxButton = new Button { 
            Style = Application.Current.TryFindResource("ChromeButtonStyle") as Style,
            Content = new System.Windows.Shapes.Rectangle { Width = 9, Height = 9, Stroke = new SolidColorBrush(Color.FromRgb(140, 148, 158)), StrokeThickness = 1.2 }
        };
        WindowChrome.SetIsHitTestVisibleInChrome(maxButton, true);
        maxButton.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        var closeButton = new Button { 
            Style = Application.Current.TryFindResource("ChromeCloseButtonStyle") as Style,
            Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"), Stroke = new SolidColorBrush(Color.FromRgb(140, 148, 158)), StrokeThickness = 1.3 }
        };
        WindowChrome.SetIsHitTestVisibleInChrome(closeButton, true);
        closeButton.Click += (s, e) => Close();

        windowButtonsPanel.Children.Add(minButton);
        windowButtonsPanel.Children.Add(maxButton);
        windowButtonsPanel.Children.Add(closeButton);

        topBarGrid.Children.Add(windowButtonsPanel);
        topBar.Child = topBarGrid;

        rootGrid.Children.Add(topBar);

        var outerGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        Grid.SetRow(outerGrid, 1);
        rootGrid.Children.Add(outerGrid);
        
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var rootScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var mainStack = new StackPanel();

        var introCard = CreateCardBorder();
        introCard.Margin = new Thickness(0, 0, 0, 14);

        var introGrid = new Grid();
        introGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        introGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var introText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        introText.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Конфигурация MCP", "MCP Configuration"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        
        var introDescription = existing == null
            ? I18n.Tr(
                "Свободное редактирование JSON. Вы можете менять любые параметры сервера. Валидация выполняется только при проверке или сохранении.",
                "Free JSON editing. You can change any server parameters. Validation is performed only during verification or saving.")
            : I18n.Tr(
                "Свободное редактирование JSON. Вы можете менять любые параметры сервера. Валидация выполняется только при проверке или сохранении.",
                "Free JSON editing. You can change any server parameters. Validation is performed only during verification or saving.");

        if (_requiresStdio)
            introDescription += I18n.Tr(
                " Все разрешённые папки и их состояние показаны в allowedRoots; здесь редактируйте состав и enabled. args отражает активные пути и дополнительные аргументы. При сохранении пути синхронизируются с Settings без дублирования.",
                " All allowed folders and their states are shown in allowedRoots; edit the folder list and enabled flags there. args mirrors active paths and extra arguments. Saving synchronizes paths with Settings without duplicating runtime arguments.");
        else
            introDescription += I18n.Tr(
                " Локальные HTTP MCP подключаются напрямую. Удалённые адреса требуют HTTPS и отдельного подтверждения доверия к домену и порту.",
                " Local HTTP MCP endpoints connect directly. Remote endpoints require HTTPS and a separate trust confirmation for the host and port.");

        introText.Children.Add(new TextBlock
        {
            Text = introDescription,
            FontSize = 11,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });

        var pasteButton = new Button
        {
            Content = I18n.Tr("📋 Вставить из буфера", "📋 Paste from Clipboard"),
            Padding = new Thickness(12, 8, 12, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        pasteButton.Click += PasteFromClipboard_Click;
        pasteButton.Visibility = existing == null ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(introText, 0);
        Grid.SetColumn(pasteButton, 1);
        introGrid.Children.Add(introText);
        introGrid.Children.Add(pasteButton);
        introCard.Child = introGrid;
        mainStack.Children.Add(introCard);

        var jsonCard = CreateCardBorder();
        jsonCard.Margin = new Thickness(0, 0, 0, 14);

        var jsonStack = new StackPanel();
        jsonStack.Children.Add(CreateLabel(I18n.Tr("ПОЛНАЯ КОНФИГУРАЦИЯ (JSON)", "FULL CONFIGURATION (JSON)")));

        _jsonEditorScroll = new ScrollViewer
        {
            Height = 140,
            MinHeight = 120,
            MaxHeight = 360,
            Margin = new Thickness(0, 6, 0, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(25, 28, 32))
        };

        var editorLayer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };

        _jsonSyntaxOverlay = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239)),
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(12, 10, 12, 10),
            IsHitTestVisible = false
        };

        _jsonTextEditor = new TextBox
        {
            Text = initialJson,
            Height = 140,
            MinHeight = 120,
            Width = 700,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = Brushes.Transparent,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10),
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(55, 91, 95))
        };
        SpellCheck.SetIsEnabled(_jsonTextEditor, false);
        _jsonTextEditor.TextChanged += JsonTextEditor_TextChanged;
        _jsonTextEditor.ContextMenu = null;

        editorLayer.Children.Add(_jsonSyntaxOverlay);
        editorLayer.Children.Add(_jsonTextEditor);
        _jsonEditorScroll.Content = editorLayer;
        jsonStack.Children.Add(_jsonEditorScroll);

        jsonStack.Children.Add(new TextBlock
        {
            Text = I18n.Tr(
                "Текст должен являться валидным JSON. Обязательные ключи (name, command) проверяются при сохранении.",
                "Text must be valid JSON. Required keys (name, command) are checked upon saving."),
            FontSize = 10.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2)
        });

        jsonCard.Child = jsonStack;
        mainStack.Children.Add(jsonCard);

        var verifyCard = CreateCardBorder();
        var verifyStack = new StackPanel();

        verifyStack.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Конвейер верификации сервера (4 этапа)", "Server Verification Pipeline (4 Stages)"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _verificationActivityText = new TextBox
        {
            Text = I18n.Tr("Ожидание запуска проверки.", "Waiting to start verification."),
            FontSize = 10.5,
            Foreground = SyntaxBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 22,
            MaxHeight = 44,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(55, 91, 95)),
            Margin = new Thickness(0, 0, 0, 6)
        };
        _verificationProgressBar = new ProgressBar
        {
            Height = 5,
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromRgb(22, 25, 29)),
            Foreground = SyntaxBrush,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 8)
        };
        verifyStack.Children.Add(_verificationActivityText);
        verifyStack.Children.Add(_verificationProgressBar);

        _step1Text = CreateStepLine(I18n.Tr("1. Pre-flight (проверка путей и параметров): ожидание", "1. Pre-flight (paths & parameters): waiting"));
        _step2Text = CreateStepLine(I18n.Tr("2. Транспорт и порт: ожидание", "2. Transport & port: waiting"));
        _step3Text = CreateStepLine(I18n.Tr("3. Рукопожатие протокола (MCP initialize): ожидание", "3. Protocol handshake (MCP initialize): waiting"));
        _step4Text = CreateStepLine(I18n.Tr("4. Проверка инструментов (tools/list): ожидание", "4. Tools audit (tools/list): waiting"));

        verifyStack.Children.Add(_step1Text);
        verifyStack.Children.Add(_step2Text);
        verifyStack.Children.Add(_step3Text);
        verifyStack.Children.Add(_step4Text);

        _summaryText = new TextBox
        {
            Text = I18n.Tr("Проверьте конфигурацию или сохраните её без проверки.", "Verify the configuration or save it without verification."),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = SyntaxBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 28,
            MaxHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 6),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(55, 91, 95))
        };
        verifyStack.Children.Add(_summaryText);

        verifyStack.Children.Add(CreateLabel(I18n.Tr("ВЫВОД ПРОЦЕССА / ДИАГНОСТИКА", "PROCESS OUTPUT / DIAGNOSTICS")));
        _diagBox = new TextBox
        {
            Height = 112,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(214, 220, 226)),
            Background = new SolidColorBrush(Color.FromRgb(22, 25, 29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 54, 61)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(55, 91, 95)),
            Visibility = Visibility.Collapsed
        };
        verifyStack.Children.Add(_diagBox);

        verifyCard.Child = verifyStack;
        mainStack.Children.Add(verifyCard);

        rootScroll.Content = mainStack;
        Grid.SetRow(rootScroll, 0);
        outerGrid.Children.Add(rootScroll);

        var buttonGrid = new Grid();
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _verifyButton = new Button
        {
            Content = I18n.Tr("🔍 Проверить", "🔍 Verify"),
            Padding = new Thickness(14, 8, 14, 8)
        };
        _verifyButton.Click += VerifyOnly_Click;

        _saveButton = new Button
        {
            Content = I18n.Tr("Сохранить без проверки", "Save Without Check"),
            Padding = new Thickness(12, 8, 12, 8)
        };
        _saveButton.Click += SaveWithoutVerify_Click;

        _verifyAndSaveButton = new Button
        {
            Content = existing == null
                ? I18n.Tr("✓ Проверить и добавить", "✓ Verify & Add")
                : I18n.Tr("✓ Проверить и сохранить", "✓ Verify & Save"),
            Padding = new Thickness(16, 8, 16, 8)
        };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
            _verifyAndSaveButton.Style = primaryStyle;
        _verifyAndSaveButton.Click += VerifyAndSave_Click;

        var cancelButton = new Button
        {
            Content = I18n.Tr("Отмена", "Cancel"),
            Padding = new Thickness(14, 8, 14, 8)
        };
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        Grid.SetColumn(_verifyButton, 0);
        Grid.SetColumn(_saveButton, 2);
        Grid.SetColumn(_verifyAndSaveButton, 4);
        Grid.SetColumn(cancelButton, 6);

        buttonGrid.Children.Add(_verifyButton);
        buttonGrid.Children.Add(_saveButton);
        buttonGrid.Children.Add(_verifyAndSaveButton);
        buttonGrid.Children.Add(cancelButton);

        Grid.SetRow(buttonGrid, 2);
        outerGrid.Children.Add(buttonGrid);

        Content = rootGrid;
        InitializeEditorDocument(initialJson);
    }

    private void InitializeEditorDocument(string json)
    {
        json = (json ?? string.Empty).Replace("\t", "  ");
        _settingEditorText = true;
        _jsonTextEditor.Text = json;
        _settingEditorText = false;

        RefreshEditorPresentation(_jsonTextEditor.Text);
        _jsonTextEditor.SelectionStart = 0;
        _jsonTextEditor.SelectionLength = 0;
    }

    private static string FormatJsonForEditor(string json)
    {
        try
        {
            if (McpServerConfigParser.DeserializeJson(json) is not IDictionary<string, object?> root)
                return json;

            var builder = new StringBuilder();
            AppendFormattedObject(builder, root, 0);
            return builder.ToString();
        }
        catch
        {
            return json;
        }
    }

    private static string BuildFilesystemEditorJson(CustomMcpServerEntry entry, IEnumerable<RootPathItemViewModel> configuredRoots)
    {
        var roots = new List<RootPathItemViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in configuredRoots)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) continue;
            try
            {
                var path = SettingsStore.NormalizePath(item.Path);
                if (!Directory.Exists(path) || !seen.Add(path)) continue;
                roots.Add(new RootPathItemViewModel { Path = path, IsEnabled = item.IsEnabled });
            }
            catch
            {
                // Invalid or unavailable roots are not included in the editor snapshot.
            }
        }

        if (McpServerConfigParser.DeserializeJson(McpServerConfigJson.Serialize(entry)) is not IDictionary<string, object?> properties)
            throw new FormatException("Не удалось подготовить JSON Filesystem MCP.");

        var rootPaths = new HashSet<string>(roots.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var args = new List<string>();
        foreach (var argument in entry.Args ?? new List<string>())
        {
            if (TryNormalizeExistingDirectory(argument, out var normalized) && rootPaths.Contains(normalized))
                continue;
            args.Add(argument);
        }

        args.AddRange(roots.Where(item => item.IsEnabled).Select(item => item.Path));
        properties["args"] = args;
        properties["allowedRoots"] = roots.Select(item => (object)new Dictionary<string, object?>
        {
            ["path"] = item.Path,
            ["enabled"] = item.IsEnabled
        }).ToList();

        return FormatJsonForEditor(McpServerConfigParser.SerializeJson(properties));
    }

    private static bool TryNormalizeExistingDirectory(string? value, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            var normalized = SettingsStore.NormalizePath(value!);
            if (!Directory.Exists(normalized)) return false;
            normalizedPath = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendFormattedObject(StringBuilder builder, IDictionary<string, object?> value, int depth)
    {
        builder.Append('{');
        if (value.Count == 0)
        {
            builder.Append('}');
            return;
        }

        builder.Append('\n');
        var entries = value.ToList();
        for (var index = 0; index < entries.Count; index++)
        {
            AppendIndent(builder, depth + 1);
            builder.Append(McpServerConfigParser.SerializeJson(entries[index].Key));
            builder.Append(": ");
            AppendFormattedValue(builder, entries[index].Value, depth + 1);
            if (index < entries.Count - 1)
                builder.Append(',');
            builder.Append('\n');
        }

        AppendIndent(builder, depth);
        builder.Append('}');
    }

    private static void AppendFormattedArray(StringBuilder builder, IList value, int depth)
    {
        builder.Append('[');
        if (value.Count == 0)
        {
            builder.Append(']');
            return;
        }

        builder.Append('\n');
        for (var index = 0; index < value.Count; index++)
        {
            AppendIndent(builder, depth + 1);
            AppendFormattedValue(builder, value[index], depth + 1);
            if (index < value.Count - 1)
                builder.Append(',');
            builder.Append('\n');
        }

        AppendIndent(builder, depth);
        builder.Append(']');
    }

    private static void AppendFormattedValue(StringBuilder builder, object? value, int depth)
    {
        if (value is IDictionary<string, object?> objectValue)
        {
            AppendFormattedObject(builder, objectValue, depth);
            return;
        }

        if (value is IList arrayValue && value is not string)
        {
            AppendFormattedArray(builder, arrayValue, depth);
            return;
        }

        builder.Append(McpServerConfigParser.SerializeJson(value));
    }

    private static void AppendIndent(StringBuilder builder, int depth)
    {
        if (depth > 0)
            builder.Append(' ', depth * 2);
    }

    private void JsonTextEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingEditorText)
            return;

        RefreshEditorPresentation(_jsonTextEditor.Text);
    }

    private void RefreshEditorPresentation(string text)
    {
        _jsonSyntaxOverlay.Inlines.Clear();

        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '"')
            {
                var stringStart = index;
                var stringEnd = FindJsonStringEnd(text, stringStart);
                if (stringEnd < 0)
                {
                    AddOverlayRun("\"", SyntaxBrush);
                    AddOverlayRun(text.Substring(stringStart + 1), Brushes.White);
                    break;
                }

                var afterString = stringEnd + 1;
                while (afterString < text.Length && char.IsWhiteSpace(text[afterString]))
                    afterString++;

                if (afterString < text.Length && text[afterString] == ':')
                {
                    AddOverlayRun(text.Substring(stringStart, stringEnd - stringStart + 1), SyntaxBrush);
                }
                else
                {
                    AddOverlayRun("\"", SyntaxBrush);
                    if (stringEnd > stringStart + 1)
                        AddOverlayRun(text.Substring(stringStart + 1, stringEnd - stringStart - 1), Brushes.White);
                    AddOverlayRun("\"", SyntaxBrush);
                }

                index = stringEnd + 1;
                continue;
            }

            if ("{}[]:,".IndexOf(text[index]) >= 0)
            {
                AddOverlayRun(text[index].ToString(), SyntaxBrush);
                index++;
                continue;
            }

            var segmentStart = index;
            while (index < text.Length &&
                   text[index] != '"' &&
                   "{}[]:,".IndexOf(text[index]) < 0)
            {
                index++;
            }

            AddOverlayRun(text.Substring(segmentStart, index - segmentStart), Brushes.White);
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var longestLine = lines.Length == 0 ? 0 : lines.Max(line => line.Length);
        var width = Math.Max(700, longestLine * 8.1 + 28);
        var contentHeight = Math.Max(120, lines.Length * 18.0 + 24);
        var viewportHeight = Math.Min(360, contentHeight);

        _jsonTextEditor.Width = width;
        _jsonTextEditor.Height = contentHeight;
        _jsonSyntaxOverlay.Width = width - 20;
        _jsonSyntaxOverlay.Height = contentHeight - 20;
        _jsonEditorScroll.Height = viewportHeight;
    }

    private void AddOverlayRun(string text, Brush foreground)
    {
        if (text.Length == 0)
            return;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length > 0)
                _jsonSyntaxOverlay.Inlines.Add(new System.Windows.Documents.Run(lines[index]) { Foreground = foreground });

            if (index < lines.Length - 1)
                _jsonSyntaxOverlay.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
    }

    private static int FindJsonStringEnd(string text, int start)
    {
        var escaped = false;
        for (var index = start + 1; index < text.Length; index++)
        {
            var ch = text[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
                return index;
        }

        return -1;
    }

    private void PasteFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (WpfClipboard.ContainsText())
            {
                var text = WpfClipboard.GetText();
                if (text != null)
                {
                    InitializeEditorDocument(text);
                    _summaryText.Text = I18n.Tr(
                        "Текст вставлен из буфера обмена. Проверьте значения и сохраните сервер.",
                        "Text pasted from clipboard. Review the values and save the server.");
                }
            }
        }
        catch (Exception exception)
        {
            ShowCustomDialog(exception.Message, I18n.Tr("Буфер обмена", "Clipboard"));
        }
    }

    private void VerifyOnly_Click(object sender, RoutedEventArgs e)
    {
        _ = RunVerificationAsync(false);
    }

    private void VerifyAndSave_Click(object sender, RoutedEventArgs e)
    {
        _ = RunVerificationAsync(true);
    }

    private void SaveWithoutVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_isVerifying) return;

        try
        {
            var candidate = BuildCandidate();
            if (!ConfirmRemoteEndpointTrust(candidate)) return;
            ResultServers.Clear();
            ResultServers.Add(candidate);
            DialogResult = true;
            Close();
        }
        catch (FormatException exception)
        {
            ShowCustomDialog(exception.Message, I18n.Tr("Некорректная конфигурация", "Invalid Configuration"));
        }
    }

    private CustomMcpServerEntry BuildCandidate()
    {
        var json = _jsonTextEditor.Text;
        McpServerConfigJson.ValidateJsonDocument(json);

        if (McpServerConfigParser.DeserializeJson(json) is not IDictionary<string, object?> root)
            throw new FormatException("Корнем конфигурации должен быть JSON-объект.");

        ValidateOptionalStringProperty(root, "name");
        ValidateOptionalStringProperty(root, "command");
        ValidateOptionalStringProperty(root, "url");
        ValidateOptionalStringProperty(root, "serverUrl");
        ValidateOptionalStringProperty(root, "endpoint");
        ValidateOptionalStringProperty(root, "cwd");
        ValidateOptionalStringProperty(root, "workingDirectory");
        ValidateOptionalStringProperty(root, "transport");
        ValidateOptionalStringProperty(root, "type");

        if (root.TryGetValue("args", out var rawArgs))
        {
            if (rawArgs is not IList arguments || rawArgs is string)
                throw new FormatException(I18n.Tr("Поле args должно быть массивом строк.", "The args field must be an array of strings."));

            if (arguments.Cast<object?>().Any(argument => argument is not string))
                throw new FormatException(I18n.Tr("Каждый элемент args должен быть строкой в кавычках.", "Every args item must be a quoted string."));
        }

        var candidate = McpServerConfigJson.ParseSingleServer(json, _editingId, _enabled);
        if (_requiresStdio && !string.Equals(candidate.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(I18n.Tr(
                "Встроенный Filesystem MCP использует только STDIO-транспорт.",
                "The built-in Filesystem MCP only supports STDIO transport."));

        if (_isFilesystemEditor)
            ApplyFilesystemEditorMetadata(root, candidate);

        candidate.TrustedExternalOrigin = string.Equals(candidate.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? _trustedExternalOrigin
            : null;
        return candidate;
    }

    private void ApplyFilesystemEditorMetadata(
        IDictionary<string, object?> root,
        CustomMcpServerEntry candidate)
    {
        var configuredRoots = new List<RootPathItemViewModel>();
        var rootsByPath = new Dictionary<string, RootPathItemViewModel>(StringComparer.OrdinalIgnoreCase);
        var hasAllowedRoots = root.TryGetValue("allowedRoots", out var rawRoots);

        if (hasAllowedRoots)
        {
            if (rawRoots is not IList roots || rawRoots is string)
                throw new FormatException(I18n.Tr(
                    "Поле allowedRoots должно быть массивом объектов с полями path и enabled.",
                    "The allowedRoots field must be an array of objects with path and enabled properties."));

            foreach (var item in roots)
            {
                if (item is not IDictionary<string, object?> rootItem ||
                    !rootItem.TryGetValue("path", out var rawPath) ||
                    rawPath is not string path ||
                    string.IsNullOrWhiteSpace(path))
                {
                    throw new FormatException(I18n.Tr(
                        "Каждый элемент allowedRoots должен содержать строковое поле path.",
                        "Each allowedRoots item must contain a string path property."));
                }

                var enabled = true;
                if (rootItem.TryGetValue("enabled", out var rawEnabled))
                {
                    if (rawEnabled is not bool enabledValue)
                        throw new FormatException(I18n.Tr(
                            "Поле enabled в allowedRoots должно быть логическим значением.",
                            "The enabled property in allowedRoots must be a boolean."));
                    enabled = enabledValue;
                }

                string normalizedPath;
                try
                {
                    normalizedPath = SettingsStore.NormalizePath(path);
                }
                catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
                {
                    throw new FormatException(I18n.Tr("Некорректный путь Filesystem MCP: ", "Invalid Filesystem MCP path: ") + path);
                }

                if (!Directory.Exists(normalizedPath))
                    throw new FormatException(I18n.Tr(
                        "Папка из allowedRoots не существует: ",
                        "The folder listed in allowedRoots does not exist: ") + normalizedPath);

                if (!rootsByPath.ContainsKey(normalizedPath))
                {
                    var rootEntry = new RootPathItemViewModel { Path = normalizedPath, IsEnabled = enabled };
                    rootsByPath.Add(normalizedPath, rootEntry);
                    configuredRoots.Add(rootEntry);
                }
            }
        }

        var additionalArguments = new List<string>();

        foreach (var argument in candidate.Args ?? new List<string>())
        {
            if (TryNormalizeExistingDirectory(argument, out var normalizedPath))
            {
                // allowedRoots is the authoritative folder list when present.
                // args displays active roots, but removing a path from allowedRoots must really revoke it.
                if (!hasAllowedRoots && !rootsByPath.ContainsKey(normalizedPath))
                {
                    var rootEntry = new RootPathItemViewModel { Path = normalizedPath, IsEnabled = true };
                    rootsByPath.Add(normalizedPath, rootEntry);
                    configuredRoots.Add(rootEntry);
                }
            }
            else
            {
                additionalArguments.Add(argument);
            }
        }

        // Root paths are owned by Settings and appended by the Filesystem runtime separately.
        // Keep only server-specific arguments in the persisted entry to avoid passing roots twice.
        candidate.Args = additionalArguments;
        candidate.AdditionalJson = RemoveFilesystemEditorMetadata(candidate.AdditionalJson);
        ResultFilesystemRoots.Clear();
        ResultFilesystemRoots.AddRange(configuredRoots.Select(rootEntry => new RootPathItemViewModel
        {
            Path = rootEntry.Path,
            IsEnabled = rootEntry.IsEnabled
        }));
    }

    private static string? RemoveFilesystemEditorMetadata(string? additionalJson)
    {
        if (string.IsNullOrWhiteSpace(additionalJson)) return additionalJson;

        try
        {
            if (McpServerConfigParser.DeserializeJson(additionalJson!) is not IDictionary<string, object?> additional)
                return additionalJson;

            additional.Remove("allowedRoots");
            return additional.Count == 0 ? null : McpServerConfigParser.SerializeJson(additional);
        }
        catch
        {
            return additionalJson;
        }
    }

    private bool ConfirmRemoteEndpointTrust(CustomMcpServerEntry candidate)
    {
        if (!string.Equals(candidate.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            candidate.TrustedExternalOrigin = null;
            _trustedExternalOrigin = null;
            return true;
        }

        if (!McpRemoteEndpointTrust.TryGetEndpoint(candidate.Url, out var endpoint, out var origin, out _))
            return true;

        if (endpoint!.IsLoopback)
        {
            candidate.TrustedExternalOrigin = null;
            _trustedExternalOrigin = null;
            return true;
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            ShowCustomDialog(
                I18n.Tr(
                    "Удалённые MCP-серверы можно подключать только по HTTPS. Обычный HTTP разрешён исключительно для localhost / loopback.",
                    "Remote MCP servers can only be connected over HTTPS. Plain HTTP is allowed only for localhost / loopback."),
                I18n.Tr("Небезопасное удалённое подключение", "Insecure remote connection"));
            return false;
        }

        if (string.Equals(candidate.TrustedExternalOrigin, origin, StringComparison.OrdinalIgnoreCase))
        {
            _trustedExternalOrigin = origin;
            return true;
        }

        var confirmation = ShowCustomDialog(
            I18n.Tr(
                "Подтвердите доверие к удалённому MCP-серверу:\n\n" + origin +
                "\n\nBridge будет отправлять этому адресу MCP-запросы и все указанные HTTP-заголовки, включая Authorization / Bearer token. Значения заголовков сохраняются без шифрования в Config/settings.json. Подтверждайте только сервер, которому доверяете. Разрешение будет сохранено только для этой записи MCP и этого HTTPS origin (домен + порт).\n\nПродолжить?",
                "Trust this remote MCP server:\n\n" + origin +
                "\n\nBridge will send MCP requests and all configured HTTP headers, including Authorization / Bearer tokens, to this endpoint. Header values are stored unencrypted in Config/settings.json. Only approve a server you trust. Approval is stored only for this MCP entry and this HTTPS origin (host + port).\n\nContinue?"),
            I18n.Tr("Подтверждение удалённого MCP", "Remote MCP trust confirmation"),
            MessageBoxButton.YesNo);

        if (confirmation != MessageBoxResult.Yes)
            return false;

        candidate.TrustedExternalOrigin = origin;
        _trustedExternalOrigin = origin;
        return true;
    }

    private static void ValidateOptionalStringProperty(
        IDictionary<string, object?> root,
        string propertyName,
        bool required = false)
    {
        if (!root.TryGetValue(propertyName, out var rawValue))
        {
            if (required)
                throw new FormatException(I18n.Tr(
                    "Не заполнено обязательное строковое поле " + propertyName + ".",
                    "Required string property '" + propertyName + "' is missing."));
            return;
        }

        if (rawValue is not string value ||
            (required && string.IsNullOrWhiteSpace(value)))
        {
            throw new FormatException(I18n.Tr(
                "Поле " + propertyName + " должно содержать строковое значение.",
                "Property '" + propertyName + "' must contain a string value."));
        }
    }

    private async Task RunVerificationAsync(bool saveOnSuccess)
    {
        if (_isVerifying) return;

        CustomMcpServerEntry candidate;
        try
        {
            candidate = BuildCandidate();
            if (!ConfirmRemoteEndpointTrust(candidate)) return;
        }
        catch (FormatException exception)
        {
            ErrorLog.LogVerification("[UI] " + I18n.Tr("Ошибка конфигурации: ", "Configuration error: ") + exception.Message);
            ShowCustomDialog(exception.Message, I18n.Tr("Некорректная конфигурация", "Invalid Configuration"));
            return;
        }

        _isVerifying = true;
        _verifyButton.IsEnabled = false;
        _verifyAndSaveButton.IsEnabled = false;
        _saveButton.IsEnabled = false;
        _verificationProgressBar.IsIndeterminate = true;
        _verificationProgressBar.Visibility = Visibility.Visible;
        _verificationActivityText.Text = I18n.Tr(
            "Запускаю процесс MCP. При первом запуске может потребоваться загрузка и установка зависимостей.",
            "Starting the MCP process. The first run may need to download and install dependencies.");
        _summaryText.Text = I18n.Tr("Верификация выполняется…", "Verification is running…");
        _summaryText.Foreground = SyntaxBrush;
        _diagBox.Text = I18n.Tr("Ожидание вывода запускаемого процесса…", "Waiting for child-process output…");
        _diagBox.Visibility = Visibility.Visible;

        try
        {
            var report = await _infrastructure.VerifyCandidateAsync(
                candidate,
                result =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _step1Text.Text = "1. Pre-flight: " + result.Step1Preflight;
                        _step2Text.Text = I18n.Tr("2. Транспорт и порт: ", "2. Transport & port: ") + result.Step2Transport;
                        _step3Text.Text = I18n.Tr("3. Протокол MCP: ", "3. MCP Protocol: ") + result.Step3Initialize;
                        _step4Text.Text = I18n.Tr("4. Проверка инструментов: ", "4. Tools audit: ") + result.Step4Tools;
                        if (!string.IsNullOrWhiteSpace(result.Summary))
                            _summaryText.Text = result.Summary;

                        _verificationActivityText.Text = !string.IsNullOrWhiteSpace(result.Step3Initialize)
                            ? result.Step3Initialize
                            : !string.IsNullOrWhiteSpace(result.Step2Transport)
                                ? result.Step2Transport
                                : I18n.Tr("Выполняется проверка…", "Verification in progress…");

                        if (!string.IsNullOrWhiteSpace(result.DiagnosticOutput))
                        {
                            _diagBox.Text = result.DiagnosticOutput;
                            if (!_diagBox.IsKeyboardFocusWithin)
                            {
                                _diagBox.SelectionStart = _diagBox.Text.Length;
                                _diagBox.ScrollToEnd();
                            }
                        }
                    }));
                },
                _isFilesystemEditor
                    ? ResultFilesystemRoots.Where(root => root.IsEnabled).Select(root => root.Path)
                    : null,
                saveOnSuccess && candidate.Enabled && !_isFilesystemEditor ? candidate : null);

            _step1Text.Text = "1. Pre-flight: " + report.Step1Preflight;
            _step2Text.Text = I18n.Tr("2. Транспорт и порт: ", "2. Transport & port: ") + report.Step2Transport;
            _step3Text.Text = I18n.Tr("3. Протокол MCP: ", "3. MCP Protocol: ") + report.Step3Initialize;
            _step4Text.Text = I18n.Tr("4. Проверка инструментов: ", "4. Tools audit: ") + report.Step4Tools;
            _summaryText.Text = report.Summary;
            _summaryText.Foreground = report.IsSuccess
                ? SyntaxBrush
                : new SolidColorBrush(Color.FromRgb(255, 107, 120));
            _verificationActivityText.Text = report.Summary;

            if (!string.IsNullOrWhiteSpace(report.DiagnosticOutput))
            {
                _diagBox.Text = report.DiagnosticOutput;
                _diagBox.Visibility = Visibility.Visible;
            }
            else
            {
                _diagBox.Clear();
                _diagBox.Visibility = Visibility.Collapsed;
            }

            if (saveOnSuccess)
            {
                if (report.IsSuccess)
                {
                    ResultServers.Clear();
                    ResultServers.Add(candidate);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    var ask = ShowCustomDialog(
                        report.Summary + "\n\n" + I18n.Tr("Сохранить этот сервер в списке в режиме ОЖИДАНИЕ?", "Save this server in the list in WAITING mode?"),
                        I18n.Tr("Сохранение сервера", "Save Server"),
                        MessageBoxButton.YesNo);

                    if (ask == MessageBoxResult.Yes)
                    {
                        ResultServers.Clear();
                        ResultServers.Add(candidate);
                        DialogResult = true;
                        Close();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ErrorLog.LogVerification("[UI] " + I18n.Tr("Исключение верификации: ", "Verification exception: ") + ErrorLog.Describe(exception));
            _summaryText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 120));
            _summaryText.Text = exception.Message;
            _verificationActivityText.Text = exception.Message;
            _diagBox.Text = ErrorLog.Describe(exception);
            _diagBox.Visibility = Visibility.Visible;
            ShowCustomDialog(exception.Message, I18n.Tr("Ошибка проверки", "Verification Error"));
        }
        finally
        {
            _verificationProgressBar.IsIndeterminate = false;
            _verificationProgressBar.Visibility = Visibility.Collapsed;
            _isVerifying = false;
            _verifyButton.IsEnabled = true;
            _verifyAndSaveButton.IsEnabled = true;
            _saveButton.IsEnabled = true;
        }
    }

    private MessageBoxResult ShowCustomDialog(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 450,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 29)),
            Foreground = Brushes.White,
            ShowInTaskbar = false
        };

        var chrome = new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(dialog, chrome);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topBar = new Border
        {
            Background = Application.Current.TryFindResource("TopBarBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(22, 25, 29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(20, 22, 25)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var tbGrid = new Grid();
        var tbTitle = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        tbGrid.Children.Add(tbTitle);
        
        var closeBtn = new Button { 
            Style = Application.Current.TryFindResource("ChromeCloseButtonStyle") as Style,
            Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"), Stroke = new SolidColorBrush(Color.FromRgb(140, 148, 158)), StrokeThickness = 1.3 },
            HorizontalAlignment = HorizontalAlignment.Right
        };
        WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
        closeBtn.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };
        tbGrid.Children.Add(closeBtn);
        topBar.Child = tbGrid;
        root.Children.Add(topBar);

        var msgBox = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239)),
            FontSize = 13
        };
        Grid.SetRow(msgBox, 1);
        root.Children.Add(msgBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20) };
        Grid.SetRow(btnPanel, 2);

        MessageBoxResult result = MessageBoxResult.None;

        if (buttons == MessageBoxButton.YesNo)
        {
            var yesBtn = new Button { Content = "Да (Yes)", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 10, 0), Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style };
            yesBtn.Click += (s, e) => { result = MessageBoxResult.Yes; dialog.DialogResult = true; dialog.Close(); };
            var noBtn = new Button { Content = "Нет (No)", Padding = new Thickness(16, 6, 16, 6) };
            noBtn.Click += (s, e) => { result = MessageBoxResult.No; dialog.DialogResult = false; dialog.Close(); };
            btnPanel.Children.Add(yesBtn);
            btnPanel.Children.Add(noBtn);
        }
        else
        {
            var okBtn = new Button { Content = "OK", Padding = new Thickness(16, 6, 16, 6), Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style };
            okBtn.Click += (s, e) => { result = MessageBoxResult.OK; dialog.DialogResult = true; dialog.Close(); };
            btnPanel.Children.Add(okBtn);
        }

        root.Children.Add(btnPanel);
        
        var outerBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(49, 54, 61)), BorderThickness = new Thickness(1), Child = root };
        dialog.Content = outerBorder;
        
        dialog.ShowDialog();
        return result;
    }

    private static Border CreateCardBorder()
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(31, 34, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 54, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14)
        };
    }

    private static TextBlock CreateLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 0, 0, 2)
        };
    }

    private static TextBox CreateStepLine(string text)
    {
        return new TextBox
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(190, 196, 203)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            MinHeight = 22,
            MaxHeight = 58,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 3, 0, 3),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(55, 91, 95))
        };
    }
}