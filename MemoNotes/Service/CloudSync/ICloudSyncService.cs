using MemoNotes.Models;

namespace MemoNotes.Service.CloudSync;

/// <summary>
/// Интерфейс сервиса синхронизации с облачным хранилищем.
/// </summary>
public interface ICloudSyncService
{
    /// <summary>
    /// Проверяет доступность облака и корректность OAuth-токена.
    /// </summary>
    Task<bool> CheckConnectionAsync();
    
    /// <summary>
    /// Загрузить файл доски из облака.
    /// </summary>
    /// <param name="localETag">ETag локального файла для проверки конфликтов.</param>
    Task<CloudSyncResult> DownloadAsync(string? localETag = null);
    
    /// <summary>
    /// Загрузить файл доски в облако.
    /// </summary>
    /// <param name="content">Содержимое файла доски.</param>
    /// <param name="localETag">ETag для проверки конфликтов (if-match).</param>
    Task<CloudSyncResult> UploadAsync(string content, string? localETag = null);
    
    /// <summary>
    /// Получить информацию о файле в облаке (последнее изменение, ETag).
    /// </summary>
    Task<CloudSyncResult> GetFileInfoAsync();
}
