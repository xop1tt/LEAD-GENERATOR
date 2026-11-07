using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Customers.Ui;

/// <summary>Передача лидов в Bitrix24, Notion или вебхук: выбор назначения, ход отправки, итог.</summary>
internal static class CrmFlow
{
    public static IReadOnlyList<CrmKind> Configured(AppModel model)
    {
        var list = new List<CrmKind>();
        foreach (var kind in new[] { CrmKind.Bitrix, CrmKind.Notion, CrmKind.Webhook })
        {
            var hasSecret = !string.IsNullOrWhiteSpace(model.CrmSecret(kind));
            if (hasSecret && (kind != CrmKind.Notion || model.LoadNotionSetup() is { Database.Length: > 0 }))
            {
                list.Add(kind);
            }
        }

        return list;
    }

    public static async Task<ICrmDestination> BuildAsync(AppModel model, CrmKind kind, CancellationToken ct = default)
    {
        var secret = model.CrmSecret(kind) ?? "";
        switch (kind)
        {
            case CrmKind.Bitrix:
                return new Bitrix24Destination(secret);
            case CrmKind.Webhook:
                return new WebhookDestination(secret);
            default:
                var setup = model.LoadNotionSetup() ?? throw new CrmException(CrmErrorKind.InvalidAddress, "Notion не настроен: укажите базу в Настройки → Интеграции");
                var http = model.Environment.CrmHttp();
                try
                {
                    var database = await NotionDestination.FetchDatabaseAsync(secret, setup.Database, http, ct);
                    return new NotionDestination(secret, setup.Database, database.Properties, setup.Mapping);
                }
                finally
                {
                    (http as IDisposable)?.Dispose();
                }
        }
    }

    public static async Task RunAsync(AppModel model, IReadOnlyCollection<LeadItem> items)
    {
        if (items.Count == 0)
        {
            App.Instance.Notifier.Warning("Нечего передавать", "В списке нет лидов.");
            return;
        }

        var configured = Configured(model);
        if (configured.Count == 0)
        {
            if (await Dialogs.ConfirmAsync("Интеграции не настроены", "Чтобы передавать лиды в CRM, укажите вебхук Bitrix24, Notion или свой вебхук.", "Открыть настройки", "Закрыть"))
            {
                App.Instance.MainWindow.Shell?.OpenSettings(SettingsSection.Integrations);
            }

            return;
        }

        var target = new ComboBox { Header = "Куда передать", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in configured)
        {
            target.Items.Add(new ComboBoxItem { Content = k.Title(), Tag = k });
        }

        target.SelectedIndex = 0;
        var resend = new CheckBox { Content = "Отправить повторно и те, что уже были отправлены" };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = $"Будет передано: {Fmt.Plural(items.Count, "лид", "лида", "лидов")}. Лиды, отправленные раньше, пропускаются, повторов не будет.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(target);
        content.Children.Add(resend);
        var dialog = new ContentDialog { Title = "Передать в CRM", Content = content, PrimaryButtonText = "Отправить", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || (target.SelectedItem as ComboBoxItem)?.Tag is not CrmKind kind)
        {
            return;
        }

        await SendAsync(model, kind, [.. items], resend.IsChecked == true);
    }

    private static async Task SendAsync(AppModel model, CrmKind kind, IReadOnlyCollection<LeadItem> items, bool resend)
    {
        using var cts = new CancellationTokenSource();
        var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, items.Count), Value = 0 };
        var text = new TextBlock { Text = "Подготовка…" };
        var progress = new ContentDialog {
            Title = "Передача в " + kind.Title(),
            Content = new StackPanel { Spacing = 10, MinWidth = 360, Children = { text, bar } },
            CloseButtonText = "Остановить",
        };
        progress.CloseButtonClick += (_, _) => cts.Cancel();
        var shown = Dialogs.ShowAsync(progress);
        CrmReport? report = null;
        string? failure = null;
        try
        {
            var destination = await BuildAsync(model, kind, cts.Token);
            report = await model.ExportToCrmAsync(destination, items, resend, (done, total) =>
            {
                bar.Maximum = Math.Max(1, total);
                bar.Value = done;
                text.Text = $"Отправлено {done} из {total}";
            }, cts.Token);
        }
        catch (CrmException e)
        {
            failure = e.Message;
        }
        catch (OperationCanceledException)
        {
            failure = "Передача остановлена. Уже отправленные лиды не будут отправлены повторно.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            failure = "Не удалось связаться с сервисом: " + e.Message;
        }

        progress.Hide();
        await shown;
        if (failure is not null)
        {
            App.Instance.Env.Log.Warn("Передача в CRM: " + failure);
            await Dialogs.InfoAsync("Передача не выполнена", failure);
        }
        else if (report is not null)
        {
            var details = report.Failures.Count == 0 ? "" : "\n\n" + string.Join("\n", report.Failures.Take(20).Select(f => $"• {f.Title}: {f.Message}"));
            await Dialogs.InfoAsync(report.Failures.Count == 0 && !report.StoppedEarly ? "Передача завершена" : "Передача завершена с замечаниями", report.Summary + details);
        }
    }
}
