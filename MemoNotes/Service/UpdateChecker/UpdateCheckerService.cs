using System.IO;
using System.Net.Http;
using System.Text.Json;
using MemoNotes.Models;

namespace MemoNotes.Service.UpdateChecker;

/// <summary>
/// Сервис для проверки обновлений через GitHub Releases API.
/// </summary>
public class UpdateCheckerService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/max-tkv/MemoNotes/releases/latest";
    private const string UserAgent = "MemoNotes-UpdateChecker";
    
    private static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Асинхронно проверяет наличие новой версии на GitHub.
    /// </summary>
    /// <param name="currentVersion">Текущая версия приложения.</param>
    /// <returns>Новый релиз, если доступна обновлённая версия; иначе null.</returns>
    public static async Task<GitHubRelease?> CheckForUpdatesAsync(string currentVersion)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Add("Accept", "application/vnd.github.v3+json");

            using var response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release == null || release.Draft)
                return null;

            var current = ParseVersion(currentVersion);
            var latest = ParseVersion(release.TagName);

            if (latest == null || current == null)
                return null;

            return latest > current ? release : null;
        }
        catch
        {
            // При ошибке сети или API — тихо возвращаем null
            return null;
        }
    }

    /// <summary>
    /// Формирует прямой URL скачивания .exe файла релиза.
    /// </summary>
    public static string GetDirectDownloadUrl(string tagName)
    {
        var version = tagName.TrimStart('v');
        return $"https://github.com/max-tkv/MemoNotes/releases/download/{tagName}/MemoNotes-{version}.exe";
    }

    /// <summary>
    /// Скачивает .exe файл обновления в папку Temp с прогрессом.
    /// </summary>
    /// <param name="tagName">Тег версии для скачивания.</param>
    /// <param name="progress">Callback прогресса загрузки (0.0 — 1.0).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Путь к скачанному файлу, или null при ошибке.</returns>
    public static async Task<string?> DownloadUpdateAsync(string tagName, 
        IProgress<double>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var downloadUrl = GetDirectDownloadUrl(tagName);
            var version = tagName.TrimStart('v');
            var fileName = $"MemoNotes-{version}-update.exe";
            var tempPath = Path.Combine(Path.GetTempPath(), fileName);

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Add("User-Agent", UserAgent);

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0L;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (double)totalRead / totalBytes;
                    progress?.Report(percent);
                }
            }

            return tempPath;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Запускает скачанный файл обновления.
    /// </summary>
    public static void RunUpdate(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    /// <summary>
    /// Открывает страницу релиза в браузере.
    /// </summary>
    public static void OpenReleasePage(string htmlUrl)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = htmlUrl,
            UseShellExecute = true
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    /// <summary>
    /// Парсит строку версии (формата "1.0.0", "v1.0.0", "1.0.0-beta3") в объект Version.
    /// Учитывает только числовые части (Major.Minor.Patch), пред-релиз теги игнорируются.
    /// </summary>
    private static Version? ParseVersion(string versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
            return null;

        // Убираем префикс 'v' если есть
        versionString = versionString.TrimStart('v');

        // Оставляем только числовую часть (до первого дефиса для пред-релизов)
        var dashIndex = versionString.IndexOf('-');
        if (dashIndex >= 0)
            versionString = versionString.Substring(0, dashIndex);

        return Version.TryParse(versionString, out var version) ? version : null;
    }
}
