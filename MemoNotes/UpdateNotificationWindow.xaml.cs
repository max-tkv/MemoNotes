using System.Windows;
using System.Windows.Input;
using MemoNotes.Models;
using MemoNotes.Properties;
using MemoNotes.Service.UpdateChecker;

namespace MemoNotes;

public partial class UpdateNotificationWindow : Window
{
    private readonly GitHubRelease _release;
    private CancellationTokenSource? _downloadCts;

    public UpdateNotificationWindow(GitHubRelease release)
    {
        InitializeComponent();
        _release = release;

        VersionTextBlock.Text = $"{_release.TagName}";
        ReleaseNotesTextBlock.Text = _release.DisplayBody;
    }

    /// <summary>
    /// Закрыть окно без запоминания отклонения.
    /// </summary>
    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownload();
        Close();
    }

    /// <summary>
    /// Отклонить обновление на 1 день.
    /// </summary>
    private void DismissForDayButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownload();
        Settings.Default.UpdateDismissedDate = DateTime.Now.ToString("yyyy-MM-dd");
        Settings.Default.Save();
        Close();
    }

    /// <summary>
    /// Скачать обновление через приложение с прогрессом.
    /// </summary>
    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts = new CancellationTokenSource();

        // Переключаем UI в режим загрузки
        DownloadButton.IsEnabled = false;
        DownloadButton.Content = "Скачивание...";
        DismissForDayButton.IsEnabled = false;
        OpenOnGitHubButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        ProgressTextBlock.Visibility = Visibility.Visible;
        ProgressTextBlock.Text = "Загрузка: 0%";

        var progress = new Progress<double>(percent =>
        {
            DownloadProgressBar.Value = percent * 100;
            ProgressTextBlock.Text = $"Загрузка: {(int)(percent * 100)}%";
        });

        var filePath = await UpdateCheckerService.DownloadUpdateAsync(
            _release.TagName, 
            progress, 
            _downloadCts.Token);

        if (filePath != null)
        {
            ProgressTextBlock.Text = "Загрузка завершена! Запуск обновления...";
            DownloadButton.Content = "Готово";

            // Запускаем скачанный файл
            UpdateCheckerService.RunUpdate(filePath);

            // Закрываем окно через небольшую задержку
            await Task.Delay(1500);
            Close();
        }
        else
        {
            // Сбрасываем UI при ошибке
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = "Скачать";
            DismissForDayButton.IsEnabled = true;
            OpenOnGitHubButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            ProgressTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Открыть страницу релиза на GitHub.
    /// </summary>
    private void OpenOnGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckerService.OpenReleasePage(_release.HtmlUrl);
        Close();
    }

    /// <summary>
    /// Перетаскивание окна за заголовок.
    /// </summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CancelDownload()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelDownload();
        base.OnClosed(e);
    }
}
