namespace MemoNotes.Models;

/// <summary>
/// Результат операции синхронизации с облаком.
/// </summary>
public class CloudSyncResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? RemoteLastModified { get; set; }
    public string? RemoteETag { get; set; }
    
    /// <summary>
    /// Данные файла из облака (при скачивании).
    /// </summary>
    public string? DownloadedContent { get; set; }

    public static CloudSyncResult Ok(DateTime? lastModified = null, string? eTag = null) => new()
    {
        Success = true,
        RemoteLastModified = lastModified,
        RemoteETag = eTag
    };

    public static CloudSyncResult Fail(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}
