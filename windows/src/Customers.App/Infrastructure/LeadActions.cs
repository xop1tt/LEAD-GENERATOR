using Microsoft.UI.Xaml.Controls;

namespace Customers.Ui;

/// <summary>Действия над лидами, общие для таблицы, контекстного меню и карточки.</summary>
internal static class LeadActions
{
    public static void ToggleArchive(AppModel model, IReadOnlyList<LeadItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        model.SetArchived([.. items.Select(l => l.Id)], !items.All(l => l.Archived));
    }

    public static async Task DeleteAsync(AppModel model, IReadOnlyList<LeadItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var what = items.Count == 1 ? $"лид «{items[0].Title}»" : Fmt.Plural(items.Count, "лид", "лида", "лидов");
        if (await Dialogs.ConfirmAsync("Удалить?", $"Будет удалён {what}. Если источник снова найдёт его, он появится как новый.", "Удалить"))
        {
            model.Delete([.. items.Select(l => l.Id)]);
        }
    }

    /// <summary>Значения, которые можно занести в исключения: домен, телефон, почта, название.</summary>
    public static IEnumerable<(ExclusionKind Kind, string Value)> ExcludeCandidates(IReadOnlyList<LeadItem> leads)
    {
        var seen = new HashSet<(ExclusionKind, string)>();
        foreach (var l in leads.Take(50))
        {
            var d = l.Data;
            var all = new List<(ExclusionKind, string)>();
            if (d.Domain is { Length: > 0 } domain)
            {
                all.Add((ExclusionKind.Domain, domain));
            }

            all.AddRange(d.Phones.Select(p => (ExclusionKind.Phone, p)));
            all.AddRange(d.Emails.Select(e => (ExclusionKind.Email, e)));
            var name = d.CustomerName ?? d.Title;
            if (!string.IsNullOrWhiteSpace(name))
            {
                all.Add((ExclusionKind.Name, name));
            }

            foreach (var c in all)
            {
                if (seen.Add(c))
                {
                    yield return c;
                }
            }
        }
    }

    public static void FillExcludeMenu(AppModel model, MenuFlyout menu, IReadOnlyList<LeadItem> leads)
    {
        menu.Items.Clear();
        FillExcludeItems(model, menu.Items, leads);
    }

    public static void FillExcludeItems(AppModel model, IList<MenuFlyoutItemBase> items, IReadOnlyList<LeadItem> leads)
    {
        var candidates = ExcludeCandidates(leads).Take(12).ToList();
        if (candidates.Count == 0)
        {
            items.Add(new MenuFlyoutItem { Text = "Нечего исключать", IsEnabled = false });
            return;
        }

        foreach (var (kind, value) in candidates)
        {
            var text = $"{kind.Title()}: {(value.Length > 48 ? value[..48] + "…" : value)}";
            var item = new MenuFlyoutItem { Text = text };
            var k = kind;
            var v = value;
            item.Click += (_, _) => model.Exclude(k, v);
            items.Add(item);
        }
    }
}
