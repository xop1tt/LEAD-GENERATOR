namespace Customers.Ui;

/// <summary>Разделы настроек (в таком порядке они показаны в списке слева).</summary>
public enum SettingsSection
{
    Appearance,
    Account,
    Search,
    Sources,
    Filters,
    Exclusions,
    Integrations,
    Cloud,
    Backup,
    ExportImport,
    Notifications,
    Data,
    About,
}

public static class SettingsSections
{
    public static string Title(this SettingsSection s) => s switch {
        SettingsSection.Appearance => "Внешний вид",
        SettingsSection.Account => "Учётная запись",
        SettingsSection.Search => "Поиск",
        SettingsSection.Sources => "Источники",
        SettingsSection.Filters => "Фильтры",
        SettingsSection.Exclusions => "Исключения",
        SettingsSection.Integrations => "Интеграции",
        SettingsSection.Cloud => "Облачная синхронизация",
        SettingsSection.Backup => "Резервные копии",
        SettingsSection.ExportImport => "Экспорт и импорт",
        SettingsSection.Notifications => "Уведомления",
        SettingsSection.Data => "Данные",
        _ => "О программе",
    };

    public static string Glyph(this SettingsSection s) => s switch {
        SettingsSection.Appearance => "",
        SettingsSection.Account => "",
        SettingsSection.Search => "",
        SettingsSection.Sources => "",
        SettingsSection.Filters => "",
        SettingsSection.Exclusions => "",
        SettingsSection.Integrations => "",
        SettingsSection.Cloud => "",
        SettingsSection.Backup => "",
        SettingsSection.ExportImport => "",
        SettingsSection.Notifications => "",
        SettingsSection.Data => "",
        _ => "",
    };
}
