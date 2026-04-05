using System.IO;
using System.Threading;
using System.Windows;
using MemoNotes.Enums;
using MemoNotes.Models;
using MemoNotes.Properties;
using MemoNotes.Service.Logging;

namespace MemoNotes.Service.CloudSync;

/// <summary>
/// Менеджер координации облачной синхронизации.
/// Выступает фасадом для работы с любым провайдером облака.
/// </summary>
public static class CloudSyncManager
{
    private const string LogSource = "CloudSync";
    private const int PollingIntervalSeconds = 5;

    /// <summary>
    /// Client ID приложения OAuth для Яндекс Диска.
    /// Зарегистрирован на https://oauth.yandex.ru/
    /// </summary>
    public const string YandexClientId = "7202d89c6a90467ea543bcbb21b0fbdf";

    /// <summary>
    /// Client Secret приложения OAuth для Яндекс Диска.
    /// Обязателен для приложений типа «Веб-сервисы».
    /// </summary>
    public const string YandexClientSecret = "372a5103e6a34d4389f3c16bf8f1b43f";

    private static ICloudSyncService? _service;
    private static readonly SemaphoreSlim _syncLock = new(1, 1);
    private static bool _isInitialized;
    private static bool _isTokenRevoked;
    private static CancellationTokenSource? _pollingCts;
    private static Task? _pollingTask;

    /// <summary>
    /// Событие, вызываемое при обнаружении обновления файла в облаке (polling).
    /// Подписчики (UI) должны перезагрузить доску.
    /// </summary>
    public static event Action? CloudDataUpdated;

    /// <summary>
    /// Событие, вызываемое при успешном подключении к облаку (авторизация/реинициализация).
    /// Подписчики (UI) должны отреагировать (перезагрузить доску).
    /// </summary>
    public static event Action? CloudConnected;

    /// <summary>
    /// Событие, вызываемое при отозванном или недействительном токене.
    /// Подписчики (UI) должны отреагировать (показать уведомление, обновить состояние).
    /// </summary>
    public static event Action<string?>? TokenRevoked;

    /// <summary>
    /// Событие, вызываемое при начале операции синхронизации (upload/download).
    /// Подписчики (UI) должны показать индикатор.
    /// </summary>
    public static event Action? SyncStarted;

    /// <summary>
    /// Событие, вызываемое при завершении операции синхронизации.
    /// Подписчики (UI) должны скрыть индикатор.
    /// </summary>
    public static event Action? SyncCompleted;

    /// <summary>
    /// Включена ли облачная синхронизация.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            var provider = (CloudProvider)Settings.Default.CloudProvider;
            return provider != CloudProvider.None
                   && !string.IsNullOrWhiteSpace(Settings.Default.CloudOAuthToken);
        }
    }

    /// <summary>
    /// Текущий провайдер облака.
    /// </summary>
    public static CloudProvider CurrentProvider => (CloudProvider)Settings.Default.CloudProvider;

    /// <summary>
    /// Инициализирует сервис синхронизации на основе настроек.
    /// Если уже инициализирован — пропускает повторное создание.
    /// Для принудительной реинициализации используйте <see cref="Reinitialize"/>.
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized && _service != null)
        {
            Logger.Debug(LogSource, "Initialize() пропущен — уже инициализирован");
            return;
        }

        var provider = (CloudProvider)Settings.Default.CloudProvider;

        if (provider == CloudProvider.None || string.IsNullOrWhiteSpace(Settings.Default.CloudOAuthToken))
        {
            Logger.Info(LogSource, "Облачная синхронизация отключена");
            StopPolling();
            _service = null;
            _isInitialized = false;
            return;
        }

        try
        {
            if (provider == CloudProvider.YandexDisk)
            {
                _service = new YandexDiskSyncService(Settings.Default.CloudOAuthToken);
                _isInitialized = true;
                Logger.Info(LogSource, "Сервис синхронизации: Яндекс Диск");

                // Проверяем, не истёк ли токен, и обновляем при необходимости
                _ = TryRefreshTokenIfNeededAsync();

                // Запускаем периодический опрос изменений
                StartPolling();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка инициализации облачного сервиса: {ex.Message}", ex);
            _service = null;
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Принудительно переинициализирует сервис синхронизации (например, после смены токена).
    /// </summary>
    public static void Reinitialize()
    {
        _isInitialized = false;
        _isTokenRevoked = false;
        Initialize();
    }

    /// <summary>
    /// Пытается обновить токен, если он истёк.
    /// </summary>
    private static async Task TryRefreshTokenIfNeededAsync()
    {
        if (!Settings.Default.IsCloudTokenExpired())
            return;

        var refreshToken = Settings.Default.CloudRefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            Logger.Warn(LogSource, "OAuth-токен истёк, refresh-токен отсутствует");
            return;
        }

        try
        {
            Logger.Info(LogSource, "OAuth-токен истёк, обновление...");
            var oauthService = new YandexOAuthService(YandexClientId, YandexClientSecret);
            var tokenResult = await oauthService.RefreshTokenAsync(refreshToken);

            if (tokenResult != null)
            {
                SaveTokenResult(tokenResult);
                Logger.Info(LogSource, "OAuth-токен обновлён");

                // Обновляем сервис с новым токеном
                _service = new YandexDiskSyncService(tokenResult.AccessToken!);
            }
            else
            {
                Logger.Warn(LogSource, "Не удалось обновить OAuth-токен");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка обновления токена: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Сохраняет результат OAuth авторизации в настройки.
    /// </summary>
    public static void SaveTokenResult(YandexTokenResponse tokenResult)
    {
        Settings.Default.CloudOAuthToken = tokenResult.AccessToken ?? "";

        if (!string.IsNullOrEmpty(tokenResult.RefreshToken))
        {
            Settings.Default.CloudRefreshToken = tokenResult.RefreshToken;
        }

        // Рассчитываем время истечения
        if (tokenResult.ExpiresIn > 0)
        {
            var expiresAt = DateTime.UtcNow.AddSeconds(tokenResult.ExpiresIn);
            Settings.Default.CloudTokenExpiresAt = expiresAt.ToString("O");
        }

        Settings.Default.Save();
    }

    /// <summary>
    /// Выполняет полный OAuth 2.0 flow авторизации.
    /// Открывает окно авторизации, возвращает true если успешно.
    /// </summary>
    public static async Task<bool> AuthorizeAsync(Window owner)
    {
        var authWindow = new OAuthWindow(YandexClientId, YandexClientSecret);
        authWindow.Owner = owner;
        authWindow.Show();

        // Ждём закрытия окна
        while (authWindow.IsVisible)
        {
            await Task.Delay(100);
        }

        if (authWindow.TokenResult != null)
        {
            SaveTokenResult(authWindow.TokenResult);

            // Обновляем сервис с новым токеном
            _service = new YandexDiskSyncService(authWindow.TokenResult.AccessToken!);
            _isInitialized = true;
            _isTokenRevoked = false;

            Logger.Info(LogSource, "OAuth авторизация завершена успешно");
            StartPolling();
            CloudConnected?.Invoke();
            return true;
        }

        Logger.Info(LogSource, "OAuth авторизация отменена");
        return false;
    }

    /// <summary>
    /// Выходит из учётной записи (удаляет все облачные токены).
    /// </summary>
    public static void Logout()
    {
        StopPolling();

        Settings.Default.CloudOAuthToken = "";
        Settings.Default.CloudRefreshToken = "";
        Settings.Default.CloudTokenExpiresAt = "";
        Settings.Default.CloudRemoteETag = "";
        Settings.Default.CloudLastSyncTime = "";
        Settings.Default.Save();

        _service = null;
        _isInitialized = false;

        Logger.Info(LogSource, "Выход из облачной учётной записи");
    }

    /// <summary>
    /// Проверяет подключение к облаку. Возвращает true, если подключение успешно.
    /// При отозванном токене (401) — выполняет логаут и уведомляет UI через событие TokenRevoked.
    /// </summary>
    public static async Task<bool> CheckConnectionAsync()
    {
        EnsureInitialized();
        if (_service == null) return false;

        var isValid = await _service.CheckConnectionAsync();

        if (!isValid)
        {
            // Дополнительная проверка через REST API — токен может быть отозван
            var oauthService = new YandexOAuthService(YandexClientId, YandexClientSecret);
            var tokenValid = await oauthService.ValidateTokenAsync(Settings.Default.CloudOAuthToken);

            if (!tokenValid)
            {
                Logger.Warn(LogSource, "Токен отозван — выполняется логаут");
                var message = "Токен авторизации Яндекс Диска был отозван. Войдите заново.";
                Logout();
                TokenRevoked?.Invoke(message);
                return false;
            }
        }

        return isValid;
    }

    /// <summary>
    /// Синхронизирует локальный файл с облаком при запуске.
    /// Если в облаке более новая версия — скачивает её.
    /// </summary>
    public static async Task<CloudSyncData?> SyncOnLoadAsync(string localFilePath)
    {
        if (!IsEnabled) return null;
        EnsureInitialized();
        if (_service == null) return null;

        SyncStarted?.Invoke();
        await _syncLock.WaitAsync();
        try
        {
            Logger.Info(LogSource, "Начало синхронизации при загрузке...");

            var remoteInfo = await _service.GetFileInfoAsync();

            if (!remoteInfo.Success)
            {
                Logger.Warn(LogSource, $"Не удалось получить информацию об удалённом файле: {remoteInfo.ErrorMessage}");
                return null;
            }

            if (remoteInfo.RemoteETag == null && remoteInfo.RemoteLastModified == null)
            {
                Logger.Info(LogSource, "Файла в облаке ещё нет");
                return null;
            }

            var localETag = Settings.Default.CloudRemoteETag;

            if (!File.Exists(localFilePath))
            {
                Logger.Info(LogSource, "Локальный файл отсутствует — загрузка из облака");
                var download = await _service.DownloadAsync();
                if (download.Success && download.DownloadedContent != null)
                {
                    UpdateSyncMetadata(download.RemoteETag, download.RemoteLastModified);
                    return new CloudSyncData(download.DownloadedContent, download.RemoteETag);
                }
                return null;
            }

            var localModifiedTime = File.GetLastWriteTimeUtc(localFilePath);

            if (!string.IsNullOrEmpty(localETag) && localETag == remoteInfo.RemoteETag)
            {
                Logger.Info(LogSource, "ETag совпадает — локальный файл актуален");
                return null;
            }

            if (remoteInfo.RemoteLastModified.HasValue
                && remoteInfo.RemoteLastModified.Value > localModifiedTime)
            {
                Logger.Info(LogSource, $"Удалённый файл новее ({remoteInfo.RemoteLastModified} > {localModifiedTime})");
                var download = await _service.DownloadAsync(localETag);

                if (download.Success && download.DownloadedContent != null)
                {
                    UpdateSyncMetadata(download.RemoteETag, download.RemoteLastModified);
                    return new CloudSyncData(download.DownloadedContent, download.RemoteETag);
                }

                if (download.Success && download.DownloadedContent == null)
                    return null;

                return null;
            }

            Logger.Info(LogSource, "Локальный файл актуален или новее");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка синхронизации при загрузке: {ex.Message}", ex);
            return null;
        }
        finally
        {
            _syncLock.Release();
            SyncCompleted?.Invoke();
        }
    }

    /// <summary>
    /// Загружает локальный файл в облако при сохранении.
    /// </summary>
    public static async Task SyncOnSaveAsync(string localFilePath)
    {
        if (!IsEnabled) return;
        if (_isTokenRevoked) return;
        EnsureInitialized();
        if (_service == null) return;

        SyncStarted?.Invoke();
        await _syncLock.WaitAsync();
        try
        {
            if (_isTokenRevoked) { SyncCompleted?.Invoke(); return; }

            if (!File.Exists(localFilePath))
            {
                Logger.Warn(LogSource, "Локальный файл не найден — нечего выгружать");
                SyncCompleted?.Invoke();
                return;
            }

            var content = await File.ReadAllTextAsync(localFilePath);
            var localETag = Settings.Default.CloudRemoteETag;

            Logger.Info(LogSource, "Выгрузка файла в облако...");
            var result = await _service.UploadAsync(content, localETag);

            if (result.Success)
            {
                UpdateSyncMetadata(result.RemoteETag, result.RemoteLastModified);
                Logger.Info(LogSource, "Файл успешно выгружен в облако");
            }
            else if (result.ErrorMessage == "CONFLICT")
            {
                Logger.Warn(LogSource, "Конфликт версий — приоритет облака");
                var download = await _service.DownloadAsync();
                if (download.Success && download.DownloadedContent != null)
                {
                    await File.WriteAllTextAsync(localFilePath, download.DownloadedContent);
                    UpdateSyncMetadata(download.RemoteETag, download.RemoteLastModified);
                    Logger.Info(LogSource, "При конфликте загружена облачная версия");
                }
            }
            else if (IsUnauthorizedError(result.ErrorMessage))
            {
                HandleTokenRevoked();
                return;
            }
            else
            {
                Logger.Warn(LogSource, $"Не удалось выгрузить файл: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка синхронизации при сохранении: {ex.Message}", ex);
        }
        finally
        {
            _syncLock.Release();
            SyncCompleted?.Invoke();
        }
    }

    #region Polling

    /// <summary>
    /// Запускает быстрый polling для обнаружения изменений в облаке.
    /// Проверяет SHA256 файла каждые 5 секунд.
    /// </summary>
    private static void StartPolling()
    {
        StopPolling();
        _pollingCts = new CancellationTokenSource();
        _pollingTask = PollingLoopAsync(_pollingCts.Token);
        Logger.Info(LogSource, $"Polling запущен (интервал: {PollingIntervalSeconds}с)");
    }

    /// <summary>
    /// Останавливает polling.
    /// </summary>
    private static void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
        _pollingTask = null;
    }

    /// <summary>
    /// Бесконечный цикл polling: проверяет SHA256 файла каждые N секунд.
    /// При обнаружении изменений — уведомляет UI через CloudDataUpdated.
    /// </summary>
    private static async Task PollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_isTokenRevoked || _service == null || !IsEnabled)
                {
                    await Task.Delay(PollingIntervalSeconds * 1000, cancellationToken);
                    continue;
                }

                var localETag = Settings.Default.CloudRemoteETag;
                var remoteInfo = await _service.GetFileInfoAsync();

                // Проверяем, не был ли polling отменён пока мы ждали ответа
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Проверяем, не отозван ли токен
                if (!remoteInfo.Success && IsUnauthorizedError(remoteInfo.ErrorMessage))
                {
                    Logger.Warn(LogSource, "Токен отозван (401) — обнаружено в polling");
                    HandleTokenRevoked();
                    break;
                }

                if (remoteInfo.Success && remoteInfo.RemoteETag != null)
                {
                    if (string.IsNullOrEmpty(localETag) || localETag != remoteInfo.RemoteETag)
                    {
                        Logger.Info(LogSource, $"Обнаружено обновление в облаке: SHA256={remoteInfo.RemoteETag}");
                        UpdateSyncMetadata(remoteInfo.RemoteETag, remoteInfo.RemoteLastModified);

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            CloudDataUpdated?.Invoke();
                        });
                    }
                }

                // Ждём интервал перед следующей проверкой
                await Task.Delay(PollingIntervalSeconds * 1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                HandleTokenRevoked();
                break;
            }
            catch (Exception ex)
            {
                if (IsUnauthorizedError(ex.Message))
                {
                    HandleTokenRevoked();
                    break;
                }

                Logger.Debug(LogSource, $"Ошибка polling: {ex.Message}");

                try
                {
                    await Task.Delay(PollingIntervalSeconds * 1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Logger.Info(LogSource, "Polling остановлен");
    }

    #endregion

    #region Helpers

    private static void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            Initialize();
        }
    }

    private static void UpdateSyncMetadata(string? eTag, DateTime? lastModified)
    {
        if (!string.IsNullOrEmpty(eTag))
            Settings.Default.CloudRemoteETag = eTag;

        if (lastModified.HasValue)
            Settings.Default.CloudLastSyncTime = lastModified.Value.ToString("O");

        Settings.Default.Save();
    }

    /// <summary>
    /// Проверяет, содержит ли сообщение об ошибке указание на 401 Unauthorized.
    /// </summary>
    private static bool IsUnauthorizedError(string? errorMessage)
    {
        return errorMessage?.Contains("401", StringComparison.OrdinalIgnoreCase) == true
               || errorMessage?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Обрабатывает отзыв токена: ставит флаг, выполняет логаут, уведомляет UI.
    /// </summary>
    private static void HandleTokenRevoked()
    {
        if (_isTokenRevoked) return;

        _isTokenRevoked = true;
        Logger.Warn(LogSource, "Токен отозван (401) — синхронизация остановлена");

        var message = "Токен авторизации Яндекс Диска был отозван. Войдите заново.";
        Logout();
        
        // Вызываем через Dispatcher, т.к. HandleTokenRevoked может быть вызван из фонового потока
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            TokenRevoked?.Invoke(message);
        });
    }

    #endregion
}
