using System.Text.Json.Serialization;

namespace MemoNotes.Models;

/// <summary>
/// Ответ API Яндекс Диска с информацией о ресурсе.
/// GET /v1/disk/resources
/// </summary>
public class YandexResourceResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
    
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
    
    /// <summary>
    /// Размер файла в байтах.
    /// </summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
    
    /// <summary>
    /// Дата и время изменения ресурса (ISO 8601).
    /// </summary>
    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; }
    
    /// <summary>
    /// Дата и время создания ресурса (ISO 8601).
    /// </summary>
    [JsonPropertyName("created")]
    public DateTime Created { get; set; }
    
    /// <summary>
    /// Публичный ключ для доступа к ресурсу.
    /// </summary>
    [JsonPropertyName("public_key")]
    public string? PublicKey { get; set; }
    
    [JsonPropertyName("custom_properties")]
    public Dictionary<string, object>? CustomProperties { get; set; }

    /// <summary>
    /// Описание ошибки (если запрос вернул ошибку).
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
