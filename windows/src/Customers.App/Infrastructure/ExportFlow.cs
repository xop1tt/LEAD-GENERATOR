using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Customers.Ui;

/// <summary>Экспорт лидов в файл и импорт из файла: диалог выбора формата и полей, окно сохранения, результат.</summary>
internal static class ExportFlow
{
    /// <summary>Последний выбор полей: запоминается до закрытия приложения, чтобы не отмечать их каждый раз заново.</summary>
    public static HashSet<ExportField> LastFields { get; } = [.. ExportFields.Defaults];

    public static ExportFormat LastFormat { get; set; } = ExportFormat.Csv;

    public static async Task RunAsync(AppModel model, IReadOnlyCollection<LeadItem> items, string scope)
    {
        if (items.Count == 0)
        {
            App.Instance.Notifier.Warning("Нечего экспортировать", "В списке нет лидов.");
            return;
        }

        var format = new RadioButtons { Header = "Формат" };
        format.Items.Add("CSV (открывается в Excel)");
        format.Items.Add("JSON");
        format.SelectedIndex = LastFormat == ExportFormat.Csv ? 0 : 1;

        var boxes = new List<(ExportField Field, CheckBox Box)>();
        var fieldsPanel = new StackPanel { Spacing = 2 };
        foreach (var f in ExportFields.All)
        {
            var box = new CheckBox { Content = f.Title(), IsChecked = LastFields.Contains(f), MinWidth = 0 };
            boxes.Add((f, box));
            fieldsPanel.Children.Add(box);
        }

        var all = new Button { Content = "Все поля" };
        all.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = true);
        var defaults = new Button { Content = "По умолчанию" };
        defaults.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = ExportFields.Defaults.Contains(b.Field));
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { all, defaults } };

        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = $"Будет сохранено: {Fmt.Plural(items.Count, "лид", "лида", "лидов")} ({scope}).", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(format);
        content.Children.Add(new TextBlock { Text = "Поля", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        content.Children.Add(presets);
        content.Children.Add(new ScrollViewer { MaxHeight = 220, Content = fieldsPanel });

        var dialog = new ContentDialog {
            Title = "Экспорт лидов",
            Content = content,
            PrimaryButtonText = "Сохранить…",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Field).ToList();
        if (chosen.Count == 0)
        {
            App.Instance.Notifier.Warning("Не выбрано ни одного поля");
            return;
        }

        LastFields.Clear();
        LastFields.UnionWith(chosen);
        LastFormat = format.SelectedIndex == 0 ? ExportFormat.Csv : ExportFormat.Json;
        var path = await Pickers.SaveFileAsync($"leads-{DateTime.Now:yyyy-MM-dd}", LastFormat.Title(), "." + LastFormat.Extension());
        if (path is not null)
        {
            model.Export([.. items], LastFormat, chosen, path);
        }
    }

    public static async Task ImportAsync(AppModel model)
    {
        var path = await Pickers.OpenFileAsync(".csv", ".json");
        if (path is null)
        {
            return;
        }

        var result = model.ImportLeads(path);
        if (result is { Problems.Count: > 0 })
        {
            await Dialogs.InfoAsync("Импорт завершён с замечаниями",
                $"Добавлено: {result.Added}, дубликатов: {result.Duplicates}, пропущено: {result.Skipped}.\n\n" + string.Join("\n", result.Problems.Take(30)));
        }
    }
}
