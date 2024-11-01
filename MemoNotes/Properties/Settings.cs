using System.Configuration;

namespace MemoNotes.Properties;

internal sealed class Settings : ApplicationSettingsBase
{
    private static readonly Settings defaultInstance = 
        (Settings)Synchronized(new Settings());

    public static Settings Default => defaultInstance;

    [UserScopedSetting]
    [DefaultSettingValue("800")]
    public double WindowWidth
    {
        get => (double)this["WindowWidth"];
        set => this["WindowWidth"] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("600")]
    public double WindowHeight
    {
        get => (double)this["WindowHeight"];
        set => this["WindowHeight"] = value;
    }
    
    [UserScopedSetting]
    [DefaultSettingValue("True")]
    public bool TopmostTextBoxWindow
    {
        get => (bool)this["TopmostTextBoxWindow"];
        set => this["TopmostTextBoxWindow"] = value;
    }
}