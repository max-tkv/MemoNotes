using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MemoNotes.Service.CloudSync;
using MemoNotes.Service.Logging;

namespace MemoNotes;

/// <summary>
/// Окно авторизации через Яндекс OAuth 2.0 с использованием WebView2.
/// Открывает страницу авторизации Яндекса внутри встроенного браузера,
/// автоматически извлекает код подтверждения со страницы verification_code
/// и обменивает его на access_token.
/// </summary>
public partial class OAuthWindow : Window
{   
    private const string VerificationUrl = "https://oauth.yandex.ru/verification_code";
    
    private readonly YandexOAuthService _oauthService;
    private readonly string _authorizationUrl;
    private WebView2? _webView;
    private bool _isCompleted;

    /// <summary>
    /// Результат авторизации — полученные токены.
    /// </summary>
    public YandexTokenResponse? TokenResult { get; private set; }

    public OAuthWindow(string clientId, string? clientSecret = null)
    {
        _oauthService = new YandexOAuthService(clientId, clientSecret);
        _authorizationUrl = _oauthService.GetAuthorizationUrl();
        
        InitializeComponent();
        
        Loaded += OAuthWindow_Loaded;
    }

    private async void OAuthWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Создаём WebView2 программно
            _webView = new WebView2();
            WebViewContainer.Children.Add(_webView);
            _webView.Visibility = Visibility.Collapsed;
            
            // Инициализируем WebView2
            await _webView.EnsureCoreWebView2Async(null);
            
            _webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            
            Logger.Info<OAuthWindow>("WebView2 инициализирован, переход к авторизации...");
            
            // Открываем страницу авторизации
            _webView.Source = new Uri(_authorizationUrl);

            Logger.Info<OAuthWindow>($"Uri авторизации: {_authorizationUrl}");
        }
        catch (Exception ex)
        {
            Logger.Error<OAuthWindow>($"Ошибка инициализации WebView2: {ex.Message}", ex);
            System.Windows.MessageBox.Show(
                $"Не удалось инициализировать встроенный браузер.\n\nОшибка: {ex.Message}\n\n" +
                "Убедитесь, что установлен Microsoft Edge WebView2 Runtime.",
                "Ошибка авторизации",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            
            _isCompleted = false;
            Close();
        }
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_webView == null) return;
        
        try
        {
            if (!e.IsSuccess)
            {
                Logger.Warn<OAuthWindow>($"Ошибка навигации: {e.WebErrorStatus}");
                return;
            }
            
            var url = _webView.Source?.ToString() ?? "";
            Logger.Info<OAuthWindow>($"Навигация завершена: {url}");
            
            // Проверяем, попали ли на страницу verification_code
            if (url.StartsWith(VerificationUrl))
            {
                // Скрываем индикатор загрузки, показываем WebView
                Dispatcher.Invoke(() =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    _webView!.Visibility = Visibility.Visible;
                });
                
                // Даём странице время для рендеринга и извлекаем код
                await Task.Delay(2000);
                
                var code = await ExtractVerificationCodeAsync();
                
                if (!string.IsNullOrEmpty(code))
                {
                    Logger.Info<OAuthWindow>($"Код подтверждения извлечён: {code}");
                    
                    // Показываем пользователю, что код получен
                    await ExecuteScriptAsync(@"
                        document.body.innerHTML = `
                            <div style='display:flex;justify-content:center;align-items:center;height:100vh;
                                        margin:0;background:#1a1a2e;color:#fff;font-family:Segoe UI,sans-serif;'>
                                <div style='text-align:center;padding:40px;border-radius:12px;
                                            background:#16213e;box-shadow:0 4px 20px rgba(0,0,0,0.3);'>
                                    <h1 style='color:#4caf50;font-size:2em;margin-bottom:10px;'>✓</h1>
                                    <p style='color:#aaa;font-size:1.1em;'>Код подтверждения получен</p>
                                    <p style='color:#666;font-size:0.9em;margin-top:15px;'>Получаю токены...</p>
                                </div>
                            </div>`;
                    ");
                    
                    // Обмениваем код на токен
                    var tokenResult = await _oauthService.ExchangeCodeForTokenAsync(code);
                    
                    if (tokenResult != null)
                    {
                        TokenResult = tokenResult;
                        _isCompleted = true;
                        
                        // Получаем информацию о пользователе
                        var userInfo = await _oauthService.GetUserInfoAsync(tokenResult.AccessToken!);
                        string? userName = null;
                        if (userInfo != null)
                        {
                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(userInfo);
                                if (doc.RootElement.TryGetProperty("display_name", out var nameEl))
                                    userName = nameEl.GetString();
                                else if (doc.RootElement.TryGetProperty("login", out var loginEl))
                                    userName = loginEl.GetString();
                            }
                            catch { }
                        }
                        
                        var displayName = !string.IsNullOrEmpty(userName) ? $" ({userName})" : "";
                        
                        await ExecuteScriptAsync(@$"
                            document.body.innerHTML = `
                                <div style='display:flex;justify-content:center;align-items:center;height:100vh;
                                            margin:0;background:#1a1a2e;color:#fff;font-family:Segoe UI,sans-serif;'>
                                    <div style='text-align:center;padding:40px;border-radius:12px;
                                                background:#16213e;box-shadow:0 4px 20px rgba(0,0,0,0.3);'>
                                        <h1 style='color:#4caf50;font-size:2em;margin-bottom:10px;'>✓</h1>
                                        <p style='color:#fff;font-size:1.2em;'>Авторизация успешна{displayName}</p>
                                        <p style='color:#666;font-size:0.9em;margin-top:10px;'>Окно закроется автоматически...</p>
                                    </div>
                                </div>`;
                        ");
                        
                        Logger.Info<OAuthWindow>($"Авторизация успешна для пользователя: {userName}");
                        
                        // Закрываем через 2 секунды
                        await Task.Delay(2000);
                        Dispatcher.Invoke(() => Close());
                    }
                    else
                    {
                        await ExecuteScriptAsync(@"
                            document.body.innerHTML = `
                                <div style='display:flex;justify-content:center;align-items:center;height:100vh;
                                            margin:0;background:#1a1a2e;color:#fff;font-family:Segoe UI,sans-serif;'>
                                    <div style='text-align:center;padding:40px;border-radius:12px;
                                                background:#16213e;box-shadow:0 4px 20px rgba(0,0,0,0.3);'>
                                        <h1 style='color:#f44336;font-size:2em;margin-bottom:10px;'>✗</h1>
                                        <p style='color:#aaa;font-size:1.1em;'>Не удалось получить токен</p>
                                        <p style='color:#666;font-size:0.9em;margin-top:10px;'>Проверьте настройки приложения на oauth.yandex.ru</p>
                                    </div>
                                </div>`;
                        ");
                        
                        Logger.Error<OAuthWindow>("Не удалось обменять код на токен");
                    }
                }
                else
                {
                    Logger.Warn<OAuthWindow>("Не удалось извлечь код подтверждения со страницы");
                }
            }
            else
            {
                // Скрываем индикатор загрузки, показываем WebView для других страниц
                Dispatcher.Invoke(() =>
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    _webView!.Visibility = Visibility.Visible;
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error<OAuthWindow>($"Ошибка обработки навигации: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Извлекает код подтверждения из DOM-дерева страницы verification_code.
    /// Яндекс отображает код в элементе с классом "verification-code__code" или внутри нумерованного списка.
    /// </summary>
    private async Task<string?> ExtractVerificationCodeAsync()
    {
        try
        {
            // JavaScript для извлечения кода из разных мест на странице
            var jsGetCode = @"
                (function() {
                    // Способ 1: Яндекс может показывать код в элементе с классом
                    var codeEl = document.querySelector('.verification-code__code');
                    if (codeEl) return codeEl.textContent.trim();
                    
                    // Способ 2: Код в нумерованном списке (старый формат)
                    var noteEl = document.querySelector('ol li');
                    if (noteEl) {
                        var text = noteEl.textContent.trim();
                        if (text.length >= 10 && text.length <= 30) return text;
                    }
                    
                    // Способ 3: Код в notice/code блоках
                    var notices = document.querySelectorAll('.notice, .code, [class*=""code""]');
                    for (var i = 0; i < notices.length; i++) {
                        var t = notices[i].textContent.trim().replace(/\s+/g, '');
                        if (t.length >= 10 && t.length <= 30 && /^[a-z0-9]+$/i.test(t)) return t;
                    }
                    
                    // Способ 4: Ищем прямой текст в элементах, похожий на код
                    var allElements = document.querySelectorAll('p, span, div, li, td, code, pre, h1, h2, h3');
                    for (var i = 0; i < allElements.length; i++) {
                        var el = allElements[i];
                        var directText = '';
                        for (var j = 0; j < el.childNodes.length; j++) {
                            if (el.childNodes[j].nodeType === 3) {
                                directText += el.childNodes[j].textContent;
                            }
                        }
                        directText = directText.trim().replace(/\s+/g, '');
                        if (directText.length >= 10 && directText.length <= 30 && /^[a-z0-9]+$/i.test(directText)) {
                            return directText;
                        }
                    }
                    
                    return '';
                })();
            ";
            
            var result = await ExecuteScriptAsync(jsGetCode);
            
            if (!string.IsNullOrEmpty(result))
            {
                result = result.Trim();
                if (result.StartsWith("\"") && result.EndsWith("\""))
                {
                    result = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? "";
                }
                
                if (!string.IsNullOrWhiteSpace(result) && result.Length >= 10)
                {
                    return result;
                }
            }
            
            // Fallback: Получаем весь текст страницы и ищем код по regex
            var jsGetPageText = @"document.body.innerText";
            var pageText = await ExecuteScriptAsync(jsGetPageText);
            
            if (!string.IsNullOrEmpty(pageText))
            {
                pageText = pageText.Trim();
                if (pageText.StartsWith("\"") && pageText.EndsWith("\""))
                {
                    pageText = System.Text.Json.JsonSerializer.Deserialize<string>(pageText) ?? "";
                }
                
                // Ищем код по ключевым словам
                var match = Regex.Match(pageText, @"(?:код|code)[:\s]+([a-z0-9]{10,30})", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                
                // Альтернативный поиск — длинная буквенно-цифровая последовательность
                match = Regex.Match(pageText, @"\b([a-z0-9]{15,30})\b", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error<OAuthWindow>($"Ошибка извлечения кода: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Выполняет JavaScript в WebView2 и возвращает результат.
    /// </summary>
    private async Task<string> ExecuteScriptAsync(string script)
    {
        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                return await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
        }
        catch (Exception ex)
        {
            Logger.Error<OAuthWindow>($"Ошибка выполнения скрипта: {ex.Message}");
        }
        return "";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _isCompleted = false;
        Close();
    }
}
