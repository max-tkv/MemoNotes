namespace MemoNotes.Enums;

/// <summary>
/// Поддерживаемые облачные провайдеры для синхронизации.
/// </summary>
public enum CloudProvider
{
    /// <summary>Синхронизация отключена.</summary>
    None = 0,
    
    /// <summary>Яндекс Диск (через WebDAV API).</summary>
    YandexDisk = 1
}
