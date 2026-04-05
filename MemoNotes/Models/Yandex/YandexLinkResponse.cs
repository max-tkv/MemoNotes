using System.Text.Json.Serialization;

namespace MemoNotes.Models;

/// <summary>
/// Ответ API Яндекс Диска с ссылкой для загрузки или скачивания файла.
/// GET /v1/disk/resources/upload и GET /v1/disk/resources/download.
/// </summary>
public class YandexLinkResponse
{
    /// <summary>
    /// URL для загрузки (PUT) или скачивания (GET) файла.
    /// </summary>
    [JsonPropertyName("href")]
    public string? Href { get; set; }
    
    ///summary>
    /// HTTP-метод для использования ссылки.
    /// </summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }
    
    /// <summary>
    /// Является ли ссылка шаблоном (true — нужно подставить значения).
    /// </summary>
    [JsonPropertyName("templated")]
    public bool Templated { get; set; }
}
