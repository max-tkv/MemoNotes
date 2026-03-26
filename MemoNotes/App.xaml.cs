using System.Configuration;
using System.Data;
using System.Reflection;
using System.Windows;
using MemoNotes.Properties;
using MemoNotes.Service.UpdateChecker;
using Application = System.Windows.Application;

namespace MemoNotes;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Проверка обновлений в фоновом потоке
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // Проверяем, не было ли обновление отклонено сегодня
            if (Settings.Default.IsUpdateDismissedToday())
                return;

            var currentVersion = GetCurrentVersion();
            var release = await UpdateCheckerService.CheckForUpdatesAsync(currentVersion);

            if (release != null)
            {
                // Показываем окно обновления в потоке UI
                Dispatcher.Invoke(() =>
                {
                    var updateWindow = new UpdateNotificationWindow(release);
                    updateWindow.Show();
                });
            }
        }
        catch
        {
            // Тихо игнорируем ошибки при проверке обновлений
        }
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var assemblyName = assembly.GetName();
        return assemblyName.Version?.ToString(3) ?? "0.0.0";
    }
}
