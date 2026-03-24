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
}
