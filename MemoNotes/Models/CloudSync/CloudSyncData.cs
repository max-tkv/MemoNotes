/// <summary>
/// Данные, полученные из облака при синхронизации.
/// </summary>
public class CloudSyncData
{
    public string Content { get; }
    public string? ETag { get; }
    
    public CloudSyncData(string content, string? eTag)
    {
        Content = content;
        ETag = eTag;
    }
}