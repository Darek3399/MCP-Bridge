using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using McpBridge.Models;
using McpBridge.Services;
using Forms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace McpBridge;

public sealed class ServerEditorWindow : Window
{
    private readonly McpInfrastructureService _infrastructure;
    private readonly TextBox _nameBox;
    private readonly CheckBox _enabledCheckBox;
    private readonly RadioButton _stdioRadio;
    private readonly RadioButton _httpRadio;
    private readonly StackPanel _stdioPanel;
    private readonly StackPanel _httpPanel;
    private readonly TextBox _commandBox;
    private readonly TextBox _argsBox;
    private readonly TextBox _cwdBox;
    private readonly TextBox _envBox;
    private readonly TextBox _headersBox;
    private readonly TextBox _stdioAdditionalJsonBox;
    private readonly TextBox _httpAdditionalJsonBox;
    private readonly TextBox _urlBox;
    private readonly TextBlock _step1Text;
    private readonly TextBlock _step2Text;
    private readonly TextBlock _step3Text;
    private readonly TextBlock _step4Text;
    private readonly TextBlock _summaryText;
    private readonly TextBox _diagBox;
    private readonly Button _verifyButton;
    private readonly Button _verifyAndSaveButton;
    private readonly Button _saveButton;
    private bool _isVerifying;
    private string? _editingId;
    private string? _trustedExternalOrigin;
    private readonly bool _isBuiltInFilesystem;
    private StackPanel? _filesystemRootsList;
    private readonly List<FilesystemRootEditorRow> _filesystemRootRows = new();

    private sealed class FilesystemRootEditorRow
    {
        public RootPathItemViewModel Item { get; }
        public CheckBox EnabledCheckBox { get; }
        public TextBox PathBox { get; }

        public Grid Container { get; }

        public FilesystemRootEditorRow(RootPathItemViewModel item, CheckBox enabledCheckBox, TextBox pathBox, Grid container)
        {
            Item = item;
            EnabledCheckBox = enabledCheckBox;
            PathBox = pathBox;
            Container = container;
        }
    }

    public List<CustomMcpServerEntry> ResultServers { get; } = new();
    public List<RootPathItemViewModel> ResultFilesystemRoots { get; } = new();

    public ServerEditorWindow(
        McpInfrastructureService infrastructure,
        CustomMcpServerEntry? existing = null,
        IEnumerable<RootPathItemViewModel>? filesystemRoots = null)
    {
        _infrastructure = infrastructure;
        _isBuiltInFilesystem = filesystemRoots != null;
        Title = existing == null
            ? I18n.Tr("Добавить MCP-сервер", "Add MCP Server")
            : I18n.Tr("Настройка MCP-сервера: ", "Configure MCP Server: ") + existing.Name;
        Width = 720;
        Height = 760;
        MinWidth = 640;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 29));
        Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239));

        var outerGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var rootScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var mainStack = new StackPanel();

        var pasteCard = CreateCardBorder();
        pasteCard.Margin = new Thickness(0, 0, 0, 14);
        var pasteGrid = new Grid();
        pasteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pasteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pasteInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        pasteInfo.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Быстрый импорт конфигурации", "Quick Configuration Import"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        pasteInfo.Children.Add(new TextBlock
        {
            Text = I18n.Tr(
                "Скопируйте JSON-блок mcpServers, команду (npx / uvx / python) или URL и нажмите кнопку справа.",
                "Copy an mcpServers JSON block, CLI command (npx / uvx / python), or URL and click the button on the right."),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var smartPasteButton = new Button
        {
            Content = I18n.Tr("📋 Умная вставка из буфера", "📋 Smart Paste from Clipboard"),
            Padding = new Thickness(14, 8, 14, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
            smartPasteButton.Style = primaryStyle;
        smartPasteButton.Click += SmartPaste_Click;

        Grid.SetColumn(pasteInfo, 0);
        Grid.SetColumn(smartPasteButton, 1);
        pasteGrid.Children.Add(pasteInfo);
        pasteGrid.Children.Add(smartPasteButton);
        pasteCard.Child = pasteGrid;
        mainStack.Children.Add(pasteCard);

        var formCard = CreateCardBorder();
        formCard.Margin = new Thickness(0, 0, 0, 14);
        var formStack = new StackPanel();

        formStack.Children.Add(CreateLabel(I18n.Tr("НАЗВАНИЕ СЕРВЕРА (ОТОБРАЖАЕТСЯ В СПИСКЕ)", "SERVER NAME (DISPLAYED IN LIST)")));
        _nameBox = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
        formStack.Children.Add(_nameBox);
        _enabledCheckBox = new CheckBox
        {
            Content = I18n.Tr("Сервер включён и доступен для подключения", "Server is enabled and available for connections"),
            IsChecked = true,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 14)
        };
        formStack.Children.Add(_enabledCheckBox);

        formStack.Children.Add(CreateLabel(I18n.Tr("ТИП ПОДКЛЮЧЕНИЯ (ТРАНСПОРТ)", "CONNECTION TYPE (TRANSPORT)")));
        var radioPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 6, 0, 12) };
        _stdioRadio = new RadioButton
        {
            Content = I18n.Tr("Процесс STDIO (npx, uvx, python, node, .exe — запуск и управление мостом)", "STDIO Process (npx, uvx, python, node, .exe — managed by bridge)"),
            Foreground = Brushes.White,
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 6)
        };
        _httpRadio = new RadioButton
        {
            Content = I18n.Tr("HTTP / HTTPS MCP-сервер (локальный или удалённый)", "HTTP / HTTPS MCP endpoint (local or remote)"),
            Foreground = Brushes.White
        };
        _stdioRadio.Checked += TransportRadio_Changed;
        _httpRadio.Checked += TransportRadio_Changed;
        _httpRadio.IsEnabled = !_isBuiltInFilesystem;
        radioPanel.Children.Add(_stdioRadio);
        radioPanel.Children.Add(_httpRadio);
        formStack.Children.Add(radioPanel);
        if (_isBuiltInFilesystem)
        {
            formStack.Children.Add(new TextBlock
            {
                Text = I18n.Tr(
                    "Встроенный Filesystem MCP использует STDIO-транспорт под управлением Bridge. Его разрешённые директории настраиваются ниже.",
                    "The built-in Filesystem MCP uses Bridge-managed STDIO transport. Its allowed directories are configured below."),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -4, 0, 10)
            });
        }

        _stdioPanel = new StackPanel();
        _stdioPanel.Children.Add(CreateLabel(I18n.Tr("КОМАНДА ИЛИ ИСПОЛНЯЕМЫЙ ФАЙЛ (COMMAND)", "COMMAND OR EXECUTABLE (COMMAND)")));
        var cmdGrid = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _commandBox = new TextBox();
        var browseCmdBtn = new Button { Content = I18n.Tr("Обзор…", "Browse…"), Padding = new Thickness(12, 6, 12, 6) };
        browseCmdBtn.Click += BrowseCommand_Click;
        Grid.SetColumn(_commandBox, 0);
        Grid.SetColumn(browseCmdBtn, 2);
        cmdGrid.Children.Add(_commandBox);
        cmdGrid.Children.Add(browseCmdBtn);
        _stdioPanel.Children.Add(cmdGrid);

        _stdioPanel.Children.Add(CreateLabel(_isBuiltInFilesystem
            ? I18n.Tr("ДОПОЛНИТЕЛЬНЫЕ АРГУМЕНТЫ (ARGS)", "ADDITIONAL COMMAND ARGUMENTS (ARGS)")
            : I18n.Tr("АРГУМЕНТЫ КОМАНДНОЙ СТРОКИ (ARGS)", "COMMAND LINE ARGUMENTS (ARGS)")));
        _argsBox = new TextBox { Margin = new Thickness(0, 4, 0, 10), FontFamily = new FontFamily("Consolas") };
        _stdioPanel.Children.Add(_argsBox);

        _stdioPanel.Children.Add(CreateLabel(I18n.Tr("РАБОЧАЯ ПАПКА (ОПЦИОНАЛЬНО)", "WORKING DIRECTORY (OPTIONAL)")));
        var cwdGrid = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        cwdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cwdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        cwdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _cwdBox = new TextBox { FontFamily = new FontFamily("Consolas") };
        var browseCwdBtn = new Button { Content = I18n.Tr("Папка…", "Folder…"), Padding = new Thickness(12, 6, 12, 6) };
        browseCwdBtn.Click += BrowseCwd_Click;
        Grid.SetColumn(_cwdBox, 0);
        Grid.SetColumn(browseCwdBtn, 2);
        cwdGrid.Children.Add(_cwdBox);
        cwdGrid.Children.Add(browseCwdBtn);
        _stdioPanel.Children.Add(cwdGrid);
        if (_isBuiltInFilesystem)
        {
            _stdioPanel.Children.Add(new TextBlock
            {
                Text = I18n.Tr(
                    "Рабочая папка — это текущая директория процесса при запуске. Она не предоставляет доступ к файлам. Доступ задаётся только списком разрешённых директорий ниже.",
                    "Working directory is the process current directory at launch. It does not grant file access. Access is controlled only by the allowed-directories list below."),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -5, 0, 10)
            });
        }

        _stdioPanel.Children.Add(CreateLabel(I18n.Tr("ПЕРЕМЕННЫЕ ОКРУЖЕНИЯ (ENV: KEY=VALUE ПО ОДНОЙ НА СТРОКУ)", "ENVIRONMENT VARIABLES (ENV: KEY=VALUE PER LINE)")));
        _envBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 4),
            Height = 56,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas")
        };
        _stdioPanel.Children.Add(_envBox);

        _stdioPanel.Children.Add(CreateLabel(I18n.Tr("ДОПОЛНИТЕЛЬНЫЕ ПОЛЯ JSON (ОПЦИОНАЛЬНО)", "ADDITIONAL JSON PROPERTIES (OPTIONAL)")));
        _stdioAdditionalJsonBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 4),
            Height = 104,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Text = "{}"
        };
        _stdioPanel.Children.Add(_stdioAdditionalJsonBox);
        _stdioPanel.Children.Add(new TextBlock
        {
            Text = _isBuiltInFilesystem
                ? I18n.Tr(
                    "Дополнительные свойства MCP JSON сохраняются и передаются в конфигурацию. Основные поля command, дополнительные args, env, cwd и разрешённые директории настраиваются выше.",
                    "Additional MCP JSON properties are preserved and passed to the server configuration. Core fields command, extra args, env, cwd and allowed directories are configured above.")
                : I18n.Tr(
                    "Здесь можно редактировать любые дополнительные свойства записи MCP. Основные поля command, args, env и cwd задаются выше.",
                    "Edit any additional properties of this MCP entry here. Core fields command, args, env and cwd are configured above."),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        if (_isBuiltInFilesystem)
        {
            var rootsCard = CreateCardBorder();
            rootsCard.Margin = new Thickness(0, 12, 0, 0);
            var rootsStack = new StackPanel();
            rootsStack.Children.Add(CreateLabel(I18n.Tr("РАЗРЕШЁННЫЕ ДИРЕКТОРИИ FILESYSTEM MCP", "FILESYSTEM MCP ALLOWED DIRECTORIES")));
            rootsStack.Children.Add(new TextBlock
            {
                Text = I18n.Tr(
                    "Отмеченные директории доступны серверу. Снятая галочка сохраняет путь в списке, но отзывает доступ. Другие каталоги не передаются Filesystem MCP.",
                    "Checked directories are accessible to the server. Unchecking a directory keeps it in the list but revokes access. Other directories are not passed to Filesystem MCP."),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8)
            });
            _filesystemRootsList = new StackPanel();
            rootsStack.Children.Add(_filesystemRootsList);
            var addRootButton = new Button
            {
                Content = I18n.Tr("＋ Добавить директорию", "＋ Add Directory"),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            addRootButton.Click += AddFilesystemRoot_Click;
            rootsStack.Children.Add(addRootButton);
            rootsCard.Child = rootsStack;
            _stdioPanel.Children.Add(rootsCard);

            foreach (var root in filesystemRoots!)
            {
                AddFilesystemRootRow(new RootPathItemViewModel { Path = root.Path, IsEnabled = root.IsEnabled });
            }
        }

        formStack.Children.Add(_stdioPanel);

        _httpPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _httpPanel.Children.Add(CreateLabel(I18n.Tr("HTTP / HTTPS / STREAMABLE HTTP / SSE MCP URL", "HTTP / HTTPS / STREAMABLE HTTP / SSE MCP URL")));
        _urlBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 6),
            FontFamily = new FontFamily("Consolas"),
            Text = "http://127.0.0.1:8050/mcp"
        };
        _httpPanel.Children.Add(_urlBox);
        _httpPanel.Children.Add(new TextBlock
        {
            Text = I18n.Tr(
                "Локальные loopback-адреса (127.0.0.1 / localhost) подключаются без подтверждения. Удалённые адреса разрешены только по HTTPS и требуют отдельного подтверждения доверия к origin (домену и порту). HTTP-заголовки ниже, включая Authorization, будут отправляться этому серверу.",
                "Local loopback addresses (127.0.0.1 / localhost) connect without approval. Remote endpoints require HTTPS and a separate trust confirmation for the origin (host and port). HTTP headers below, including Authorization, will be sent to that server."),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        _httpPanel.Children.Add(CreateLabel(I18n.Tr("HTTP-ЗАГОЛОВКИ (ОПЦИОНАЛЬНО, KEY=VALUE)", "HTTP HEADERS (OPTIONAL, KEY=VALUE)")));
        _headersBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 10),
            Height = 62,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas")
        };
        _httpPanel.Children.Add(_headersBox);

        _httpPanel.Children.Add(CreateLabel(I18n.Tr("ДОПОЛНИТЕЛЬНЫЕ ПОЛЯ JSON (ОПЦИОНАЛЬНО)", "ADDITIONAL JSON PROPERTIES (OPTIONAL)")));
        _httpAdditionalJsonBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 4),
            Height = 104,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Text = "{}"
        };
        _httpPanel.Children.Add(_httpAdditionalJsonBox);
        _httpPanel.Children.Add(new TextBlock
        {
            Text = I18n.Tr(
                "Дополнительные свойства исходного external JSON сохраняются. Для внешнего подключения Bridge использует URL и HTTP-заголовки.",
                "Additional properties from the original external JSON are preserved. For external connections, Bridge uses the URL and HTTP headers."),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        formStack.Children.Add(_httpPanel);

        formCard.Child = formStack;
        mainStack.Children.Add(formCard);

        var verifyCard = CreateCardBorder();
        var verifyStack = new StackPanel();
        verifyStack.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Конвейер верификации сервера (4 этапа)", "Server Verification Pipeline (4 Stages)"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _step1Text = CreateStepLine(I18n.Tr("1. Pre-flight (проверка путей и параметров): ожидание", "1. Pre-flight (paths & parameters): waiting"));
        _step2Text = CreateStepLine(I18n.Tr("2. Запуск процесса / проверка порта: ожидание", "2. Process launch / port check: waiting"));
        _step3Text = CreateStepLine(I18n.Tr("3. Рукопожатие протокола (MCP initialize): ожидание", "3. Protocol handshake (MCP initialize): waiting"));
        _step4Text = CreateStepLine(I18n.Tr("4. Ревизия инструментов (tools/list): ожидание", "4. Tools audit (tools/list): waiting"));
        verifyStack.Children.Add(_step1Text);
        verifyStack.Children.Add(_step2Text);
        verifyStack.Children.Add(_step3Text);
        verifyStack.Children.Add(_step4Text);

        _summaryText = new TextBlock
        {
            Text = I18n.Tr("Нажмите «Проверить и добавить» для запуска теста и добавления в мост.", "Click 'Verify & Add' to test and add the server to the bridge."),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(44, 229, 208)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 6)
        };
        verifyStack.Children.Add(_summaryText);

        _diagBox = new TextBox
        {
            Height = 76,
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Visibility = Visibility.Collapsed
        };
        verifyStack.Children.Add(_diagBox);

        verifyCard.Child = verifyStack;
        mainStack.Children.Add(verifyCard);

        rootScroll.Content = mainStack;
        Grid.SetRow(rootScroll, 0);
        outerGrid.Children.Add(rootScroll);

        var btnGrid = new Grid();
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _verifyButton = new Button { Content = I18n.Tr("🔍 Проверить", "🔍 Verify"), Padding = new Thickness(14, 8, 14, 8) };
        _verifyButton.Click += VerifyOnly_Click;

        _saveButton = new Button { Content = I18n.Tr("Сохранить без проверки", "Save Without Check"), Padding = new Thickness(12, 8, 12, 8) };
        _saveButton.Click += SaveWithoutVerify_Click;

        _verifyAndSaveButton = new Button { Content = I18n.Tr("✓ Проверить и добавить", "✓ Verify & Add"), Padding = new Thickness(16, 7, 16, 9) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style pStyle)
            _verifyAndSaveButton.Style = pStyle;
        _verifyAndSaveButton.Click += VerifyAndSave_Click;

        var cancelButton = new Button { Content = I18n.Tr("Отмена", "Cancel"), Padding = new Thickness(14, 8, 14, 8) };
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        Grid.SetColumn(_verifyButton, 0);
        Grid.SetColumn(_saveButton, 2);
        Grid.SetColumn(_verifyAndSaveButton, 4);
        Grid.SetColumn(cancelButton, 6);

        btnGrid.Children.Add(_verifyButton);
        btnGrid.Children.Add(_saveButton);
        btnGrid.Children.Add(_verifyAndSaveButton);
        btnGrid.Children.Add(cancelButton);

        Grid.SetRow(btnGrid, 2);
        outerGrid.Children.Add(btnGrid);

        Content = outerGrid;

        if (existing != null)
        {
            PopulateFromEntry(existing);
            _verifyAndSaveButton.Content = I18n.Tr("✓ Проверить и сохранить", "✓ Verify & Save");
        }
    }

    private void PopulateFromEntry(CustomMcpServerEntry entry)
    {
        _editingId = entry.Id;
        _trustedExternalOrigin = entry.TrustedExternalOrigin;
        _enabledCheckBox.IsChecked = entry.Enabled;
        _nameBox.Text = entry.Name;
        var additionalJson = string.IsNullOrWhiteSpace(entry.AdditionalJson) ? "{}" : entry.AdditionalJson!;
        if (string.Equals(entry.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            _httpRadio.IsChecked = true;
            _urlBox.Text = entry.Url ?? string.Empty;
            _headersBox.Text = McpServerConfigParser.FormatHeaderLines(entry.Headers);
            _httpAdditionalJsonBox.Text = additionalJson;
        }
        else
        {
            _stdioRadio.IsChecked = true;
            _commandBox.Text = entry.Command ?? string.Empty;
            _argsBox.Text = McpServerConfigParser.FormatArguments(entry.Args);
            _cwdBox.Text = entry.WorkingDirectory ?? string.Empty;
            _envBox.Text = McpServerConfigParser.FormatEnvLines(entry.Env);
            _stdioAdditionalJsonBox.Text = additionalJson;
        }
    }

    private void TransportRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_stdioPanel == null || _httpPanel == null) return;
        var isHttp = _httpRadio.IsChecked == true;
        _stdioPanel.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
        _httpPanel.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseCommand_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = I18n.Tr("Выберите исполняемый файл или скрипт", "Select Executable or Script"),
            Filter = "Executables & Scripts (*.exe;*.cmd;*.bat;*.js;*.py)|*.exe;*.cmd;*.bat;*.js;*.py|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (ext == ".js" || ext == ".mjs" || ext == ".cjs")
            {
                _commandBox.Text = "node";
                _argsBox.Text = "\"" + dialog.FileName + "\"";
            }
            else if (ext == ".py")
            {
                _commandBox.Text = "python";
                _argsBox.Text = "\"" + dialog.FileName + "\"";
            }
            else
            {
                _commandBox.Text = dialog.FileName;
            }
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
                _nameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void BrowseCwd_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = I18n.Tr("Выберите рабочую директорию", "Select working directory")
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            _cwdBox.Text = dialog.SelectedPath;
    }

    private void AddFilesystemRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_filesystemRootsList == null) return;
        try
        {
            var selectedPath = NativeFolderPicker.Show(
                this,
                I18n.Tr("Выберите корневую директорию для Filesystem MCP", "Select a root directory for Filesystem MCP"));
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            var normalized = SettingsStore.NormalizePath(selectedPath!);
            if (!Directory.Exists(normalized))
                throw new DirectoryNotFoundException(normalized);

            var existing = _filesystemRootRows.FirstOrDefault(row =>
            {
                try { return string.Equals(SettingsStore.NormalizePath(row.PathBox.Text), normalized, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
            if (existing != null)
            {
                existing.EnabledCheckBox.IsChecked = true;
                return;
            }

            AddFilesystemRootRow(new RootPathItemViewModel { Path = normalized, IsEnabled = true });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, I18n.Tr("Не удалось добавить директорию", "Unable to add directory"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddFilesystemRootRow(RootPathItemViewModel item)
    {
        if (_filesystemRootsList == null) return;
        var rowGrid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new CheckBox
        {
            IsChecked = item.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = I18n.Tr("Разрешить доступ к этой директории", "Allow access to this directory")
        };
        var path = new TextBox
        {
            Text = item.Path,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var remove = new Button
        {
            Content = "✕",
            Padding = new Thickness(8, 3, 8, 3),
            ToolTip = I18n.Tr("Удалить директорию из allow-list", "Remove directory from the allow-list")
        };
        var entry = new FilesystemRootEditorRow(item, enabled, path, rowGrid);
        enabled.Checked += (_, _) => item.IsEnabled = true;
        enabled.Unchecked += (_, _) => item.IsEnabled = false;
        remove.Click += (_, _) =>
        {
            _filesystemRootRows.Remove(entry);
            _filesystemRootsList.Children.Remove(rowGrid);
        };

        Grid.SetColumn(enabled, 0);
        Grid.SetColumn(path, 1);
        Grid.SetColumn(remove, 2);
        rowGrid.Children.Add(enabled);
        rowGrid.Children.Add(path);
        rowGrid.Children.Add(remove);
        _filesystemRootRows.Add(entry);
        _filesystemRootsList.Children.Add(rowGrid);
    }

    private List<RootPathItemViewModel> CaptureFilesystemRoots()
    {
        var result = new List<RootPathItemViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _filesystemRootRows)
        {
            if (string.IsNullOrWhiteSpace(row.PathBox.Text))
                throw new FormatException(I18n.Tr("Путь к разрешённой директории не может быть пустым.", "An allowed directory path cannot be empty."));

            string normalized;
            try { normalized = SettingsStore.NormalizePath(row.PathBox.Text); }
            catch (Exception ex) { throw new FormatException(I18n.Tr("Некорректный путь: ", "Invalid directory path: ") + row.PathBox.Text, ex); }

            if (!Directory.Exists(normalized))
                throw new FormatException(I18n.Tr("Директория не существует: ", "Directory does not exist: ") + normalized);
            if (!seen.Add(normalized))
                throw new FormatException(I18n.Tr("Директория указана несколько раз: ", "Directory is listed more than once: ") + normalized);

            result.Add(new RootPathItemViewModel
            {
                Path = normalized,
                IsEnabled = row.EnabledCheckBox.IsChecked == true
            });
        }
        return result;
    }

    private void StoreResultFilesystemRoots(IEnumerable<RootPathItemViewModel>? roots)
    {
        ResultFilesystemRoots.Clear();
        if (roots == null) return;
        ResultFilesystemRoots.AddRange(roots.Select(root => new RootPathItemViewModel { Path = root.Path, IsEnabled = root.IsEnabled }));
    }

    private void SmartPaste_Click(object sender, RoutedEventArgs e)
    {
        string clipText;
        try
        {
            clipText = WpfClipboard.ContainsText() ? WpfClipboard.GetText() : string.Empty;
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, I18n.Tr("Буфер обмена", "Clipboard"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parsed = McpServerConfigParser.ParseSmartInput(clipText, out var error);
        if (parsed.Count == 0)
        {
            WpfMessageBox.Show(this, error, I18n.Tr("Умная вставка", "Smart Paste"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (parsed.Count == 1)
        {
            var single = parsed[0];
            if (_editingId != null) single.Id = _editingId;
            PopulateFromEntry(single);
            _summaryText.Text = I18n.Tr(
                "✓ Данные («" + single.Name + "») распознаны из буфера обмена. Нажмите «Проверить и добавить».",
                "✓ Configuration ('" + single.Name + "') parsed from clipboard. Click 'Verify & Add'.");
            return;
        }

        var names = string.Join(", ", parsed.Select(s => s.Name));
        var ask = WpfMessageBox.Show(
            this,
            I18n.Tr(
                "Обнаружено несколько MCP-серверов (" + parsed.Count + " шт.):\n" + names + "\n\nДобавить их все?",
                "Multiple MCP servers detected (" + parsed.Count + "):\n" + names + "\n\nAdd all of them?"),
            I18n.Tr("Пакетный импорт", "Batch Import"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (ask == MessageBoxResult.Yes)
        {
            foreach (var server in parsed)
                if (!ConfirmRemoteEndpointTrust(server)) return;

            ResultServers.Clear();
            ResultServers.AddRange(parsed);
            DialogResult = true;
            Close();
        }
        else
        {
            PopulateFromEntry(parsed[0]);
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
            WpfMessageBox.Show(
                this,
                I18n.Tr(
                    "Удалённые MCP-серверы можно подключать только по HTTPS. Обычный HTTP разрешён исключительно для localhost / loopback.",
                    "Remote MCP servers can only be connected over HTTPS. Plain HTTP is allowed only for localhost / loopback."),
                I18n.Tr("Небезопасное удалённое подключение", "Insecure remote connection"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (string.Equals(candidate.TrustedExternalOrigin, origin, StringComparison.OrdinalIgnoreCase))
        {
            _trustedExternalOrigin = origin;
            return true;
        }

        var confirmation = WpfMessageBox.Show(
            this,
            I18n.Tr(
                "Подтвердите доверие к удалённому MCP-серверу:\n\n" + origin +
                "\n\nBridge будет отправлять этому адресу MCP-запросы и все указанные HTTP-заголовки, включая Authorization / Bearer token. Заголовки сохраняются без шифрования в Config/settings.json. Подтверждайте только домен, которому доверяете. Разрешение будет сохранено только для этой записи сервера и этого HTTPS origin (домен + порт).\n\nПродолжить?",
                "Trust this remote MCP server:\n\n" + origin +
                "\n\nBridge will send MCP requests and all configured HTTP headers, including Authorization / Bearer tokens, to this endpoint. Header values are stored unencrypted in Config/settings.json. Only approve a domain you trust. Approval is stored only for this server entry and this HTTPS origin (host + port).\n\nContinue?"),
            I18n.Tr("Подтверждение удалённого MCP", "Remote MCP trust confirmation"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
            return false;

        candidate.TrustedExternalOrigin = origin;
        _trustedExternalOrigin = origin;
        return true;
    }

    private CustomMcpServerEntry BuildCandidateFromForm()
    {
        var isHttp = _httpRadio.IsChecked == true;
        var rawName = _nameBox.Text.Trim();
        var cmd = _commandBox.Text.Trim();
        var args = McpServerConfigParser.SplitCommandLine(_argsBox.Text);
        var url = _urlBox.Text.Trim();
        var additionalJson = McpServerConfigParser.NormalizeAdditionalJson(
            isHttp ? _httpAdditionalJsonBox.Text : _stdioAdditionalJsonBox.Text);

        if (string.IsNullOrWhiteSpace(rawName))
        {
            rawName = isHttp ? "HTTP MCP" : (args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? cmd);
            if (string.IsNullOrWhiteSpace(rawName)) rawName = "custom-mcp";
        }

        var id = !string.IsNullOrWhiteSpace(_editingId) ? _editingId! : SettingsStore.SanitizeServerId(rawName);
        if (string.IsNullOrWhiteSpace(id)) id = "srv_" + Guid.NewGuid().ToString("N").Substring(0, 6);

        return new CustomMcpServerEntry
        {
            Id = id,
            Name = rawName,
            Transport = isHttp ? "http" : "stdio",
            Command = isHttp ? null : cmd,
            Args = isHttp ? new List<string>() : args,
            WorkingDirectory = isHttp || string.IsNullOrWhiteSpace(_cwdBox.Text) ? null : _cwdBox.Text.Trim(),
            Env = isHttp ? new Dictionary<string, string>() : McpServerConfigParser.ParseEnvLines(_envBox.Text),
            Url = isHttp ? url : null,
            Headers = isHttp ? McpServerConfigParser.ParseHeaderLines(_headersBox.Text) : new Dictionary<string, string>(),
            AdditionalJson = additionalJson,
            TrustedExternalOrigin = isHttp ? _trustedExternalOrigin : null,
            Enabled = _enabledCheckBox.IsChecked == true
        };
    }

    private async void VerifyOnly_Click(object sender, RoutedEventArgs e) => await RunVerificationAsync(false);
    private async void VerifyAndSave_Click(object sender, RoutedEventArgs e) => await RunVerificationAsync(true);

    private void SaveWithoutVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_isVerifying) return;
        CustomMcpServerEntry candidate;
        List<RootPathItemViewModel>? filesystemRoots = null;
        try
        {
            candidate = BuildCandidateFromForm();
            if (!ConfirmRemoteEndpointTrust(candidate)) return;
            if (_isBuiltInFilesystem) filesystemRoots = CaptureFilesystemRoots();
        }
        catch (FormatException ex)
        {
            WpfMessageBox.Show(this, ex.Message, I18n.Tr("Некорректные параметры JSON", "Invalid JSON parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StoreResultFilesystemRoots(filesystemRoots);
        ResultServers.Clear();
        ResultServers.Add(candidate);
        DialogResult = true;
        Close();
    }

    private async Task RunVerificationAsync(bool saveOnSuccess)
    {
        if (_isVerifying) return;
        CustomMcpServerEntry candidate;
        List<RootPathItemViewModel>? filesystemRoots = null;
        CustomMcpServerEntry candidateForVerification;
        try
        {
            candidate = BuildCandidateFromForm();
            if (!ConfirmRemoteEndpointTrust(candidate)) return;
            if (_isBuiltInFilesystem)
                filesystemRoots = CaptureFilesystemRoots();

            candidateForVerification = candidate;
        }
        catch (Exception ex) when (ex is FormatException || ex is FileNotFoundException || ex is InvalidOperationException)
        {
            WpfMessageBox.Show(this, ex.Message, I18n.Tr("Некорректные параметры сервера", "Invalid server configuration"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isVerifying = true;
        _verifyButton.IsEnabled = false;
        _verifyAndSaveButton.IsEnabled = false;
        _saveButton.IsEnabled = false;
        _diagBox.Visibility = Visibility.Collapsed;

        try
        {
            var report = await _infrastructure.VerifyCandidateAsync(candidateForVerification, r =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _step1Text.Text = "1. Pre-flight: " + r.Step1Preflight;
                    _step2Text.Text = I18n.Tr("2. Транспорт и порт: ", "2. Transport & port: ") + r.Step2Transport;
                    _step3Text.Text = I18n.Tr("3. Протокол MCP: ", "3. MCP Protocol: ") + r.Step3Initialize;
                    _step4Text.Text = I18n.Tr("4. Инструменты: ", "4. Tools: ") + r.Step4Tools;
                    if (!string.IsNullOrWhiteSpace(r.Summary))
                        _summaryText.Text = r.Summary;
                }));
            },
            filesystemRoots?.Where(root => root.IsEnabled).Select(root => root.Path),
            saveOnSuccess && candidate.Enabled && !_isBuiltInFilesystem ? candidate : null);

            _summaryText.Foreground = report.IsSuccess
                ? new SolidColorBrush(Color.FromRgb(44, 229, 208))
                : new SolidColorBrush(Color.FromRgb(255, 107, 120));

            if (!string.IsNullOrWhiteSpace(report.DiagnosticOutput))
            {
                _diagBox.Text = report.DiagnosticOutput;
                _diagBox.Visibility = Visibility.Visible;
            }

            if (string.IsNullOrWhiteSpace(_nameBox.Text) && !string.IsNullOrWhiteSpace(report.DiscoveredServerName))
            {
                _nameBox.Text = report.DiscoveredServerName!;
                candidate.Name = report.DiscoveredServerName!;
            }

            if (saveOnSuccess)
            {
                if (report.IsSuccess)
                {
                    StoreResultFilesystemRoots(filesystemRoots);
                    ResultServers.Clear();
                    ResultServers.Add(candidate);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    var ask = WpfMessageBox.Show(
                        this,
                        report.Summary + "\n\n" + I18n.Tr("Сохранить этот сервер в список (в режиме ОЖИДАНИЕ)?", "Save this server in WAITING (Auto-Attach) mode?"),
                        I18n.Tr("Сохранение сервера", "Save Server"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (ask == MessageBoxResult.Yes)
                    {
                        StoreResultFilesystemRoots(filesystemRoots);
                        ResultServers.Clear();
                        ResultServers.Add(candidate);
                        DialogResult = true;
                        Close();
                    }
                }
            }
        }
        finally
        {
            _isVerifying = false;
            _verifyButton.IsEnabled = true;
            _verifyAndSaveButton.IsEnabled = true;
            _saveButton.IsEnabled = true;
        }
    }

    private static Border CreateCardBorder() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(34, 37, 42)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(44, 48, 54)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2),
        Padding = new Thickness(16, 14, 16, 14)
    };

    private static TextBlock CreateLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144))
    };

    private static TextBlock CreateStepLine(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11.5,
        Foreground = new SolidColorBrush(Color.FromRgb(214, 220, 226)),
        Margin = new Thickness(0, 3, 0, 3),
        TextWrapping = TextWrapping.Wrap
    };
}

public sealed class PresetNameDialog : Window
{
    private readonly TextBox _nameBox;
    public string PresetName => _nameBox.Text.Trim();

    public PresetNameDialog(string activeSummary)
    {
        Title = I18n.Tr("Сохранить пресет серверов", "Save Server Preset");
        Width = 440;
        Height = 230;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 29));
        Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239));

        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        stack.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Название пресета:", "Preset name:"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });

        _nameBox = new TextBox { Margin = new Thickness(0, 8, 0, 8), Text = I18n.Tr("Мой пресет", "My Preset") };
        stack.Children.Add(_nameBox);

        stack.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Включённые серверы: ", "Included servers: ") + activeSummary,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 144)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = I18n.Tr("Сохранить", "Save"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style pStyle)
            okBtn.Style = pStyle;
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameBox.Text)) return;
            DialogResult = true;
            Close();
        };

        var cancelBtn = new Button { Content = I18n.Tr("Отмена", "Cancel"), Padding = new Thickness(14, 7, 14, 7) };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        Content = stack;
        Loaded += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
    }
}

public sealed class FilesystemRenameDialog : Window
{
    private readonly TextBox _nameBox;
    public string ServerName => _nameBox.Text.Trim();

    public FilesystemRenameDialog(string currentName)
    {
        Title = I18n.Tr("Переименовать сервер файловой системы", "Rename Filesystem Server");
        Width = 420;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 29));
        Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239));

        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        stack.Children.Add(new TextBlock
        {
            Text = I18n.Tr("Отображаемое имя для Filesystem MCP:", "Display name for Filesystem MCP:"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });

        _nameBox = new TextBox { Margin = new Thickness(0, 10, 0, 16), Text = string.IsNullOrWhiteSpace(currentName) ? "Filesystem MCP" : currentName };
        stack.Children.Add(_nameBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = I18n.Tr("Сохранить", "Save"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("PrimaryButtonStyle") is Style pStyle)
            okBtn.Style = pStyle;
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameBox.Text)) return;
            DialogResult = true;
            Close();
        };

        var cancelBtn = new Button { Content = I18n.Tr("Отмена", "Cancel"), Padding = new Thickness(14, 7, 14, 7) };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        Content = stack;
        Loaded += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
    }
}