using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MemoNotes.Service.Logging;

namespace MemoNotes.Service.CloudSync;

/// <summary>
/// Сервис авторизации Яндекс OAuth 2.0 для нативных (desktop) приложений.
/// Flow: открываем браузер → пользователь авторизуется → копирует код подтверждения → вставляет в приложение.
/// </summary>
public class YandexOAuthService
{
    private const string LogSource = "YandexOAuth";
    
    private const string AuthorizeUrl = "https://oauth.yandex.ru/authorize";
    private const string TokenUrl = "https://oauth.yandex.ru/token";
    private const string UserInfoUrl = "https://login.yandex.ru/info";
    
    /// <summary>
    /// ID приложения OAuth (регистрируется на https://oauth.yandex.ru/).
    /// Тип: «Нативные приложения».
    /// </summary>
    public string ClientId { get; }
    
    /// <summary>
    /// Секрет приложения. Для нативных приложений не обязателен.
    /// </summary>
    public string? ClientSecret { get; }

    public YandexOAuthService(string clientId, string? clientSecret = null)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    /// <summary>
    /// Формирует URL для авторизации в браузере.
    /// Для нативных приложений Яндекс перенаправляет на https://oauth.yandex.ru/verification_code
    /// с отображением кода подтверждения.
    /// </summary>
    public string GetAuthorizationUrl()
    {
        var scopes = new[]
        {
            "cloud_api:disk.app_folder", // Доступ к папке приложения на Диске
            "cloud_api:disk.read",       // Чтение файлов и метаинформации
            "cloud_api:disk.write",      // Создание папок и загрузка файлов
            "cloud_api:disk.info"        // Информация о диске
        };
        
        return $"{AuthorizeUrl}?" +
               $"response_type=code" +
               $"&client_id={Uri.EscapeDataString(ClientId)}" +
               $"&scope={Uri.EscapeDataString(string.Join(" ", scopes))}";
    }

    /// <summary>
    /// Обменивает авторизационный код (введённый пользователем вручную) на access_token и refresh_token.
    /// </summary>
    public async Task<YandexTokenResponse?> ExchangeCodeForTokenAsync(string code)
    {
        try
        {
            using var http = new HttpClient();
            
            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId
            };
            
            if (!string.IsNullOrEmpty(ClientSecret))
            {
                parameters["client_secret"] = ClientSecret;
            }
            
            var content = new FormUrlEncodedContent(parameters);
            var response = await http.PostAsync(TokenUrl, content);
            
            var json = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Logger.Error(LogSource, $"Ошибка обмена кода на токен: HTTP {response.StatusCode}, {json}");
                return null;
            }
            
            var tokenResponse = JsonSerializer.Deserialize<YandexTokenResponse>(json);
            
            if (tokenResponse?.AccessToken == null)
            {
                Logger.Error(LogSource, $"Пустой токен в ответе: {json}");
                return null;
            }
            
            Logger.Info(LogSource, $"Токен получен успешно, expires_in={tokenResponse.ExpiresIn}");
            return tokenResponse;
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка обмена кода на токен: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Обновляет access_token с помощью refresh_token.
    /// </summary>
    public async Task<YandexTokenResponse?> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            using var http = new HttpClient();
            
            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId
            };
            
            if (!string.IsNullOrEmpty(ClientSecret))
            {
                parameters["client_secret"] = ClientSecret;
            }
            
            var content = new FormUrlEncodedContent(parameters);
            var response = await http.PostAsync(TokenUrl, content);
            
            var json = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Logger.Error(LogSource, $"Ошибка обновления токена: HTTP {response.StatusCode}, {json}");
                return null;
            }
            
            var tokenResponse = JsonSerializer.Deserialize<YandexTokenResponse>(json);
            
            if (tokenResponse?.AccessToken == null)
            {
                Logger.Error(LogSource, $"Пустой токен при обновлении: {json}");
                return null;
            }
            
            Logger.Info(LogSource, $"Токен обновлён, expires_in={tokenResponse.ExpiresIn}");
            return tokenResponse;
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка обновления токена: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Получает информацию о пользователе Яндекс по access_token.
    /// </summary>
    public async Task<string?> GetUserInfoAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
            
            var response = await http.GetAsync($"{UserInfoUrl}?format=json");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка получения информации о пользователе: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Проверяет валидность access_token через REST API Яндекс Диска.
    /// Возвращает true если токен действителен, false — если истёк или отозван.
    /// </summary>
    public async Task<bool> ValidateTokenAsync(string accessToken)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
            
            var response = await http.GetAsync("https://cloud-api.yandex.net/v1/disk/");
            
            if (response.IsSuccessStatusCode)
                return true;
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Logger.Error(LogSource, "Токен отозван или недействителен (401 Unauthorized)");
                return false;
            }
            
            // Другие ошибки (сеть, сервер) — считаем токен валидным, проблема в другом
            Logger.Warn(LogSource, $"Проверка токена: HTTP {response.StatusCode}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(LogSource, $"Ошибка проверки токена: {ex.Message}");
            // При сетевых ошибках не считаем токен невалидным
            return true;
        }
    }
}
