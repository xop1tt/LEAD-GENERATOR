using System.Globalization;

namespace Customers.Presentation;

/// <summary>Строка таблицы лидов: подготовленные для показа значения. Данные лида не копируются.</summary>
public sealed class LeadRow : ObservableObject
{
    private LeadItem _item;

    public LeadRow(LeadItem item) => _item = item;

    public LeadItem Item => _item;

    public string Id => _item.Id;

    public LeadData Data => _item.Data;

    public string Title => string.IsNullOrWhiteSpace(Data.Title) ? "(без названия)" : Data.Title;

    public string ScoreText => ((long)Math.Round(Data.Score, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    public double Score => Data.Score;

    /// <summary>high / medium / low / rejected — по нему интерфейс выбирает цвет.</summary>
    public string Level => Data.Relevance.Raw();

    public string RelevanceTitle => Data.Relevance.Title();

    public string KindTitle => Data.Kind.Title();

    public string KindGlyph => Data.Kind == LeadKind.Order ? "" : "";

    public string SourceTitle => SourceCatalog.Title(Data.Source);

    public string Region => Data.Region ?? "";

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Data.Category ?? Data.ProjectType))
            {
                parts.Add(Data.Category ?? Data.ProjectType!);
            }

            if (Data.Budget is { } b)
            {
                parts.Add($"{(long)b} {Data.Currency}".Trim());
            }

            if (!string.IsNullOrEmpty(Data.Phone))
            {
                parts.Add(Data.Phone);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Вторая строка таблицы: тип, регион, категория, бюджет, телефон.</summary>
    public string Details => string.Join(" · ", new[] { KindTitle, Region, Subtitle }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public string FoundText => _item.DiscoveredAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);

    public bool IsFavorite => _item.Favorite;

    public bool IsNew => !_item.Seen;

    public bool IsArchived => _item.Archived;

    public bool IsExcluded => _item.Excluded;

    public string FavoriteGlyph => _item.Favorite ? "" : "";

    /// <summary>Заменяет данные и обновляет все значения (после действия пользователя или синхронизации).</summary>
    public void Update(LeadItem item)
    {
        _item = item;
        RaiseAll(string.Empty);
    }
}

/// <summary>Порядок и фильтры таблицы. Вынесены из модели, чтобы проверяться отдельно.</summary>
public static class LeadQuery
{
    public static bool InSection(LeadItem l, NavSection s) => s switch {
        NavSection.All => !l.Archived && !l.Excluded,
        NavSection.New => !l.Seen && !l.Archived && !l.Excluded,
        NavSection.Favorites => l.Favorite && !l.Excluded,
        NavSection.Archive => l.Archived && !l.Excluded,
        NavSection.Excluded => l.Excluded,
        _ => false,
    };

    public static bool Matches(LeadItem l, string search)
    {
        var q = search.Trim();
        if (q.Length == 0)
        {
            return true;
        }

        var d = l.Data;
        IEnumerable<string> hay = [d.Title, d.Description, d.Address ?? "", d.Phone ?? "", d.Website ?? "", d.Region ?? "",
            d.Category ?? "", d.CustomerName ?? "", l.Note, .. d.Contacts.Values];
        return hay.Any(h => h.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    public static IEnumerable<LeadItem> Sort(IEnumerable<LeadItem> items, SortColumn column, bool descending)
    {
        Func<LeadItem, object> key = column switch {
            SortColumn.Title => l => l.Data.Title.ToLowerInvariant(),
            SortColumn.Kind => l => l.Data.Kind,
            SortColumn.Source => l => SourceCatalog.Title(l.Data.Source),
            SortColumn.Region => l => (l.Data.Region ?? "").ToLowerInvariant(),
            SortColumn.Found => l => l.DiscoveredAt,
            SortColumn.Relevance => l => l.Data.Relevance.Rank(),
            _ => l => l.Data.Score,
        };

        // Второй ключ — оценка и дата: порядок равных значений устойчив, строки не «прыгают» при обновлении.
        var ordered = descending ? items.OrderByDescending(key) : items.OrderBy(key);
        return ordered.ThenByDescending(l => l.Data.Score).ThenByDescending(l => l.DiscoveredAt).ThenBy(l => l.Id, StringComparer.Ordinal);
    }
}
