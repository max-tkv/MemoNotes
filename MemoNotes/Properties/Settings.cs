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
}
