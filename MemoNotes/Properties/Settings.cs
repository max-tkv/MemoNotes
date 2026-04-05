using System.Configuration;

namespace MemoNotes.Properties;

internal sealed class Settings : ApplicationSettingsBase
{
    private static readonly Settings defaultInstance = 
        (Settings)Synchronized(new Settings());

    public static Settings Default => defaultInstance;

    [UserScopedSetting]
    [DefaultSettingValue("1000")]
    public double BoardWindowWidth
    {
        get => (double)this["BoardWindowWidth"];
        set => this["BoardWindowWidth"] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("700")]
    public double BoardHeight
    {
        get => (double)this["BoardHeight"];
        set => this["BoardHeight"] = value;
    }
    
    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool TopmostTextBoxWindow
    {
        get => (bool)this["TopmostTextBoxWindow"];
        set => this["TopmostTextBoxWindow"] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("1")]
    public int StartupCorner
    {
        get => (int)this["StartupCorner"];
        set => this["StartupCorner"] = value;
    }

    /// <summary>
    /// Клавиша непрерывного рисования (VK-код). Значение 0 = отключено.
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("0")]
    public int ContinuousStrokeKey
    {
        get => (int)this["ContinuousStrokeKey"];
        set => this["ContinuousStrokeKey"] = value;
    }

    /// <summary>
    /// Модификатор клавиши непрерывного рисования: 1=Shift, 2=Ctrl, 4=Alt (битовая маска).
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("0")]
    public int ContinuousStrokeModifier
    {
        get => (int)this["ContinuousStrokeModifier"];
        set => this["ContinuousStrokeModifier"] = value;
    }

    /// <summary>
    /// Дата отклонения обновления в формате ISO 8601 (yyyy-MM-dd).
    /// Пустая строка означает, что обновление не отклонялось.
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string UpdateDismissedDate
    {
        get => (string)this["UpdateDismissedDate"];
        set => this["UpdateDismissedDate"] = value;
    }

    /// <summary>
    /// Отклонена ли версия обновления до конца текущего дня.
    /// </summary>
    public bool IsUpdateDismissedToday()
    {
        if (string.IsNullOrWhiteSpace(UpdateDismissedDate))
            return false;

        if (DateTime.TryParse(UpdateDismissedDate, out var dismissedDate))
        {
            var today = DateTime.Now.Date;
            return dismissedDate.Date == today;
        }

        return false;
    }

    #region Облачная синхронизация

    /// <summary>
    /// Провайдер облачного хранилища. 0 = отключено, 1 = Яндекс Диск.
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("0")]
    public int CloudProvider
    {
        get => (int)this["CloudProvider"];
        set => this["CloudProvider"] = value;
    }

    /// <summary>
    /// OAuth-токен для доступа к облачному хранилищу.
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string CloudOAuthToken
    {
        get => (string)this["CloudOAuthToken"];
        set => this["CloudOAuthToken"] = value;
    }

    /// <summary>
    /// Refresh-токен для обновления OAuth-токена.
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string CloudRefreshToken
    {
        get => (string)this["CloudRefreshToken"];
        set => this["CloudRefreshToken"] = value;
    }

    /// <summary>
    /// Дата/время истечения действия OAuth-токена (UTC, ISO 8601).
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string CloudTokenExpiresAt
    {
        get => (string)this["CloudTokenExpiresAt"];
        set => this["CloudTokenExpiresAt"] = value;
    }

    /// <summary>
    /// Проверяет, истёк ли OAuth-токен (с запасом 5 минут).
    /// </summary>
    public bool IsCloudTokenExpired()
    {
        if (string.IsNullOrWhiteSpace(CloudTokenExpiresAt))
            return true;
        
        if (DateTime.TryParse(CloudTokenExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return DateTime.UtcNow >= expiresAt.AddMinutes(-5);
        }
        
        return true;
    }

    /// <summary>
    /// ETag последней синхронизированной версии файла (для определения конфликтов).
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string CloudRemoteETag
    {
        get => (string)this["CloudRemoteETag"];
        set => this["CloudRemoteETag"] = value;
    }

    /// <summary>
    /// Время последней синхронизации в формате ISO 8601 (roundtrip).
    /// </summary>
    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string CloudLastSyncTime
    {
        get => (string)this["CloudLastSyncTime"];
        set => this["CloudLastSyncTime"] = value;
    }

    #endregion
}
