using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using McpBridge.Models;
using McpBridge.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using FormsProfessionalColorTable = System.Windows.Forms.ProfessionalColorTable;
using WpfClipboard = System.Windows.Clipboard;
using WpfControl = System.Windows.Controls.Control;
using WpfMessageBox = System.Windows.MessageBox;

namespace McpBridge;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly ObservableCollection<RootPathItemViewModel> _rootItems = new();
    private readonly ObservableCollection<McpServerStatusSnapshot> _serverItems = new();
    private bool _mcpSyncRunning;
    private bool _mcpSyncPending;
    private bool _pendingForceRetryQuarantined;
    private readonly HashSet<string> _pendingForceRetryServerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings;
    private readonly EditorMonitor _editorMonitor = new();
    private readonly McpInfrastructureService _mcpInfrastructure = new();
    private readonly TailscaleConnectionManager _tailscaleManager;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Dictionary<int, string> _observedEditors = new();
    private readonly Dictionary<int, string> _detectionErrors = new();
    private readonly Dictionary<FrameworkElement, double> _baseFontSizes = new();

    private string? _currentMcpUrl; private string? _currentLocalMcpUrl; private string _activeLogCategory = "connections";
    private bool _cleanupStarted;
    private bool _cleanupCompleted;
    private bool _exitRequested;
    private bool _changingAutoStartControl;
    private bool _fontScaleCommitPending;
    private bool _dependencyFooterWarning;
    private DependencyStatusView? _dependencyStatusView;
    private int _onlineServersCount;
    private int _enabledServersCount;
    private int _totalToolsCount;

    public MainWindow()
    {
        InitializeComponent();
        InitializeAppVersion();
        SourceInitialized += MainWindow_SourceInitialized;

        var brandWindowIcon = CreateBrandWindowIcon();
        if (brandWindowIcon != null) Icon = brandWindowIcon;
        TailscaleCheckButton.IsEnabled = true;
        var trayComponents = CreateTrayIcon();
        _trayIcon = trayComponents.Icon;
        _trayMenu = trayComponents.Menu;

        try
        {
            _settings = _settingsStore.Load(); I18n.CurrentLanguage = I18n.ResolveLanguage(_settings.Language); _settings.Language = I18n.CurrentLanguage;
            RebuildRootItemsFromSettings();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            ErrorLog.Write("MainWindow.LoadSettings", exception);
            WpfMessageBox.Show(this, exception.Message, I18n.Tr("Не удалось загрузить настройки", "Failed to load settings"), MessageBoxButton.OK, MessageBoxImage.Error);
            _settings = new AppSettings();
        }

        ErrorLog.Enabled = _settings.LoggingEnabled;

        _tailscaleManager = new TailscaleConnectionManager(_settings, SaveSettings);

        RootsListBox.ItemsSource = _rootItems;
        ServersManagerListControl.ItemsSource = _serverItems;
        PresetsItemsControl.ItemsSource = _settings.Presets;

        _mcpInfrastructure.StatusChanged += McpInfrastructure_StatusChanged;
        _tailscaleManager.StatusChanged += TailscaleConnectionManager_StatusChanged;        AutoStartCheckBox.IsChecked = AutoStartService.IsEnabled();
        _settings.RunAtWindowsStartup = AutoStartCheckBox.IsChecked == true;

        RefreshUrlState();
        UpdateTailscaleUi();

        FontScaleSlider.Value = Math.Max(0.85, Math.Min(1.35, _settings.FontScale <= 0 ? 1.0 : _settings.FontScale));
        if (LoggingEnabledCheckBox != null)
            LoggingEnabledCheckBox.IsChecked = _settings.LoggingEnabled;

        Loaded += async (_, _) =>
        {
            ApplyLanguage(); CaptureBaseFontSizes(this);
            ApplyFontScale(FontScaleSlider.Value);
            if (LoggingEnabledCheckBox != null)
                LoggingEnabledCheckBox.IsChecked = _settings.LoggingEnabled;
            ErrorLog.LogApp(I18n.Tr("MCP Bridge запущен.", "MCP Bridge started.")); ErrorLog.LogUpdated += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (MainPages.SelectedIndex == 2) RefreshLogs(); }), DispatcherPriority.Background); StartEditorMonitoring();
            UpdateTailscaleUi();
            await SyncMcpInfrastructureAsync(forceRetryQuarantined: true);
            _tailscaleManager.Start(_mcpInfrastructure.IsReady);
            var dependencyReport = McpDependencyResolver.GetCachedReport();
            if (dependencyReport != null && !dependencyReport.CoreReady)
            {
                FooterStatusText.Text = I18n.Tr("Нет зависимости: Node.js", "Missing dependency: Node.js");
            }
        };
    }

    private void RebuildRootItemsFromSettings()
    {
        _rootItems.Clear();
        var disabledSet = new HashSet<string>(
            (_settings.DisabledRoots ?? new List<string>()).Select(SettingsStore.NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var cleanedAllowed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawRoot in _settings.AllowedRoots ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(rawRoot)) continue;
            try
            {
                var normalized = SettingsStore.NormalizePath(rawRoot);
                if (!Directory.Exists(normalized) || !seen.Add(normalized)) continue;
                cleanedAllowed.Add(normalized);
                _rootItems.Add(new RootPathItemViewModel
                {
                    Path = normalized,
                    IsEnabled = !disabledSet.Contains(normalized)
                });
            }
            catch { }
        }

        if (cleanedAllowed.Count != (_settings.AllowedRoots?.Count ?? 0))
        {
            _settings.AllowedRoots = cleanedAllowed;
            _settings.DisabledRoots = _rootItems.Where(r => !r.IsEnabled).Select(r => r.Path).ToList();
            try { _settingsStore.Save(_settings); } catch { }
        }
    }

    private string[] GetEnabledRootPaths()
    {
        return _rootItems
            .Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => item.Path)
            .ToArray();
    }

    private (Forms.NotifyIcon Icon, Forms.ContextMenuStrip Menu) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = Drawing.Color.FromArgb(30, 33, 37),
            ForeColor = Drawing.Color.FromArgb(234, 236, 239),
            Renderer = new Forms.ToolStripProfessionalRenderer(new McpBridgeColorTable()),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(4),
            Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Regular),
            AutoSize = true,
            DropShadowEnabled = true
        };

        var openItem = new Forms.ToolStripMenuItem("Открыть MCP Bridge")
        {
            Padding = new Forms.Padding(12, 8, 18, 8),
            AutoSize = false,
            Width = 214,
            Height = 36,
            ForeColor = Drawing.Color.FromArgb(234, 236, 239)
        };
        openItem.Click += (_, _) => RestoreFromTray();

        var exitItem = new Forms.ToolStripMenuItem("Выход")
        {
            Padding = new Forms.Padding(12, 8, 18, 8),
            AutoSize = false,
            Width = 214,
            Height = 36,
            ForeColor = Drawing.Color.FromArgb(234, 236, 239)
        };
        exitItem.Click += (_, _) => ExitFromTray();

        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var icon = new Forms.NotifyIcon
        {
            Icon = CreateBrandTrayIcon(),
            Text = "MCP Bridge — оркестратор активен",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                RestoreFromTray();
        };

        return (icon, menu);
    }

    private static ImageSource? CreateBrandWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppPaths.InstallDirectory, "Resources", "MCPBridge.ico");
            if (!File.Exists(iconPath)) return null;
            return BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { return null; }
    }

    private static Drawing.Icon CreateBrandTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppPaths.InstallDirectory, "Resources", "MCPBridge.ico");
            if (!File.Exists(iconPath)) throw new FileNotFoundException("MCP Bridge icon is missing.", iconPath);
            using var source = new Drawing.Icon(iconPath);
            return (Drawing.Icon)source.Clone();
        }
        catch
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        const uint MonitorDefaultToNearest = 2;
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        info.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        info.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
        info.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleWindowState();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CaptureBaseFontSizes(DependencyObject parent)
    {
        if (parent is WpfControl control && !ReferenceEquals(control, FontScaleSlider) && !_baseFontSizes.ContainsKey(control))
            _baseFontSizes[control] = control.FontSize;
        else if (parent is TextBlock textBlock && !_baseFontSizes.ContainsKey(textBlock))
            _baseFontSizes[textBlock] = textBlock.FontSize;
        if (parent is not Visual && parent is not System.Windows.Media.Media3D.Visual3D) return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            CaptureBaseFontSizes(VisualTreeHelper.GetChild(parent, i));
    }

    private void ApplyFontScale(double scale)
    {
        foreach (var item in _baseFontSizes)
        {
            var scaledSize = Math.Max(8.0, item.Value * scale);
            if (item.Key is WpfControl control) control.FontSize = scaledSize;
            else if (item.Key is TextBlock textBlock) textBlock.FontSize = scaledSize;
        }
    }

    private void FontScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontScaleValueText == null) return;
        FontScaleValueText.Text = Math.Round(e.NewValue * 100).ToString("0") + "%";
        if (_baseFontSizes.Count > 0)
            ApplyFontScale(e.NewValue);
        _fontScaleCommitPending = true;
    }

    private void ResetScale_Click(object sender, RoutedEventArgs e)
    {
        FontScaleSlider.Value = 1.0;
        CommitFontScale();
    }

    private void FontScaleSlider_Commit(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitFontScale();
    private void FontScaleSlider_CommitKeyUp(object sender, System.Windows.Input.KeyEventArgs e) => CommitFontScale();

    private void CommitFontScale()
    {
        if (!_fontScaleCommitPending || _settings == null) return;
        _fontScaleCommitPending = false;
        ApplyFontScale(FontScaleSlider.Value);
        _settings.FontScale = FontScaleSlider.Value;
        SaveSettings();
    }

    private void LangRu_Click(object sender, RoutedEventArgs e) => SetLanguage("ru");
    private void LangEn_Click(object sender, RoutedEventArgs e) => SetLanguage("en");

    private void SetLanguage(string lang)
    {
        I18n.CurrentLanguage = lang;
        _settings.Language = lang;
        SaveSettings();
        ApplyLanguage();
        RebuildRootItemsFromSettings();
        _mcpInfrastructure.RefreshLocalizedStatusSnapshot();
        McpDependencyResolver.InvalidateCache();
        _ = _dependencyStatusView?.RefreshLocalizedReportAsync();
        ErrorLog.LogApp(I18n.Tr("Язык интерфейса изменён на русский.", "Interface language changed to English."));
    }

    private string GetFilesystemDisplayName()
    {
        return (_settings.FilesystemMcpCustom != null && !string.IsNullOrWhiteSpace(_settings.FilesystemMcpCustom.Name))
            ? _settings.FilesystemMcpCustom.Name!
            : "Filesystem MCP";
    }

    private void ApplyLanguage()
    {
        var en = I18n.IsEnglish;
        _dependencyStatusView?.ApplyLanguage();
        var primaryStyle = TryFindResource("PrimaryButtonStyle") as Style;
        var defaultStyle = TryFindResource(typeof(Button)) as Style;
        if (LangRuButton != null && LangEnButton != null)
        {
            LangRuButton.Style = !en ? primaryStyle : defaultStyle;
            LangEnButton.Style = en ? primaryStyle : defaultStyle;
        }

        TopStatusLabelText.Text = I18n.Tr("СТАТУС:", "STATUS:");
        FooterStatusText.Text = I18n.Tr("Готово", "Ready");

        NavDashTitleText.Text = I18n.Tr("Главная", "Dashboard");
        NavDashSubText.Text = I18n.Tr("Сводка и статус", "Overview & status");
        NavSrvTitleText.Text = I18n.Tr("MCP-сервисы", "MCP Services");
        NavSrvSubText.Text = I18n.Tr("Оркестратор и пулы", "Orchestrator & pools");
        NavLogsTitleText.Text = I18n.Tr("Логи", "Logs");
        NavLogsSubText.Text = I18n.Tr("Подключения и система", "Connections & system");
        NavSetTitleText.Text = I18n.Tr("Настройки", "Settings");
        NavSetSubText.Text = I18n.Tr("Разделы настроек", "Settings sections");
        ComponentsSettingsNavText.Text = I18n.Tr("Компоненты", "Components");

        Kpi1TitleText.Text = I18n.Tr("Активные сервисы", "Active Services");
        Kpi1SubText.Text = I18n.Tr("Динамический шлюз", "Dynamic gateway");
        Kpi2TitleText.Text = "Local MCP Bridge";
        Kpi2SubText.Text = I18n.Tr("Локальный адрес", "Local endpoint");
        Kpi3TitleText.Text = I18n.Tr("Онлайн-подключение", "Online connection");
        Kpi3SubText.Text = "Tailscale Funnel";

        DashServicesTitleText.Text = I18n.Tr("Состояние MCP-сервисов", "MCP Services Status");
        DashServicesSubText.Text = I18n.Tr("Текущий статус подключений", "Current connection states");
        DashManageBtn.Content = I18n.Tr("Управление серверами →", "Manage Servers →");
        DashColServiceText.Text = I18n.Tr("СЕРВИС", "SERVICE");
        DashColStatusText.Text = I18n.Tr("СТАТУС", "STATUS");

        DashUeSubText.Text = I18n.Tr("Обнаруженные сессии", "Detected sessions");
        DashFoldersBtn.Content = I18n.Tr("Разрешённые папки →", "Allowed Folders →");
        DashUeProcLabel.Text = I18n.Tr("ПРОЦЕСС РЕДАКТОРА", "EDITOR PROCESS");
        DashUeProjLabel.Text = I18n.Tr("ПУТЬ ПРОЕКТА (.UPROJECT)", "PROJECT PATH (.UPROJECT)");

        DashTunnelCardTitleText.Text = I18n.Tr("Внешнее подключение", "External Connection");
        DashTunnelCardSubText.Text = I18n.Tr("Tailscale Funnel", "Tailscale Funnel");
        DashTunnelStateLabel.Text = I18n.Tr("СОСТОЯНИЕ ТУННЕЛЯ", "TUNNEL STATE");
        DashTunnelUrlLabel.Text = I18n.Tr("АДРЕС ПОДКЛЮЧЕНИЯ (URL)", "CONNECTION ADDRESS (URL)");
        DashCopyUrlBtn.Content = I18n.Tr("Копировать", "Copy");

        PresetsTitleText.Text = I18n.Tr("Пресеты наборов серверов", "Server Presets");
        PresetsSubText.Text = I18n.Tr("Быстрое переключение профилей в 1 клик. Крестик справа от пресета удаляет его.", "1-click profile switching without dropping active sessions. Click ✕ to delete a preset.");
        SavePresetBtn.Content = I18n.Tr("＋ Сохранить пресет", "＋ Save Preset");
        AddServerBtn.Content = I18n.Tr("＋ Добавить сервер", "＋ Add Server");

        SrvTableTitleText.Text = I18n.Tr("Управление MCP-серверами и техническая сводка", "MCP Servers Management & Technical Summary");
        SrvTableSubText.Text = I18n.Tr("Включайте, редактируйте или удаляйте серверы. Выключенные STDIO-процессы полностью останавливаются.", "Toggle, edit, or remove servers. Disabled STDIO processes are stopped to free RAM.");
        SrvColOnText.Text = I18n.Tr("ВКЛ", "ON");
        SrvColNameText.Text = I18n.Tr("СЕРВИС И НАЗНАЧЕНИЕ", "SERVICE & PURPOSE");
        SrvColPortText.Text = I18n.Tr("ПОРТ / ПУТЬ", "PORT / PATH");
        SrvColStatusText.Text = I18n.Tr("СТАТУС", "STATUS");
        SrvColToolsText.Text = I18n.Tr("ИНСТРУМЕНТЫ", "TOOLS");
        SrvColActionsText.Text = I18n.Tr("ДЕЙСТВИЯ", "ACTIONS");

        LogTabConnectionsButton.Content = I18n.Tr("Подключения и MCP", "Connections & MCP");
        LogTabAppButton.Content = I18n.Tr("Приложение", "Application");
        LogTabErrorsButton.Content = I18n.Tr("Ошибки (errors.log)", "Errors (errors.log)");
        LogTabVerificationButton.Content = I18n.Tr("Верификация", "Verification");
        if (LoggingEnabledCheckBox != null)
            LoggingEnabledCheckBox.Content = I18n.Tr("Запись логов включена", "Log recording enabled");
        if (LoggingEnabledHintText != null)
            LoggingEnabledHintText.Text = I18n.Tr("По умолчанию выключено. Включение/выключение действует сразу.", "Off by default. Toggle takes effect immediately.");
        ClearLogsBtn.Content = I18n.Tr("Очистить", "Clear");
        RefreshLogsBtn.Content = I18n.Tr("Обновить", "Refresh");
        OpenLogsFolderBtn.Content = I18n.Tr("Открыть папку Logs", "Open Logs Folder");

        SettingsNavigationTitleText.Text = I18n.Tr("РАЗДЕЛЫ НАСТРОЕК", "SETTINGS SECTIONS");
        TechnicalSettingsNavText.Text = I18n.Tr("Технические настройки и подключения", "Technical & connection settings");
        InterfaceSettingsNavText.Text = I18n.Tr("Интерфейс и приложение", "Interface & application");

        RootsSectionTitleText.Text = I18n.Tr("Разрешённые папки (", "Allowed Folders (") + GetFilesystemDisplayName() + ")";
        RenameFsBtn.Content = I18n.Tr("✎ Настроить", "✎ Configure");
        RootsSectionSubText.Text = I18n.Tr(
            "Отмеченные галочкой папки доступны для чтения и записи. Снятие галочки мгновенно отключает доступ к папке без перезапуска роутера.",
            "Checked folders are accessible for reading and writing. Unchecking a folder immediately revokes access without restarting the router.");
        AddRootBtn.Content = I18n.Tr("＋  Добавить папку", "＋  Add Folder");
        RemoveRootBtn.Content = I18n.Tr("Удалить выбранную", "Remove Selected");

        TunnelSettingsTitleText.Text = I18n.Tr("Онлайн-доступ", "Online access");
        TunnelSettingsSubText.Text = I18n.Tr(
            "Tailscale уже установлен, но не запускается автоматически. Онлайн-доступ необязателен: без него MCP Bridge продолжает полностью работать локально.",
            "Tailscale is already installed but is not started automatically. Online access is optional: MCP Bridge continues to work fully in local mode without it.");
        PublicEndpointTitleText.Text = I18n.Tr("Адреса подключения", "Connection URLs");
        PublicEndpointSubText.Text = I18n.Tr(
            "Локальный и публичный адреса Bridge MCP.",
            "Local and public Bridge MCP addresses.");
        TailscaleAuthStateEyebrowText.Text = I18n.Tr("РЕЖИМ РАБОТЫ", "OPERATING MODE");
        TailscaleCheckButton.Content = I18n.Tr("Подключить", "Connect");
        if (TailscaleHelpTitleText != null)
        {
            TailscaleHelpTitleText.Text = I18n.Tr("ОНЛАЙН-ДОСТУП ЧЕРЕЗ TAILSCALE", "ONLINE ACCESS VIA TAILSCALE");
            TailscaleHelpIntroText.Text = I18n.Tr(
                "Подключение необязательно. Без него MCP Bridge продолжает работать с локальными MCP.",
                "Connection is optional. MCP Bridge continues to work with local MCP servers without it.");
            TailscaleHelpStep1Text.Text = I18n.Tr(
                "1. Нажмите «Подключить». Bridge откроет приложение Tailscale. Войдите в аккаунт и подключите устройство кнопкой Connect.",
                "1. Click 'Connect'. Bridge will open the Tailscale app. Sign in to your account and connect the device using the Connect button.");
            TailscaleHelpStep2Text.Text = I18n.Tr(
                "2. Если потребуется, подтвердите устройство в Admin Console → Machines. Затем разрешите Funnel в открывшемся окне браузера.",
                "2. If required, approve the device in Admin Console → Machines. Then approve Funnel in the browser window that opens.");
            TailscaleHelpStep3Text.Text = I18n.Tr(
                "3. Bridge дождётся подтверждений и сам запустит публичный HTTPS endpoint. Нажимать «Подключить» между этапами не нужно.",
                "3. Bridge will wait for the approvals and automatically start the public HTTPS endpoint. No need to click 'Connect' again between steps.");
            TailscaleHelpWarnText.Text = I18n.Tr(
                "Важно: публичный URL доступен всем, кто его знает. Ограничьте разрешённые Filesystem roots только нужными папками.",
                "Important: the public URL is accessible to anyone who knows it. Restrict allowed Filesystem roots to only the folders you need.");
        }
        TailscaleCheckButton.ToolTip = I18n.Tr(
            "Один сценарий: вход в Tailscale, подключение устройства, первоначальная настройка, отдельное разрешение Funnel и проверка публичного HTTPS. Потребуется подтверждение Windows.",
            "One guided flow: sign in to Tailscale, connect the device, perform initial setup, approve Funnel if required, and verify public HTTPS. Windows approval is required.");
        LocalUrlEyebrowText.Text = I18n.Tr("ЛОКАЛЬНЫЙ URL", "LOCAL URL");
        CopyLocalUrlButton.Content = I18n.Tr("Копировать URL", "Copy URL");
        TunnelUrlEyebrowText.Text = I18n.Tr("ПУБЛИЧНЫЙ URL", "PUBLIC URL");
        CopyUrlButton.Content = I18n.Tr("Копировать URL", "Copy URL");
        CopyUrlButton.ToolTip = I18n.Tr(
            "Копирует URL активного Funnel. Не включает публикацию.",
            "Copies the active Funnel URL. It does not enable publishing.");
        UrlHintText.Text = I18n.Tr(
            "Публичный URL появится после активации Funnel. Копирование адреса не меняет состояние endpoint.",
            "The public URL appears after Funnel becomes active. Copying it does not change endpoint state.");


        LangCardTitleText.Text = I18n.Tr("Язык интерфейса / Language", "Interface Language / Язык");
        LangCardSubText.Text = I18n.Tr("Переключение языка интерфейса в реальном времени без перезапуска приложения.", "Switch the interface language in real time without restarting the application.");
        ScaleCardTitleText.Text = I18n.Tr("Масштаб интерфейса", "Interface Scale");
        ScaleCardSubText.Text = I18n.Tr("Плавная регулировка размера текста и элементов во всём окне (85% — 135%).", "Smoothly adjust text and UI element sizing across the entire window (85% — 135%).");
        BgCardTitleText.Text = I18n.Tr("Автозапуск и работа в фоне", "Startup & Background Mode");
        AutoStartCheckBox.Content = I18n.Tr("Запускать MCP Bridge вместе с Windows", "Launch MCP Bridge at Windows startup");
        BgCardSubText.Text = I18n.Tr("Закрытие окна на крестик сворачивает приложение в системный трей. Для полного выхода используйте меню трея.", "Closing the window minimizes the application to the system tray. Use the tray menu to exit completely.");

        if (_trayMenu.Items.Count >= 3)
        {
            _trayMenu.Items[0].Text = I18n.Tr("Открыть MCP Bridge", "Open MCP Bridge");
            _trayMenu.Items[2].Text = I18n.Tr("Выход", "Exit");
        }
        _trayIcon.Text = I18n.Tr("MCP Bridge — оркестратор активен", "MCP Bridge — orchestrator active");

        if (PresetsItemsControl != null)
        {
            PresetsItemsControl.ItemsSource = null;
            PresetsItemsControl.ItemsSource = _settings.Presets;
        }

        UpdateTopRuntimeBadge();
        RefreshEditorDisplay();
        UpdateTailscaleUi();
        SwitchLogCategory(_activeLogCategory);
    }

    private async Task SyncMcpInfrastructureAsync(bool forceRetryQuarantined = false, string? forceRetryServerId = null)
    {
        if (_cleanupStarted || _cleanupCompleted)
        {
            return;
        }

        if (forceRetryQuarantined)
            _pendingForceRetryQuarantined = true;
        if (!string.IsNullOrWhiteSpace(forceRetryServerId))
            _pendingForceRetryServerIds.Add(forceRetryServerId!);

        _mcpSyncPending = true;
        if (_mcpSyncRunning)
        {
            return;
        }

        _mcpSyncRunning = true;
        try
        {
            while (_mcpSyncPending && !_cleanupStarted && !_cleanupCompleted)
            {
                _mcpSyncPending = false;
                var retryQuarantined = _pendingForceRetryQuarantined;
                _pendingForceRetryQuarantined = false;

                string? retryServerId = null;
                if (_pendingForceRetryServerIds.Count == 1)
                {
                    retryServerId = _pendingForceRetryServerIds.First();
                }
                else if (_pendingForceRetryServerIds.Count > 1)
                {
                    retryQuarantined = true;
                }
                _pendingForceRetryServerIds.Clear();

                await _mcpInfrastructure.SyncStateAsync(
                    _settings,
                    GetEnabledRootPaths(),
                    retryQuarantined,
                    retryServerId);

                _tailscaleManager.NotifyGatewayAvailability(_mcpInfrastructure.IsReady);
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("MainWindow.SyncMcpInfrastructureAsync", exception);
            FooterStatusText.Text = I18n.Tr("Ошибка синхронизации MCP-шлюза: ", "MCP gateway sync error: ") + exception.Message + I18n.Tr(" (см. Логи)", " (see Logs)");
        }
        finally
        {
            _mcpSyncRunning = false;
        }
    }

    // =========================================================================
    // УПРАВЛЕНИЕ СЕРВЕРАМИ И ПРЕСЕТАМИ (ВКЛАДКА MCP-СЕРВИСЫ)
    // =========================================================================

    private async void ServerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted)
        {
            return;
        }
        if (sender is not CheckBox checkBox || checkBox.Tag is not string serverId)
        {
            return;
        }

        var isEnabled = checkBox.IsChecked == true;
        if (string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
        {
            _settings.UnrealMcpEnabled = isEnabled;
        }
        else if (string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
        {
            _settings.FilesystemMcpEnabled = isEnabled;
        }
        else
        {
            var custom = _settings.CustomServers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (custom != null) custom.Enabled = isEnabled;
        }

        if (!isEnabled)
            _pendingForceRetryServerIds.Remove(serverId);

        _mcpInfrastructure.SetServerEnabledImmediate(serverId, isEnabled);
        _settings.ActivePresetId = null;
        var saved = SaveSettings();
        if (!saved) return;

        ErrorLog.LogApp(I18n.Tr(
            (isEnabled ? "Включён" : "Отключён") + " сервер «" + serverId + "».",
            (isEnabled ? "Enabled" : "Disabled") + " server '" + serverId + "'."));
        FooterStatusText.Text = I18n.Tr("Обновление конфигурации роутера (:8001)…", "Updating router (:8001) configuration…");

        await SyncMcpInfrastructureAsync(forceRetryServerId: isEnabled ? serverId : null);
        FooterStatusText.Text = I18n.Tr("Роутер (:8001) обновлён без разрыва внешней сессии.", "Router (:8001) updated without dropping active sessions.");
    }

    private async void AddCustomServer_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;

        var dialog = new ServerJsonEditorWindow(_mcpInfrastructure) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultServers.Count == 0)
            return;

        var addedCount = 0;
        foreach (var candidate in dialog.ResultServers)
        {
            var baseId = string.IsNullOrWhiteSpace(candidate.Id) ? "custom_mcp" : SettingsStore.SanitizeServerId(candidate.Id);
            if (string.IsNullOrWhiteSpace(baseId)) baseId = "custom_mcp";

            var isBuiltInDup = string.Equals(baseId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(baseId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase) ||
                               (candidate.Args != null && candidate.Args.Any(a => a != null && a.IndexOf("server-filesystem", StringComparison.OrdinalIgnoreCase) >= 0));
            var existingDup = _settings.CustomServers.FirstOrDefault(s =>
                string.Equals(s.Id, baseId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(candidate.Transport, "http", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(s.Url) &&
                 string.Equals(s.Url.Trim().TrimEnd('/'), (candidate.Url ?? string.Empty).Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));

            if (isBuiltInDup || existingDup != null)
            {
                FooterStatusText.Text = I18n.Tr(
                    "Сервер «" + candidate.Name + "» уже есть в списке и не был продублирован.",
                    "Server '" + candidate.Name + "' is already in the list and was not duplicated.");
                continue;
            }

            candidate.Id = baseId;
            _settings.CustomServers.Add(candidate);
            addedCount++;
        }
        if (addedCount == 0) return;

        if (!SaveSettings()) return;

        ErrorLog.LogApp(I18n.Tr("Добавлено серверов: ", "Servers added: ") + dialog.ResultServers.Count + ".");
        FooterStatusText.Text = I18n.Tr("Добавлено серверов: ", "Servers added: ") + dialog.ResultServers.Count;
        await SyncMcpInfrastructureAsync(forceRetryQuarantined: true);
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        if (sender is not FrameworkElement element || element.Tag is not string serverId) return;

        if (string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
        {
            var unrealEntry = _settings.UnrealMcpCustom?.Clone() ?? new CustomMcpServerEntry
            {
                Id = SettingsStore.BuiltInUnrealId,
                Name = "Unreal MCP",
                Transport = "http",
                Url = "http://127.0.0.1:8000/mcp",
                Enabled = _settings.UnrealMcpEnabled != false
            };
            unrealEntry.Enabled = _settings.UnrealMcpEnabled != false;
            var unrealDialog = new ServerJsonEditorWindow(_mcpInfrastructure, unrealEntry) { Owner = this };
            if (unrealDialog.ShowDialog() != true || unrealDialog.ResultServers.Count == 0) return;
            var updatedUnreal = unrealDialog.ResultServers[0];
            updatedUnreal.Id = SettingsStore.BuiltInUnrealId;
            var previousUnrealCustom = _settings.UnrealMcpCustom;
            var previousUnrealEnabled = _settings.UnrealMcpEnabled;
            _settings.UnrealMcpCustom = updatedUnreal;
            _settings.UnrealMcpEnabled = updatedUnreal.Enabled;
            if (!SaveSettings())
            {
                _settings.UnrealMcpCustom = previousUnrealCustom;
                _settings.UnrealMcpEnabled = previousUnrealEnabled;
                return;
            }
            ErrorLog.LogApp(I18n.Tr("Изменены настройки сервера «" + updatedUnreal.Name + "».", "Updated settings for server '" + updatedUnreal.Name + "'."));
            await SyncMcpInfrastructureAsync(forceRetryServerId: SettingsStore.BuiltInUnrealId);
            return;
        }

        if (string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
        {
            await EditFilesystemServerAsync();
            return;
        }

        var existing = _settings.CustomServers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        var dialog = new ServerJsonEditorWindow(_mcpInfrastructure, existing.Clone()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultServers.Count == 0) return;

        var updated = dialog.ResultServers[0];
        updated.Id = existing.Id;
        var index = _settings.CustomServers.IndexOf(existing);
        if (index >= 0) _settings.CustomServers[index] = updated;

        if (!SaveSettings()) return;

        ErrorLog.LogApp(I18n.Tr("Настройки сервера «" + updated.Name + "» сохранены.", "Saved settings for server '" + updated.Name + "'."));
        FooterStatusText.Text = I18n.Tr("Настройки сервера сохранены: ", "Server settings saved: ") + updated.Name;
        await SyncMcpInfrastructureAsync(forceRetryServerId: updated.Id);
    }

    private async void RenameFilesystem_Click(object sender, RoutedEventArgs e)
    {
        await EditFilesystemServerAsync();
    }

    private async Task EditFilesystemServerAsync()
    {
        if (_cleanupStarted || _cleanupCompleted) return;

        var existing = _settings.FilesystemMcpCustom?.Clone() ?? new CustomMcpServerEntry
        {
            Id = SettingsStore.BuiltInFilesystemId,
            Name = GetFilesystemDisplayName(),
            Transport = "stdio",
            Command = "node"
        };
        existing.Id = SettingsStore.BuiltInFilesystemId;
        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? GetFilesystemDisplayName() : existing.Name;
        existing.Transport = "stdio";
        existing.Command = string.IsNullOrWhiteSpace(existing.Command) ? "node" : existing.Command;
        existing.Url = null;
        existing.Enabled = _settings.FilesystemMcpEnabled != false;

        var dialog = new ServerJsonEditorWindow(_mcpInfrastructure, existing, requiresStdio: true, filesystemRoots: _rootItems) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultServers.Count == 0) return;

        var updated = dialog.ResultServers[0];
        updated.Id = SettingsStore.BuiltInFilesystemId;
        updated.Transport = "stdio";
        updated.Url = null;

        var previousCustom = _settings.FilesystemMcpCustom;
        var previousEnabled = _settings.FilesystemMcpEnabled;
        var previousDeleted = _settings.FilesystemMcpDeleted;
        var previousRootItems = _rootItems.Select(item => new RootPathItemViewModel
        {
            Path = item.Path,
            IsEnabled = item.IsEnabled
        }).ToList();
        var previousAllowedRoots = (_settings.AllowedRoots ?? new List<string>()).ToList();
        var previousDisabledRoots = (_settings.DisabledRoots ?? new List<string>()).ToList();

        _settings.FilesystemMcpCustom = updated;
        _settings.FilesystemMcpEnabled = updated.Enabled;
        _settings.FilesystemMcpDeleted = false;

        _rootItems.Clear();
        foreach (var root in dialog.ResultFilesystemRoots)
        {
            _rootItems.Add(new RootPathItemViewModel
            {
                Path = root.Path,
                IsEnabled = root.IsEnabled
            });
        }

        if (!SaveSettings())
        {
            _settings.FilesystemMcpCustom = previousCustom;
            _settings.FilesystemMcpEnabled = previousEnabled;
            _settings.FilesystemMcpDeleted = previousDeleted;
            _settings.AllowedRoots = previousAllowedRoots;
            _settings.DisabledRoots = previousDisabledRoots;
            _rootItems.Clear();
            foreach (var root in previousRootItems)
                _rootItems.Add(root);
            return;
        }

        RootsSectionTitleText.Text = I18n.Tr("Разрешённые папки (", "Allowed Folders (") + GetFilesystemDisplayName() + ")";
        ErrorLog.LogApp(I18n.Tr("Сохранены настройки Filesystem MCP.", "Saved Filesystem MCP settings."));
        FooterStatusText.Text = I18n.Tr("Настройки Filesystem MCP сохранены.", "Filesystem MCP settings saved.");
        await SyncMcpInfrastructureAsync(forceRetryServerId: SettingsStore.BuiltInFilesystemId);
    }

    private bool ShowStyledDeleteConfirmation(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MinHeight = 190,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 29)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239)),
            ShowInTaskbar = false
        };

        WindowChrome.SetWindowChrome(dialog, new WindowChrome
        {
            CaptionHeight = 42,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topBar = new Border
        {
            Background = TryFindResource("TopBarBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(22, 25, 29)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(20, 22, 25)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        topGrid.Children.Add(titleText);

        var closeButton = new Button
        {
            Style = TryFindResource("ChromeCloseButtonStyle") as Style,
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
                Stroke = new SolidColorBrush(Color.FromRgb(140, 148, 158)),
                StrokeThickness = 1.3
            }
        };
        WindowChrome.SetIsHitTestVisibleInChrome(closeButton, true);
        closeButton.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        Grid.SetColumn(closeButton, 1);
        topGrid.Children.Add(closeButton);
        topBar.Child = topGrid;
        root.Children.Add(topBar);

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(234, 236, 239)),
            FontSize = 13,
            Margin = new Thickness(20, 18, 20, 18)
        };
        Grid.SetRow(messageText, 1);
        root.Children.Add(messageText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 18)
        };
        Grid.SetRow(buttons, 2);

        var cancelButton = new Button
        {
            Content = I18n.Tr("Отмена", "Cancel"),
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
            IsDefault = true
        };
        var deleteButton = new Button
        {
            Content = I18n.Tr("Удалить", "Remove"),
            Padding = new Thickness(16, 7, 16, 7),
            Style = TryFindResource("PrimaryButtonStyle") as Style,
            IsDefault = false
        };
        deleteButton.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(deleteButton);
        root.Children.Add(buttons);

        dialog.Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 54, 61)),
            BorderThickness = new Thickness(1),
            Child = root
        };

        return dialog.ShowDialog() == true;
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        if (sender is not FrameworkElement element || element.Tag is not string serverId) return;

        if (string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
        {
            if (!ShowStyledDeleteConfirmation(
                    I18n.Tr("Удалить MCP-сервер", "Remove MCP Server"),
                    I18n.Tr("Удалить «Unreal MCP» из списка серверов?", "Remove 'Unreal MCP' from the server list?")))
                return;
            _settings.UnrealMcpDeleted = true;
            _settings.UnrealMcpEnabled = false;
            if (!SaveSettings()) return;
            ErrorLog.LogApp(I18n.Tr("Сервер «Unreal MCP» удалён.", "Server 'Unreal MCP' removed."));
            await SyncMcpInfrastructureAsync();
            return;
        }

        var existing = _settings.CustomServers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        var confirm = ShowStyledDeleteConfirmation(
            I18n.Tr("Удаление MCP-сервера", "Remove MCP Server"),
            I18n.Tr(
                "Удалить MCP-сервер «" + existing.Name + "» из моста?\nЕго дочерний процесс будет немедленно остановлен.",
                "Remove MCP server '" + existing.Name + "'?\nIts child process will be stopped immediately."));

        if (!confirm) return;

        _settings.CustomServers.Remove(existing);
        foreach (var preset in _settings.Presets)
            preset.EnabledServerIds.RemoveAll(id => string.Equals(id, existing.Id, StringComparison.OrdinalIgnoreCase));

        if (!SaveSettings()) return;

        ErrorLog.LogApp(I18n.Tr("Сервер «" + existing.Name + "» удалён.", "Server '" + existing.Name + "' removed."));
        FooterStatusText.Text = I18n.Tr("Сервер удалён: ", "Server removed: ") + existing.Name;
        await SyncMcpInfrastructureAsync();
    }

    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        if (sender is not FrameworkElement element || element.Tag is not string presetId) return;

        var preset = _settings.Presets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null) return;

        var enableAll = preset.EnabledServerIds.Contains("*");
        var enabledSet = new HashSet<string>(preset.EnabledServerIds, StringComparer.OrdinalIgnoreCase);

        if (!_settings.UnrealMcpDeleted)
            _settings.UnrealMcpEnabled = enableAll || enabledSet.Contains(SettingsStore.BuiltInUnrealId);
        if (!_settings.FilesystemMcpDeleted)
            _settings.FilesystemMcpEnabled = enableAll || enabledSet.Contains(SettingsStore.BuiltInFilesystemId);

        foreach (var srv in _settings.CustomServers)
            srv.Enabled = enableAll || enabledSet.Contains(srv.Id);

        _settings.ActivePresetId = preset.Id;
        if (!SaveSettings()) return;

        ErrorLog.LogApp(I18n.Tr("Активирован пресет «" + preset.DisplayName + "».", "Activated preset '" + preset.DisplayName + "'."));
        FooterStatusText.Text = I18n.Tr("Применяю пресет «" + preset.DisplayName + "»…", "Applying preset '" + preset.DisplayName + "'…");
        await SyncMcpInfrastructureAsync(forceRetryQuarantined: true);
        FooterStatusText.Text = I18n.Tr("Пресет «" + preset.DisplayName + "» активен.", "Preset '" + preset.DisplayName + "' is active.");
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        if (sender is not FrameworkElement element || element.Tag is not string presetId) return;

        var preset = _settings.Presets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null) return;

        if (WpfMessageBox.Show(
                this,
                I18n.Tr("Удалить пресет «" + preset.DisplayName + "»?", "Delete preset '" + preset.DisplayName + "'?"),
                I18n.Tr("Удаление пресета", "Delete Preset"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _settings.Presets.Remove(preset);
        _settings.PresetsInitialized = true;
        SaveSettings();
        PresetsItemsControl.ItemsSource = null;
        PresetsItemsControl.ItemsSource = _settings.Presets;
        ErrorLog.LogApp(I18n.Tr("Удалён пресет «" + preset.DisplayName + "».", "Deleted preset '" + preset.DisplayName + "'."));
        FooterStatusText.Text = I18n.Tr("Пресет «" + preset.DisplayName + "» удалён.", "Preset '" + preset.DisplayName + "' deleted.");
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;

        var enabledIds = new List<string>();
        var enabledNames = new List<string>();

        if (!_settings.UnrealMcpDeleted && _settings.UnrealMcpEnabled != false)
        {
            enabledIds.Add(SettingsStore.BuiltInUnrealId);
            enabledNames.Add(_settings.UnrealMcpCustom?.Name ?? "Unreal MCP");
        }
        if (!_settings.FilesystemMcpDeleted && _settings.FilesystemMcpEnabled != false)
        {
            enabledIds.Add(SettingsStore.BuiltInFilesystemId);
            enabledNames.Add(_settings.FilesystemMcpCustom?.Name ?? "Filesystem MCP");
        }
        foreach (var srv in _settings.CustomServers.Where(s => s.Enabled))
        {
            enabledIds.Add(srv.Id);
            enabledNames.Add(srv.Name);
        }

        var summary = enabledNames.Count == 0
            ? I18n.Tr("нет включённых серверов", "no enabled servers")
            : string.Join(", ", enabledNames);
        var dialog = new PresetNameDialog(summary) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.PresetName))
            return;

        var existing = _settings.Presets.FirstOrDefault(p => string.Equals(p.Name, dialog.PresetName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.EnabledServerIds = enabledIds;
            _settings.ActivePresetId = existing.Id;
        }
        else
        {
            var newPreset = new McpPresetEntry
            {
                Id = "preset_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                Name = dialog.PresetName,
                EnabledServerIds = enabledIds,
                IsBuiltIn = false
            };
            _settings.Presets.Add(newPreset);
            _settings.ActivePresetId = newPreset.Id;
        }

        _settings.PresetsInitialized = true;
        SaveSettings();
        PresetsItemsControl.ItemsSource = null;
        PresetsItemsControl.ItemsSource = _settings.Presets;
        ErrorLog.LogApp(I18n.Tr("Сохранён пресет «" + dialog.PresetName + "».", "Saved preset '" + dialog.PresetName + "'."));
        FooterStatusText.Text = I18n.Tr("Пресет «" + dialog.PresetName + "» сохранён.", "Preset '" + dialog.PresetName + "' saved.");
    }

    // =========================================================================
    // РАЗРЕШЁННЫЕ ПАПКИ С ГАЛОЧКАМИ (FILESYSTEM MCP)
    // =========================================================================

    private async void RootToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        RootPathItemViewModel? toggledItem = null;
        if (sender is CheckBox checkBox && checkBox.DataContext is RootPathItemViewModel item)
        {
            toggledItem = item;
            toggledItem.IsEnabled = checkBox.IsChecked == true;
        }
        if (!SaveSettings()) return;

        if (toggledItem != null)
        {
            ErrorLog.LogApp(I18n.Tr(
                (toggledItem.IsEnabled ? "Включён доступ к папке: " : "Отключён доступ к папке: ") + toggledItem.Path,
                (toggledItem.IsEnabled ? "Enabled folder access: " : "Disabled folder access: ") + toggledItem.Path));
        }
        FooterStatusText.Text = I18n.Tr(
            "Обновляю список активных папок в Filesystem MCP (:8003)…",
            "Updating active folders in Filesystem MCP (:8003)…");
        await ApplyMcpRootsAsync();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        string? selectedPath;

        try
        {
            selectedPath = NativeFolderPicker.Show(
                this,
                I18n.Tr(
                    "Выберите корневую папку для доступа через Filesystem MCP",
                    "Select a root folder for Filesystem MCP access"));
        }
        catch (COMException exception)
        {
            ErrorLog.Write("MainWindow.AddRoot_FolderPicker", exception);
            WpfMessageBox.Show(
                this,
                exception.Message,
                I18n.Tr("Не удалось открыть выбор папки", "Unable to open folder picker"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (selectedPath == null || string.IsNullOrWhiteSpace(selectedPath))
            return;

        try
        {
            var fullPath = NormalizeDirectory(selectedPath);
            if (!Directory.Exists(fullPath))
            {
                WpfMessageBox.Show(
                    this,
                    I18n.Tr("Выбранная папка не существует.", "The selected folder does not exist."),
                    I18n.Tr("Проверка пути", "Path Check"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var existing = _rootItems.FirstOrDefault(r => string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsEnabled = true;
                SaveSettings();
                ErrorLog.LogApp(I18n.Tr("Включён доступ к папке: " + fullPath, "Enabled folder access: " + fullPath));
                FooterStatusText.Text = I18n.Tr("Папка уже была в списке и теперь включена.", "Folder was already in the list and is now enabled.");
                _ = ApplyMcpRootsAsync();
                return;
            }

            var newItem = new RootPathItemViewModel { Path = fullPath, IsEnabled = true };
            _rootItems.Add(newItem);
            if (!SaveSettings())
            {
                _rootItems.Remove(newItem);
                return;
            }

            ErrorLog.LogApp(I18n.Tr("Добавлена разрешённая папка: " + fullPath, "Added allowed folder: " + fullPath));
            FooterStatusText.Text = I18n.Tr("Папка добавлена. Обновляю Filesystem MCP…", "Folder added. Updating Filesystem MCP…");
            _ = ApplyMcpRootsAsync();
        }
        catch (Exception exception) when (
            exception is ArgumentException || exception is IOException ||
            exception is UnauthorizedAccessException || exception is NotSupportedException)
        {
            ErrorLog.Write("MainWindow.AddRoot_Click", exception);
            WpfMessageBox.Show(
                this,
                exception.Message,
                I18n.Tr("Не удалось добавить папку", "Failed to add folder"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsListBox.SelectedItem is not RootPathItemViewModel selectedRoot)
        {
            FooterStatusText.Text = I18n.Tr("Сначала выберите папку в списке.", "Select a folder in the list first.");
            return;
        }

        var selectedIndex = _rootItems.IndexOf(selectedRoot);
        if (selectedIndex < 0) return;

        _rootItems.RemoveAt(selectedIndex);
        if (!SaveSettings())
        {
            _rootItems.Insert(selectedIndex, selectedRoot);
            return;
        }

        ErrorLog.LogApp(I18n.Tr("Удалена разрешённая папка: " + selectedRoot.Path, "Removed allowed folder: " + selectedRoot.Path));
        FooterStatusText.Text = I18n.Tr("Папка удалена. Обновляю Filesystem MCP…", "Folder removed. Updating Filesystem MCP…");
        _ = ApplyMcpRootsAsync();
    }

    private async Task ApplyMcpRootsAsync()
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        try
        {
            await _mcpInfrastructure.ApplyAllowedRootsAsync(GetEnabledRootPaths());
            FooterStatusText.Text = I18n.Tr(
                "Разрешённые папки Filesystem MCP обновлены без перезапуска Bridge MCP Gateway (:8004) и внутреннего роутера (:8001).",
                "Filesystem MCP allowed folders updated without restarting Bridge MCP Gateway (:8004) or the internal router (:8001).");
        }
        catch (Exception exception)
        {
            ErrorLog.Write("MainWindow.ApplyMcpRootsAsync", exception);
            FooterStatusText.Text = I18n.Tr("Ошибка обновления папок: ", "Error updating folders: ") + exception.Message + I18n.Tr(" (см. Логи)", " (see Logs)");
        }
    }

    // =========================================================================
    // =========================================================================
    // TAILSCALE AUTHORIZATION AND FUNNEL LIFECYCLE
    // =========================================================================

    private async void TailscaleCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted)
            return;

        try
        {
            await _tailscaleManager.ToggleConnectionAsync();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("MainWindow.TailscaleConnectionManager", exception);
            FooterStatusText.Text = exception.Message;
        }
        finally
        {
            UpdateTailscaleUi();
        }
    }

    private void StartEditorMonitoring()
    {
        FooterStatusText.Text = I18n.Tr("Оркестратор запущен.", "Orchestrator started.");
        RefreshEditorDisplay();
    }

    private void EditorMonitor_ProcessObserved(object? sender, EditorProcessEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _observedEditors[e.ProcessId] = e.ProcessName;
            _detectionErrors.Remove(e.ProcessId);
            RefreshEditorDisplay();
        }), DispatcherPriority.Background);
    }

    private void EditorMonitor_ProcessNoLongerRunning(object? sender, EditorProcessEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _observedEditors.Remove(e.ProcessId);
            _detectionErrors.Remove(e.ProcessId);
            RefreshEditorDisplay();
        }), DispatcherPriority.Background);
    }

    private void EditorMonitor_SessionChanged(object? sender, EditorSessionEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _observedEditors[e.Session.ProcessId] = e.Session.ProcessName;
            RefreshEditorDisplay();
        }), DispatcherPriority.Background);
    }

    private void EditorMonitor_SessionEnded(object? sender, EditorSessionEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _observedEditors.Remove(e.Session.ProcessId);
            _detectionErrors.Remove(e.Session.ProcessId);
            RefreshEditorDisplay();
            if (_observedEditors.Count == 0)
            {
                FooterStatusText.Text = I18n.Tr(
                    "Unreal Editor закрыт. Доступность MCP определяется независимой проверкой endpoint.",
                    "Unreal Editor closed. MCP availability is determined by endpoint verification.");
            }
        }), DispatcherPriority.Background);
    }

    private void EditorMonitor_DetectionFailed(object? sender, EditorDetectionFailedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.ProcessId > 0)
                _detectionErrors[e.ProcessId] = e.Message;
            else
                FooterStatusText.Text = e.Message;
            RefreshEditorDisplay();
        }), DispatcherPriority.Background);
    }

    private void RefreshEditorDisplay()
    {
        var sessions = _editorMonitor.Sessions.ToDictionary(session => session.ProcessId);
        var editorIds = _observedEditors.Keys.OrderBy(pid => pid).ToArray();

        if (editorIds.Length == 0)
        {
            EditorProcessText.Text = I18n.Tr("Не запущен (Auto-Attach в режиме ожидания)", "Not running (Auto-Attach waiting)");
            EditorProjectText.Text = I18n.Tr("При открытии Unreal Editor его MCP-сервер (:8000) автоматически подключится в общий шлюз.", "When Unreal Editor opens, its MCP server (:8000) will automatically attach to the gateway.");
        }
        else
        {
            EditorProcessText.Text = string.Join(Environment.NewLine, editorIds.Select(pid =>
                _observedEditors[pid] + " · PID " + pid));

            EditorProjectText.Text = string.Join(Environment.NewLine + Environment.NewLine, editorIds.Select(pid =>
            {
                if (_detectionErrors.TryGetValue(pid, out var error))
                    return "PID " + pid + ": " + error;

                if (sessions.TryGetValue(pid, out var session) && !string.IsNullOrWhiteSpace(session.ProjectPath))
                    return "PID " + pid + ": " + session.ProjectPath;

                return "PID " + pid + I18n.Tr(": процесс обнаружен; чтение пути .uproject…", ": process detected; reading .uproject path…");
            }));
        }

        UpdateTopRuntimeBadge();
    }

    private void UpdateTopRuntimeBadge()
    {
        var report = McpDependencyResolver.GetCachedReport();
        if (report == null)
        {
            RuntimeStatusText.Text = I18n.Tr("Проверка локальных компонентов…", "Checking local components…");
            RuntimeStatusText.Foreground = (Brush)FindResource("StatusConnectingBrush");
            RuntimeStatusDot.Fill = (Brush)FindResource("StatusConnectingBrush");
            return;
        }

        if (!report.CoreReady)
        {
            FooterStatusText.Text = report.Summary;
            _dependencyFooterWarning = true;
        }
        else if (_dependencyFooterWarning)
        {
            FooterStatusText.Text = I18n.Tr("Готово", "Ready");
            _dependencyFooterWarning = false;
        }

        if (_mcpInfrastructure.IsReady)
        {
            var mode = !report.StdioReady
                ? I18n.Tr(" · только HTTP MCP", " · HTTP MCP only")
                : !report.FilesystemReady
                    ? I18n.Tr(" · Filesystem недоступен", " · Filesystem unavailable")
                    : string.Empty;
            RuntimeStatusText.Text = I18n.Tr("Bridge MCP :8004 активен", "Bridge MCP :8004 active") +
                                     mode + " · " + _onlineServersCount + "/" + _enabledServersCount +
                                     " · Tools: " + _totalToolsCount;
            RuntimeStatusText.Foreground = (Brush)FindResource("StatusConnectedBrush");
            RuntimeStatusDot.Fill = (Brush)FindResource("StatusConnectedBrush");
        }
        else if (!report.CoreReady)
        {
            RuntimeStatusText.Text = I18n.Tr("MCP-шлюз недоступен · проверьте зависимости", "MCP gateway unavailable · check dependencies");
            var unavailableBrush = new SolidColorBrush(Color.FromRgb(255, 128, 128));
            RuntimeStatusText.Foreground = unavailableBrush;
            RuntimeStatusDot.Fill = unavailableBrush;
        }
        else
        {
            RuntimeStatusText.Text = I18n.Tr("Инициализация Bridge MCP Gateway :8004…", "Initializing Bridge MCP Gateway :8004…");
            RuntimeStatusText.Foreground = (Brush)FindResource("StatusConnectingBrush");
            RuntimeStatusDot.Fill = (Brush)FindResource("StatusConnectingBrush");
        }
    }

    private void McpInfrastructure_StatusChanged(object? sender, McpInfrastructureStatusEventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_cleanupStarted || _cleanupCompleted) return;

                _onlineServersCount = e.OnlineCount;
                _enabledServersCount = e.EnabledCount;
                _totalToolsCount = e.TotalToolsCount;

                DashboardServicesListControl.ItemsSource = e.EnabledServers;
                UpdateServerItemsInPlace(e.Servers);
                DashboardServiceCount.Text = e.OnlineCount + " / " + e.EnabledCount;

                UpdateTopRuntimeBadge();
                UpdateTailscaleUi();
                _tailscaleManager.NotifyGatewayAvailability(_mcpInfrastructure.IsReady);
            }), DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may be shutting down while managed MCP processes stop.
        }
    }

    private void UpdateServerItemsInPlace(IReadOnlyList<McpServerStatusSnapshot> incoming)
    {
        if (_serverItems.Count == incoming.Count &&
            !_serverItems.Where((item, idx) => !string.Equals(item.Id, incoming[idx].Id, StringComparison.OrdinalIgnoreCase)).Any())
        {
            for (var i = 0; i < incoming.Count; i++)
                _serverItems[i].UpdateFrom(incoming[i]);
            return;
        }

        var existingById = _serverItems.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        _serverItems.Clear();
        foreach (var item in incoming)
        {
            if (existingById.TryGetValue(item.Id, out var existing))
            {
                existing.UpdateFrom(item);
                _serverItems.Add(existing);
            }
            else
            {
                _serverItems.Add(item);
            }
        }
    }


    private void TailscaleConnectionManager_StatusChanged(object? sender, EventArgs e)
    {
        if (_cleanupStarted || _cleanupCompleted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_cleanupStarted && !_cleanupCompleted)
                    UpdateTailscaleUi();
            }), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may be shutting down while an already-cancelled
            // background Tailscale operation publishes its final state.
        }
    }

    private static bool IsTailscaleConnecting(TailscaleConnectionPhase phase) =>
        phase switch
        {
            TailscaleConnectionPhase.Connecting or
            TailscaleConnectionPhase.Disconnecting or
            TailscaleConnectionPhase.WaitingForLogin or
            TailscaleConnectionPhase.WaitingForDeviceApproval or
            TailscaleConnectionPhase.WaitingForHttpsSetup or
            TailscaleConnectionPhase.WaitingForFunnelApproval or
            TailscaleConnectionPhase.WaitingForGateway => true,
            _ => false
        };

    private void UpdateTailscaleUi()
    {
        // Keep the detailed lifecycle text in settings, while the dashboard KPI
        // uses the short three-state label updated by UpdateDashboardUi().
        var phase = _tailscaleManager.Phase;
        var status = _tailscaleManager.StatusText;
        var isBusy = _tailscaleManager.IsBusy;
        var isConnected = _tailscaleManager.IsConnected;
        var isEnabled = _tailscaleManager.IsEnabled;
        var isInitialSetupComplete = _tailscaleManager.IsInitialSetupComplete;

        TailscaleCheckButton.Visibility = Visibility.Visible;

        var buttonEnabled = !isBusy;
        if (TailscaleCheckButton.IsEnabled != buttonEnabled)
            TailscaleCheckButton.IsEnabled = buttonEnabled;

        var buttonContent = isBusy
            ? (phase == TailscaleConnectionPhase.Disconnecting
                ? I18n.Tr("Отключение…", "Disconnecting…")
                : I18n.Tr("Подключение…", "Connecting…"))
            : (isConnected || (isEnabled && isInitialSetupComplete))
                ? I18n.Tr("Отключить", "Disconnect")
                : I18n.Tr("Подключить", "Connect");

        if (!string.Equals(TailscaleCheckButton.Content as string, buttonContent, StringComparison.Ordinal))
            TailscaleCheckButton.Content = buttonContent;

        TailscaleStatusText.Text = status;
        TailscaleAuthStatusText.Text = status;

        var connected = isConnected || phase == TailscaleConnectionPhase.Connected;
        var connecting = IsTailscaleConnecting(phase);
        var accent = (Brush)FindResource(connected
            ? "StatusConnectedBrush"
            : connecting
                ? "StatusConnectingBrush"
                : "StatusDisconnectedBrush");
        var borderAccent = (Brush)FindResource(connected
            ? "StatusConnectedBrush"
            : connecting
                ? "StatusConnectingBrush"
                : "StatusDisconnectedBorderBrush");
        var background = (Brush)FindResource(connected
            ? "StatusConnectedBackgroundBrush"
            : connecting
                ? "StatusConnectingBackgroundBrush"
                : "StatusDisconnectedBackgroundBrush");

        TailscaleStatusBorder.Background = background;
        TailscaleStatusBorder.BorderBrush = borderAccent;
        TailscaleStatusText.Foreground = accent;

        TailscaleAuthStatusBorder.Background = background;
        TailscaleAuthStatusBorder.BorderBrush = borderAccent;
        TailscaleAuthStatusText.Foreground = accent;


        var publicUrl = isConnected ? _tailscaleManager.PublicUrl : null;
        _currentMcpUrl = publicUrl;
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            McpUrlTextBox.Text = publicUrl;
            DashboardUrlText.Text = publicUrl;
            CopyUrlButton.IsEnabled = true;
            UrlHintText.Text = I18n.Tr(
                "Публичный HTTPS-туннель активен.",
                "Public HTTPS tunnel is active.");
        }
        else
        {
            McpUrlTextBox.Text = I18n.Tr("Публичный URL не активен", "Public URL is inactive");
            DashboardUrlText.Text = status;
            CopyUrlButton.IsEnabled = false;
            UrlHintText.Text = status;
        }

        UpdateDashboardUi();
    }

    private void UpdateDashboardUi()
    {
        if (DashboardPublicUrlState == null) return;

        DashboardServiceCount.Text = _onlineServersCount + " / " + _enabledServersCount;

        var connectedBrush = (Brush)FindResource("StatusConnectedBrush");
        var connectingBrush = (Brush)FindResource("StatusConnectingBrush");
        var disconnectedBrush = (Brush)FindResource("StatusDisconnectedBrush");
        var disconnectedBorderBrush = (Brush)FindResource("StatusDisconnectedBorderBrush");
        var disabledBrush = (Brush)FindResource("StatusDisabledBrush");
        var disabledBorderBrush = (Brush)FindResource("StatusDisabledBorderBrush");

        var serviceAccent = _enabledServersCount == 0
            ? disabledBrush
            : _onlineServersCount == 0
                ? disconnectedBrush
                : _onlineServersCount < _enabledServersCount
                    ? connectingBrush
                    : connectedBrush;
        var serviceBorderAccent = _enabledServersCount == 0
            ? disabledBorderBrush
            : _onlineServersCount == 0
                ? disconnectedBorderBrush
                : serviceAccent;

        DashboardServiceCount.Foreground = serviceAccent;
        Kpi1CardBorder.BorderBrush = serviceBorderAccent;
        Kpi1StatusIconPath.Stroke = serviceAccent;

        var gatewayAccent = _mcpInfrastructure.IsReady ? connectedBrush : connectingBrush;
        Kpi2CardBorder.BorderBrush = gatewayAccent;
        Kpi2StatusIconPath.Stroke = gatewayAccent;
        DashboardGatewayPort.Text = _mcpInfrastructure.LocalEndpoint;
        DashboardGatewayPort.Foreground = gatewayAccent;

        var phase = _tailscaleManager.Phase;
        var onlineConnected = _tailscaleManager.IsConnected ||
                              phase == TailscaleConnectionPhase.Connected;
        var onlineConnecting = IsTailscaleConnecting(phase);
        var onlineAccent = onlineConnected
            ? connectedBrush
            : onlineConnecting
                ? connectingBrush
                : disconnectedBrush;
        var onlineBorderAccent = onlineConnected
            ? connectedBrush
            : onlineConnecting
                ? connectingBrush
                : disconnectedBorderBrush;

        DashboardPublicUrlState.Text = onlineConnected
            ? I18n.Tr("Активно", "Active")
            : onlineConnecting
                ? I18n.Tr("Подключение…", "Connecting…")
                : I18n.Tr("Не активно", "Inactive");
        DashboardPublicUrlState.Foreground = onlineAccent;
        Kpi3SubText.Foreground = onlineAccent;
        Kpi3CardBorder.BorderBrush = onlineBorderAccent;
        Kpi3StatusIconPath.Stroke = onlineAccent;

        if (!string.IsNullOrWhiteSpace(_currentMcpUrl))
            DashboardUrlText.Text = _currentMcpUrl;
        else
            DashboardUrlText.Text = _tailscaleManager.StatusText;
    }

    private void RestoreFromTray()
    {
        if (_cleanupStarted || _cleanupCompleted) return;
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        if (_exitRequested || _cleanupStarted || _cleanupCompleted) return;
        _exitRequested = true;
        Close();
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_changingAutoStartControl) return;

        var enabled = AutoStartCheckBox.IsChecked == true;
        try
        {
            AutoStartService.SetEnabled(enabled);
            _settings.RunAtWindowsStartup = enabled;
            SaveSettings();
            ErrorLog.LogApp(enabled
                ? I18n.Tr("Включён автозапуск при входе в Windows.", "Enabled launch at Windows startup.")
                : I18n.Tr("Автозапуск при входе в Windows отключён.", "Disabled launch at Windows startup."));
            FooterStatusText.Text = enabled
                ? I18n.Tr("Менеджер будет запускаться при входе в Windows.", "Manager will launch at Windows sign-in.")
                : I18n.Tr("Автозапуск отключён.", "Autostart disabled.");
        }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is SecurityException || exception is InvalidOperationException ||
            exception is ArgumentException)
        {
            _changingAutoStartControl = true;
            AutoStartCheckBox.IsChecked = !enabled;
            _changingAutoStartControl = false;
            _settings.RunAtWindowsStartup = !enabled;
            try { AutoStartService.SetEnabled(!enabled); } catch { }
            ErrorLog.Write("MainWindow.AutoStart_Click", exception);
            WpfMessageBox.Show(this, exception.Message, I18n.Tr("Не удалось изменить автозапуск", "Failed to change autostart"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }

        if (_cleanupCompleted) return;

        e.Cancel = true;
        if (_cleanupStarted) return;

        _cleanupStarted = true;
        _mcpInfrastructure.StatusChanged -= McpInfrastructure_StatusChanged;
        IsEnabled = false;
        ErrorLog.LogApp(I18n.Tr("Завершение работы MCP Bridge…", "Shutting down MCP Bridge…"));
        FooterStatusText.Text = I18n.Tr("Остановка мониторинга и управляемых MCP-серверов…", "Stopping monitoring and managed MCP servers…");

        try
        {
            try { _editorMonitor.Dispose(); }
            catch (Exception exception) { ErrorLog.Write("MainWindow.ExitEditorCleanup", exception); }

            async Task StopTailscaleAsync()
            {
                try { await _tailscaleManager.ShutdownAsync().ConfigureAwait(false); }
                catch (Exception exception) { ErrorLog.Write("MainWindow.ExitTailscaleCleanup", exception); }
            }

            async Task StopMcpInfrastructureAsync()
            {
                try { await _mcpInfrastructure.StopAsync().ConfigureAwait(false); }
                catch (Exception exception) { ErrorLog.Write("MainWindow.ExitMcpCleanup", exception); }
            }

            // Do not serialize two independent cleanup paths. A stalled MCP
            // operation must not keep the Tailscale shutdown queued (or vice versa).
            var cleanupTask = Task.WhenAll(
                StopTailscaleAsync(),
                StopMcpInfrastructureAsync());

            var completed = await Task.WhenAny(cleanupTask, Task.Delay(TimeSpan.FromSeconds(12)));
            if (completed != cleanupTask)
            {
                ErrorLog.WriteMessage(
                    "MainWindow.ExitCleanup",
                    "Cleanup exceeded 12 seconds. Closing Bridge; remaining managed child processes will be terminated when their Windows Job Objects close.");
            }
            else
            {
                await cleanupTask;
            }
        }
        finally
        {
            _cleanupCompleted = true;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            Close();
        }
    }

    private void NavDashboard_Click(object sender, RoutedEventArgs e) => NavigateToPage(0);
    private void NavServices_Click(object sender, RoutedEventArgs e) => NavigateToPage(1);
    private void NavLogs_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage(2);
        RefreshLogs();
    }
    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPage(3);
        SetSettingsSection(technical: true);
    }

    private void ComponentsSettingsNav_Click(object sender, RoutedEventArgs e) =>
        OpenDependencyStatusPage();

    private void OpenDependencyStatusPage()
    {
        if (_cleanupStarted || _cleanupCompleted) return;

        if (_dependencyStatusView == null)
        {
            _dependencyStatusView = new DependencyStatusView(async () =>
            {
                await SyncMcpInfrastructureAsync(forceRetryQuarantined: true);
            });
            DependencyStatusHost.Content = _dependencyStatusView;
        }

        var report = McpDependencyResolver.GetCachedReport();
        if (report != null)
            _dependencyStatusView.SetReport(report);
        else
            _ = _dependencyStatusView.RefreshLocalizedReportAsync();

        NavigateToPage(3);
        SetSettingsSection(technical: false, components: true);
    }

    private void TechnicalSettingsNav_Click(object sender, RoutedEventArgs e) =>
        SetSettingsSection(technical: true);

    private void InterfaceSettingsNav_Click(object sender, RoutedEventArgs e) =>
        SetSettingsSection(technical: false);

    private void SetSettingsSection(bool technical, bool components = false)
    {
        TechnicalSettingsPanel.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;
        InterfaceSettingsPanel.Visibility = !technical && !components ? Visibility.Visible : Visibility.Collapsed;
        DependencySettingsPanel.Visibility = components ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = new SolidColorBrush(Color.FromRgb(44, 229, 208));
        var inactiveForeground = new SolidColorBrush(Color.FromRgb(150, 159, 168));

        TechnicalSettingsNavButton.BorderBrush = technical ? activeBrush : Brushes.Transparent;
        InterfaceSettingsNavButton.BorderBrush = !technical && !components ? activeBrush : Brushes.Transparent;
        ComponentsSettingsNavButton.BorderBrush = components ? activeBrush : Brushes.Transparent;
        TechnicalSettingsNavText.Foreground = technical ? Brushes.White : inactiveForeground;
        InterfaceSettingsNavText.Foreground = !technical && !components ? Brushes.White : inactiveForeground;
        ComponentsSettingsNavText.Foreground = components ? Brushes.White : inactiveForeground;
    }

    private void NavigateToPage(int index)
    {
        MainPages.SelectedIndex = index;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyLanguage();
            ApplyFontScale(1.0);
            CaptureBaseFontSizes(this);
            ApplyFontScale(_settings.FontScale <= 0 ? 1.0 : _settings.FontScale);
        }), DispatcherPriority.Loaded);

        var buttons = new[] { NavDashboardButton, NavServicesButton, NavLogsButton, NavSettingsButton };
        foreach (var button in buttons)
        {
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.Foreground = new SolidColorBrush(Color.FromRgb(150, 159, 168));
        }

        var active = buttons[index];
        active.Background = new SolidColorBrush(Color.FromRgb(39, 43, 49));
        active.BorderBrush = new SolidColorBrush(Color.FromRgb(44, 229, 208));
        active.Foreground = Brushes.White;
    }

    private void LogCategoryConnections_Click(object sender, RoutedEventArgs e) => SwitchLogCategory("connections");
    private void LogCategoryApp_Click(object sender, RoutedEventArgs e) => SwitchLogCategory("app");
    private void LogCategoryErrors_Click(object sender, RoutedEventArgs e) => SwitchLogCategory("errors");
    private void LogCategoryVerification_Click(object sender, RoutedEventArgs e) => SwitchLogCategory("verification");

    private void SwitchLogCategory(string category)
    {
        _activeLogCategory = category;
        var primaryStyle = TryFindResource("PrimaryButtonStyle") as Style;
        var defaultStyle = TryFindResource(typeof(Button)) as Style;

        LogTabConnectionsButton.Style = category == "connections" ? primaryStyle : defaultStyle;
        LogTabAppButton.Style = category == "app" ? primaryStyle : defaultStyle;
        LogTabErrorsButton.Style = category == "errors" ? primaryStyle : defaultStyle;
        LogTabVerificationButton.Style = category == "verification" ? primaryStyle : defaultStyle;
        LogsHeaderTitleText.Text = category switch
        {
            "connections" => I18n.Tr("Журнал подключений и MCP (Logs\\connections.log)", "Connections & MCP Log (Logs\\connections.log)"),
            "app" => I18n.Tr("Журнал работы приложения (Logs\\app.log)", "Application Log (Logs\\app.log)"),
            "verification" => I18n.Tr("Журнал верификации MCP (Logs\\verification.log)", "MCP Verification Log (Logs\\verification.log)"),
            _ => I18n.Tr("Журнал ошибок и сбоев (Logs\\errors.log)", "Errors & Failures Log (Logs\\errors.log)")
        };
        RefreshLogs();
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        ErrorLog.ClearCategory(_activeLogCategory);
        RefreshLogs();
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => RefreshLogs();

    private void LoggingEnabled_Click(object sender, RoutedEventArgs e)
    {
        var enabled = LoggingEnabledCheckBox.IsChecked == true;
        _settings.LoggingEnabled = enabled;
        ErrorLog.Enabled = enabled;
        SaveSettings();
        if (enabled)
            ErrorLog.LogApp(I18n.Tr("Запись логов включена.", "Log recording enabled."));
        else
            FooterStatusText.Text = I18n.Tr("Запись логов выключена.", "Log recording disabled.");
        RefreshLogs();
    }

    private void RefreshLogs()
    {
        LogsTextBox.Text = ErrorLog.ReadCategory(_activeLogCategory);
        LogsTextBox.ScrollToEnd();
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.LogsDirectory, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is Win32Exception)
        {
            FooterStatusText.Text = exception.Message;
        }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentMcpUrl)) return;
        try
        {
            WpfClipboard.SetText(_currentMcpUrl!);
            FooterStatusText.Text = I18n.Tr("URL скопирован ✓", "URL copied ✓");
            CopyUrlButton.Content = I18n.Tr("Скопировано ✓", "Copied ✓");
            CopyUrlButton.Opacity = 0.72;
            var feedback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
            feedback.Tick += (_, _) =>
            {
                feedback.Stop();
                CopyUrlButton.Content = I18n.Tr("Копировать URL", "Copy URL");
                CopyUrlButton.Opacity = 1.0;
            };
            feedback.Start();
        }
        catch (Exception exception) when (exception is ExternalException || exception is InvalidOperationException)
        {
            ErrorLog.Write("MainWindow.CopyUrl_Click", exception);
            FooterStatusText.Text = I18n.Tr("Не удалось скопировать URL.", "Failed to copy URL.");
        }
    }

    private void CopyLocalUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentLocalMcpUrl)) return;
        try
        {
            WpfClipboard.SetText(_currentLocalMcpUrl);
            FooterStatusText.Text = I18n.Tr("Локальный URL скопирован ✓", "Local URL copied ✓");
            CopyLocalUrlButton.Content = I18n.Tr("Скопировано ✓", "Copied ✓");
            CopyLocalUrlButton.Opacity = 0.72;
            var feedback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
            feedback.Tick += (_, _) =>
            {
                feedback.Stop();
                CopyLocalUrlButton.Content = I18n.Tr("Копировать URL", "Copy URL");
                CopyLocalUrlButton.Opacity = 1.0;
            };
            feedback.Start();
        }
        catch (Exception exception) when (exception is ExternalException || exception is InvalidOperationException)
        {
            ErrorLog.Write("MainWindow.CopyLocalUrl_Click", exception);
            FooterStatusText.Text = I18n.Tr("Не удалось скопировать локальный URL.", "Failed to copy local URL.");
        }
    }

    private bool SaveSettings()
    {
        try
        {
            _settings.AllowedRoots = _rootItems.Select(r => r.Path).ToList();
            _settings.DisabledRoots = _rootItems.Where(r => !r.IsEnabled).Select(r => r.Path).ToList();
            _settings.RunAtWindowsStartup = AutoStartCheckBox.IsChecked == true;
            _settingsStore.Save(_settings);
            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
        {
            ErrorLog.Write("MainWindow.SaveSettings", exception);
            WpfMessageBox.Show(this, exception.Message, I18n.Tr("Не удалось сохранить настройки", "Failed to save settings"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshUrlState()
    {
        _currentLocalMcpUrl = _mcpInfrastructure.LocalEndpoint;
        LocalMcpUrlTextBox.Text = _currentLocalMcpUrl;
        CopyLocalUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(_currentLocalMcpUrl);
        DashboardGatewayPort.Text = _currentLocalMcpUrl;

        _currentMcpUrl = null;
        McpUrlTextBox.Text = I18n.Tr("Туннель ещё не запущен", "Tunnel is not running yet");
        CopyUrlButton.IsEnabled = false;
        UrlHintText.Text = I18n.Tr(
            "После запуска шлюза и Tailscale Funnel здесь появится актуальный публичный URL.",
            "Once the gateway and Tailscale Funnel are running, the active public URL will appear here.");
        DashboardUrlText.Text = I18n.Tr("Ожидание подключения", "Waiting for connection");
        UpdateDashboardUi();
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > pathRoot.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    private void InitializeAppVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var informationalVersion = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Select(attribute => attribute.InformationalVersion)
                .FirstOrDefault();

            var versionText = informationalVersion;
            if (string.IsNullOrWhiteSpace(versionText))
            {
                var assemblyVersion = assembly.GetName().Version;
                if (assemblyVersion != null)
                    versionText = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
            }

            if (!string.IsNullOrWhiteSpace(versionText))
            {
                var metadataSeparator = versionText.IndexOf('+');
                if (metadataSeparator >= 0)
                    versionText = versionText.Substring(0, metadataSeparator);
            }

            AppVersionText.Text = string.IsNullOrWhiteSpace(versionText) ? "v—" : $"v{versionText}";
        }
        catch
        {
            AppVersionText.Text = "v—";
        }
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Darek3399/MCP-Bridge",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ErrorLog.Write("MainWindow.GitHubButton_Click", exception);
        }
    }

    private void TrimmedTextBlock_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock textBlock) return;

        var fullText = textBlock.Tag as string ?? textBlock.Text;
        if (string.IsNullOrWhiteSpace(fullText) || !IsTextBlockTrimmed(textBlock))
        {
            e.Handled = true;
            return;
        }

        if (textBlock.ToolTip is ToolTip toolTip)
        {
            toolTip.Content = fullText;
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

        double dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(textBlock).PixelsPerDip; }
        catch { }

        var formattedText = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentCulture,
            textBlock.FlowDirection,
            typeface,
            textBlock.FontSize,
            Brushes.Black,
            dpi);

        return formattedText.Width > textBlock.ActualWidth + 1.0;
    }
}

internal sealed class McpBridgeColorTable : FormsProfessionalColorTable
{
    private static readonly Drawing.Color MenuBackground = Drawing.Color.FromArgb(30, 33, 37);
    private static readonly Drawing.Color MenuHover = Drawing.Color.FromArgb(40, 44, 50);
    private static readonly Drawing.Color MenuBorderColor = Drawing.Color.FromArgb(44, 229, 208);

    public override Drawing.Color ToolStripDropDownBackground => MenuBackground;
    public override Drawing.Color ToolStripBorder => MenuBorderColor;
    public override Drawing.Color MenuBorder => MenuBorderColor;
    public override Drawing.Color MenuItemBorder => MenuBorderColor;
    public override Drawing.Color MenuItemSelected => MenuHover;
    public override Drawing.Color MenuItemSelectedGradientBegin => MenuHover;
    public override Drawing.Color MenuItemSelectedGradientEnd => MenuHover;
    public override Drawing.Color MenuItemPressedGradientBegin => Drawing.Color.FromArgb(24, 26, 29);
    public override Drawing.Color MenuItemPressedGradientEnd => Drawing.Color.FromArgb(24, 26, 29);
    public override Drawing.Color ImageMarginGradientBegin => MenuBackground;
    public override Drawing.Color ImageMarginGradientMiddle => MenuBackground;
    public override Drawing.Color ImageMarginGradientEnd => MenuBackground;
    public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(44, 48, 54);
    public override Drawing.Color SeparatorLight => Drawing.Color.FromArgb(44, 48, 54);
}
