namespace Customers.Core;

/// <summary>Ключи источников. Совпадают с ключами Python- и macOS-версий, чтобы лиды оставались привязаны к источникам.</summary>
public static class SourceKey
{
    public const string Rss = "fl_ru";
    public const string Telegram = "telegram_channels";
    public const string Vk = "vk_communities";
    public const string Osm = "osm_organizations";

    public static readonly IReadOnlyList<string> All = [Rss, Telegram, Vk, Osm];
}

/// <summary>Человекочитаемые названия источников и честные условия доступа.</summary>
public static class SourceCatalog
{
    public sealed record Info(string Title, string Access, string Note);

    public static readonly IReadOnlyDictionary<string, Info> Known = new Dictionary<string, Info> {
        ["fl_ru"] = new("RSS-ленты", "Бесплатно",
            "Любые публичные RSS- и Atom-ленты заказов, например FL.ru. Ленты хранят только последние записи."),
        ["telegram_channels"] = new("Telegram-каналы", "Бесплатно",
            "Только публичные каналы с веб-превью t.me/s. Bot API чужие каналы не читает."),
        ["vk_communities"] = new("VK-сообщества", "Бесплатный ключ",
            "Нужен бесплатный сервисный ключ VK (dev.vk.com). Читаются стены выбранных сообществ."),
        ["osm_organizations"] = new("OpenStreetMap", "Бесплатно",
            "Overpass API, данные ODbL (© участники OpenStreetMap). Полнота в РФ ниже, чем у Яндекса и 2ГИС."),
        ["site_checker"] = new("Проверка сайтов", "Бесплатно",
            "Один запрос к главной странице: доступность, HTTPS, мобильная вёрстка, год, онлайн-запись."),
    };

    /// <summary>Источники, которые сознательно не подключены, с причиной.</summary>
    public static readonly IReadOnlyList<(string Name, string Reason)> Unavailable =
    [
        ("Яндекс Карты", "Бесплатные условия запрещают сохранять данные."),
        ("2ГИС", "Бесплатно только демо-ключ на 1 месяц."),
        ("Поисковые системы", "Официальные API платные или закрыты для новых клиентов."),
        ("Kwork, Habr Freelance", "Нет API и RSS, robots.txt запрещает автоматический сбор."),
    ];

    public static string Title(string key)
    {
        if (Known.TryGetValue(key, out var info))
        {
            return info.Title;
        }

        return key == "import" ? "Импорт" : key;
    }

    /// <summary>Обратный поиск по названию (для импорта экспортированных файлов).</summary>
    public static string KeyForTitle(string title)
    {
        var t = title.Trim();
        foreach (var (key, info) in Known)
        {
            if (string.Equals(info.Title, t, StringComparison.OrdinalIgnoreCase) || string.Equals(key, t, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return t.Length == 0 ? "import" : t;
    }
}
