namespace Customers.Presentation;

/// <summary>Разделы левой панели.</summary>
public enum NavSection
{
    Home,
    All,
    New,
    Favorites,
    Archive,
    Excluded,
    Sources,
    Runs,
    Settings,
}

public static class NavSections
{
    public static readonly IReadOnlyList<NavSection> Lists = [NavSection.All, NavSection.New, NavSection.Favorites, NavSection.Archive, NavSection.Excluded];

    public static string Title(this NavSection s) => s switch {
        NavSection.Home => "Главная",
        NavSection.All => "Все лиды",
        NavSection.New => "Новые",
        NavSection.Favorites => "Избранное",
        NavSection.Archive => "Архив",
        NavSection.Excluded => "Исключённые",
        NavSection.Sources => "Источники",
        NavSection.Runs => "Журнал поиска",
        _ => "Настройки",
    };

    /// <summary>Код значка Segoe MDL2 Assets / Segoe Fluent Icons (есть и в Windows 10, и в Windows 11).</summary>
    public static string Glyph(this NavSection s) => s switch {
        NavSection.Home => "",
        NavSection.All => "",
        NavSection.New => "",
        NavSection.Favorites => "",
        NavSection.Archive => "",
        NavSection.Excluded => "",
        NavSection.Sources => "",
        NavSection.Runs => "",
        _ => "",
    };

    public static bool IsLeadList(this NavSection s) => s is NavSection.All or NavSection.New or NavSection.Favorites or NavSection.Archive or NavSection.Excluded;

    public static string Key(this NavSection s) => s.ToString().ToLowerInvariant();

    public static NavSection FromKey(string? key) =>
        Enum.TryParse<NavSection>(key, true, out var s) && s != NavSection.Settings ? s : NavSection.All;
}

/// <summary>Столбец сортировки таблицы лидов.</summary>
public enum SortColumn
{
    Score,
    Title,
    Kind,
    Source,
    Region,
    Found,
    Relevance,
}
