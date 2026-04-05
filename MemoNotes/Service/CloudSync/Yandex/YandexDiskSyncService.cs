using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MemoNotes.Models;
using MemoNotes.Service.Logging;

namespace MemoNotes.Service.CloudSync;

/// <summary>
/// Сервис синхронизации с Яндекс Диском через REST API (https://yandex.ru/dev/disk-api/doc/ru/).
/// Использует двухшаговый процесс загрузки/скачивания файлов через получение временной ссылки.
/// </summary>
public class YandexDiskSyncService : ICloudSyncService, IDisposable
{
    private const string ApiBaseUrl = "https://cloud-api.yandex.net/v1/disk";
    private const string RemoteFolder = "MemoNotes";
    private const string RemoteFileName = "board.memo";
    private const string RemoteFolderPath = $"disk:/{RemoteFolder}";
    private const string RemoteFilePath = $"disk:/{RemoteFolder}/{RemoteFileName}";

    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public YandexDiskSyncService(string oauthToken)
    {
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", oauthToken);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var maskedToken = oauthToken.Length > 8
            ? oauthToken[..4] + "..." + oauthToken[^4..]
            : "***";
        Logger.Info<YandexDiskSyncService>($"Инициализация с токеном: {maskedToken}");
    }

    public async Task<bool> CheckConnectionAsync()
    {
        try
        {
            // GET /v1/disk — получить информацию о диске
            var response = await _client.GetAsync($"{ApiBaseUrl}/");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Logger.Error<YandexDiskSyncService>("Токен недействителен (401 Unauthorized)");
            }
            else
            {
                Logger.Error<YandexDiskSyncService>($"Ошибка проверки подключения: HTTP {response.StatusCode}");
            }
            return false;
        }
        catch (HttpRequestException ex)
        {
            Logger.Error<YandexDiskSyncService>($"Ошибка проверки подключения: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error<YandexDiskSyncService>($"Ошибка проверки подключения: {ex.Message}");
            return false;
        }
    }

    public async Task<CloudSyncResult> DownloadAsync(string? localETag = null)
    {
        try
        {
            // Шаг 1: Получить информацию о файле для проверки ETag/модификации
            var resourceInfo = await GetResourceInfoAsync(RemoteFilePath);

            if (resourceInfo == null)
            {
                // Файл не найден — первая загрузка
                Logger.Info<YandexDiskSyncService>("Файл не найден в облаке — первая загрузка");
                return CloudSyncResult.Ok();
            }

            // Проверяем ETag (используем SHA256 как аналог ETag)
            if (!string.IsNullOrEmpty(localETag) && resourceInfo.Sha256 == localETag)
            {
                Logger.Info<YandexDiskSyncService>("Файл не изменён (SHA256 совпадает)");
                return CloudSyncResult.Ok(resourceInfo.Modified, resourceInfo.Sha256);
            }

            // Шаг 2: Получить ссылку для скачивания
            var downloadUrl = await GetDownloadLinkAsync(RemoteFilePath);
            if (downloadUrl == null)
            {
                return CloudSyncResult.Fail("Не удалось получить ссылку для скачивания");
            }

            // Шаг 3: Скачать файл по полученной ссылке
            using var downloadResponse = await _client.GetAsync(downloadUrl);
            downloadResponse.EnsureSuccessStatusCode();

            var content = await downloadResponse.Content.ReadAsStringAsync();

            Logger.Info<YandexDiskSyncService>(
                $"Файл загружен из облака, SHA256={resourceInfo.Sha256}, Modified={resourceInfo.Modified}");

            return new CloudSyncResult
            {
                Success = true,
                DownloadedContent = content,
                RemoteETag = resourceInfo.Sha256,
                RemoteLastModified = resourceInfo.Modified
            };
        }
        catch (Exception ex)
        {
            Logger.Error<YandexDiskSyncService>($"Ошибка загрузки из облака: {ex.Message}");
            return CloudSyncResult.Fail(ex.Message);
        }
    }

    public async Task<CloudSyncResult> UploadAsync(string content, string? localETag = null)
    {
        try
        {
            // Шаг 1: Убедиться что папка существует
            await EnsureFolderExistsAsync();

            // Шаг 2: При наличии ETag — проверить что файл не изменился (конфликт)
            if (!string.IsNullOrEmpty(localETag))
            {
                var resourceInfo = await GetResourceInfoAsync(RemoteFilePath);
                if (resourceInfo != null && resourceInfo.Sha256 != localETag)
                {
                    Logger.Warn<YandexDiskSyncService>(
                        "Конфликт: файл был изменён в облаке (SHA256 не совпадает)");
                    return CloudSyncResult.Fail("CONFLICT");
                }
            }

            // Шаг 3: Получить ссылку для загрузки
            var uploadUrl = await GetUploadLinkAsync(RemoteFilePath);
            if (uploadUrl == null)
            {
                return CloudSyncResult.Fail("Не удалось получить ссылку для загрузки");
            }

            // Шаг 4: Загрузить файл по полученной ссылке (PUT без заголовка Authorization)
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            // Удаляем Authorization для запроса к внешнему URL загрузки
            request.Headers.Authorization = null;

            using var uploadResponse = await _client.SendAsync(request);

            if (uploadResponse.StatusCode == System.Net.HttpStatusCode.Created ||
                uploadResponse.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // Получаем актуальную информацию о загруженном файле
                var resourceInfo = await GetResourceInfoAsync(RemoteFilePath);

                Logger.Info<YandexDiskSyncService>(
                    $"Файл загружен в облако, SHA256={resourceInfo?.Sha256}");

                return new CloudSyncResult
                {
                    Success = true,
                    RemoteETag = resourceInfo?.Sha256,
                    RemoteLastModified = resourceInfo?.Modified
                };
            }

            if (uploadResponse.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                Logger.Warn<YandexDiskSyncService>(
                    "Конфликт: файл был изменён в облаке (412 Precondition Failed)");
                return CloudSyncResult.Fail("CONFLICT");
            }

            var errorContent = await uploadResponse.Content.ReadAsStringAsync();
            return CloudSyncResult.Fail($"HTTP {(int)uploadResponse.StatusCode}: {errorContent}");
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                Logger.Warn<YandexDiskSyncService>(
                    "Конфликт: файл был изменён в облаке (412 Precondition Failed)");
                return CloudSyncResult.Fail("CONFLICT");
            }

            Logger.Error<YandexDiskSyncService>($"Ошибка выгрузки: {ex.StatusCode}, {ex.Message}");
            return CloudSyncResult.Fail($"HTTP {ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error<YandexDiskSyncService>($"Ошибка выгрузки в облако: {ex.Message}");
            return CloudSyncResult.Fail(ex.Message);
        }
    }

    public async Task<CloudSyncResult> GetFileInfoAsync()
    {
        try
        {
            var resourceInfo = await GetResourceInfoAsync(RemoteFilePath);

            if (resourceInfo == null)
            {
                // Файл не найден — это нормально
                return CloudSyncResult.Ok();
            }

            return new CloudSyncResult
            {
                Success = true,
                RemoteETag = resourceInfo.Sha256,
                RemoteLastModified = resourceInfo.Modified
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return CloudSyncResult.Ok();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Logger.Error<YandexDiskSyncService>("Токен недействителен (401 Unauthorized) при получении информации о файле");
            return CloudSyncResult.Fail("401 Unauthorized");
        }
        catch (Exception ex)
        {
            Logger.Error<YandexDiskSyncService>($"Ошибка получения информации о файле: {ex.Message}");
            return CloudSyncResult.Fail(ex.Message);
        }
    }

    #region REST API helpers

    /// <summary>
    /// GET /v1/disk/resources — получить метаинформацию о ресурсе.
    /// Возвращает null если ресурс не найден (404).
    /// </summary>
    private async Task<YandexResourceResponse?> GetResourceInfoAsync(string path)
    {
        var url = $"{ApiBaseUrl}/resources?path={Uri.EscapeDataString(path)}";
        var response = await _client.GetAsync(url);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<YandexResourceResponse>(json, _jsonOptions);
    }

    /// <summary>
    /// GET /v1/disk/resources/upload — получить ссылку для загрузки файла.
    /// </summary>
    private async Task<string?> GetUploadLinkAsync(string path)
    {
        var url = $"{ApiBaseUrl}/resources/upload?path={Uri.EscapeDataString(path)}&overwrite=true";
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var linkResponse = JsonSerializer.Deserialize<YandexLinkResponse>(json, _jsonOptions);
        return linkResponse?.Href;
    }

    /// <summary>
    /// GET /v1/disk/resources/download — получить ссылку для скачивания файла.
    /// </summary>
    private async Task<string?> GetDownloadLinkAsync(string path)
    {
        var url = $"{ApiBaseUrl}/resources/download?path={Uri.EscapeDataString(path)}";
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var linkResponse = JsonSerializer.Deserialize<YandexLinkResponse>(json, _jsonOptions);
        return linkResponse?.Href;
    }

    /// <summary>
    /// PUT /v1/disk/resources — создать папку на Яндекс Диске.
    /// </summary>
    private async Task EnsureFolderExistsAsync()
    {
        try
        {
            var url = $"{ApiBaseUrl}/resources?path={Uri.EscapeDataString(RemoteFolderPath)}";
            var response = await _client.PutAsync(url, null);

            if (response.IsSuccessStatusCode)
            {
                Logger.Info<YandexDiskSyncService>($"Папка {RemoteFolderPath} создана на Яндекс Диске");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // 409 Conflict — папка уже существует
                Logger.Debug<YandexDiskSyncService>($"Папка {RemoteFolderPath} уже существует");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warn<YandexDiskSyncService>(
                    $"Не удалось создать папку: HTTP {response.StatusCode}, {error}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn<YandexDiskSyncService>($"Не удалось проверить/создать папку: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _client.Dispose();
            _disposed = true;
        }
    }
}
