using System.Windows;
using McpBridge.Services;

namespace McpBridge;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(string versionLabel)
    {
        InitializeComponent();
        WindowChromeHelper.Configure(this, WindowOuterBorder, WindowRootGrid, 5.0);

        Title = I18n.Tr("Доступно обновление MCP Bridge", "MCP Bridge update available");
        DialogHeadingText.Text = I18n.Tr("ДОСТУПНО ОБНОВЛЕНИЕ", "UPDATE AVAILABLE");
        DialogVersionText.Text = "MCP Bridge v" + versionLabel;
        DialogMessageText.Text = I18n.Tr(
            "Обновление загружено и проверено. Установить его сейчас?",
            "The update has been downloaded and verified. Install it now?");
        VerifiedStatusText.Text = I18n.Tr(
            "УСТАНОВЩИК ЗАГРУЖЕН И ПРОВЕРЕН",
            "INSTALLER DOWNLOADED AND VERIFIED");
        LaterButton.Content = I18n.Tr("Позже", "Later");
        InstallButton.Content = I18n.Tr("Установить", "Install");

        if (Application.Current.MainWindow != null)
            Icon = Application.Current.MainWindow.Icon;
    }

    private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
