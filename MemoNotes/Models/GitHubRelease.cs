using System.Text.Json.Serialization;

namespace MemoNotes.Models;

/// <summary>
/// Модель релиза GitHub API.
/// </summary>
public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTime PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    /// <summary>
    /// Возвращает текст для отображения в окне обновления.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : TagName;

    /// <summary>
    /// Возвращает описание релиза или текст "Описание отсутствует".
    /// </summary>
    [JsonIgnore]
    public string DisplayBody => !string.IsNullOrWhiteSpace(Body) 
        ? Body 
        : "Описание изменений отсутствует.";
}
