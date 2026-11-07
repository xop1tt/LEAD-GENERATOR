using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Customers.Ui;

/// <summary>Карточка выбранного лида: оценка и причины, контакты, заметка, черновик обращения, история.</summary>
public sealed partial class LeadInspector : UserControl
{
    private readonly ShellView _shell;
    private readonly DispatcherQueueTimer _noteTimer;
    private LeadItem? _shown;
    private string? _shownId;
    private bool _filling;
    private string _draft = "";

    public LeadInspector(AppModel model, ShellView shell)
    {
        Model = model;
        _shell = shell;
        InitializeComponent();
        _noteTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _noteTimer.Interval = TimeSpan.FromMilliseconds(900);
        _noteTimer.IsRepeating = false;
        _noteTimer.Tick += (_, _) => SaveNote();
        foreach (var channel in Enum.GetValues<DraftWriter.Channel>())
        {
            ChannelCombo.Items.Add(new ComboBoxItem { Content = channel.Title(), Tag = channel });
        }

        ChannelCombo.SelectedIndex = 0;
        Model.SelectionChanged += OnSelectionChanged;
        Model.DataReloaded += OnDataReloaded;
        Render(force: true);
    }

    public AppModel Model { get; }

    public void Detach()
    {
        SaveNote();
        Model.SelectionChanged -= OnSelectionChanged;
        Model.DataReloaded -= OnDataReloaded;
    }

    private LeadItem? Current => Model.SelectionCount == 1 ? Model.SelectedItems.FirstOrDefault() : null;

    private void OnSelectionChanged()
    {
        SaveNote();
        Render(force: false);
    }

    private void OnDataReloaded()
    {
        // Карточка перерисовывается только если изменился показанный лид: иначе пропал бы фокус в заметке.
        if (Model.SelectionCount == 1 && Current is { } now && ReferenceEquals(now, _shown))
        {
            return;
        }

        Render(force: false);
    }

    // MARK: Отрисовка

    private void Render(bool force)
    {
        var count = Model.SelectionCount;
        NoneState.Visibility = Fmt.Vis(count == 0);
        MultiState.Visibility = Fmt.Vis(count > 1);
        SingleState.Visibility = Fmt.Vis(count == 1);
        if (count > 1)
        {
            MultiTitle.Text = $"Выбрано: {count}";
            _shown = null;
            _shownId = null;
            return;
        }

        if (Current is not { } lead)
        {
            _shown = null;
            _shownId = null;
            return;
        }

        var sameLead = _shownId == lead.Id;
        _shown = lead;
        _shownId = lead.Id;
        var d = lead.Data;
        TitleText.Text = string.IsNullOrWhiteSpace(d.Title) ? "(без названия)" : d.Title;
        BuildChips(lead);
        var hasUrl = Uri.TryCreate(d.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
        OpenButton.IsEnabled = hasUrl;
        CopyLinkButton.IsEnabled = !string.IsNullOrWhiteSpace(d.Url);
        FavoriteGlyph.Glyph = lead.Favorite ? "" : "";
        ToolTipService.SetToolTip(FavoriteButton, lead.Favorite ? "Убрать из избранного" : "В избранное");
        ToolTipService.SetToolTip(ArchiveButton, lead.Archived ? "Вернуть из архива" : "В архив");
        BuildData(lead);
        BuildReasons(lead);
        DescriptionSection.Visibility = Fmt.VisText(d.Description);
        DescriptionText.Text = d.Description;
        BuildFacts(lead);

        _filling = true;
        if (!sameLead || NoteBox.FocusState == FocusState.Unfocused)
        {
            NoteBox.Text = lead.Note;
        }

        _filling = false;
        if (!sameLead || force)
        {
            RebuildDraft();
        }

        BuildHistory(lead);
        if (!sameLead)
        {
            SingleState.ChangeView(0, 0, null, disableAnimation: true);
        }
    }

    private void BuildChips(LeadItem lead)
    {
        Chips.Children.Clear();
        var d = lead.Data;
        Chips.Children.Add(Chip($"Оценка {Math.Round(d.Score)}", Fmt.LevelBrush(d.Relevance.Raw()), Fmt.White));
        Chips.Children.Add(Chip(d.Relevance.Title(), null, null));
        Chips.Children.Add(Chip(d.Kind.Title(), null, null));
        Chips.Children.Add(Chip(SourceCatalog.Title(d.Source), null, null));
        if (lead.Archived)
        {
            Chips.Children.Add(Chip("В архиве", null, null));
        }

        if (lead.Excluded)
        {
            Chips.Children.Add(Chip("Исключён", null, null));
        }
    }

    private static Border Chip(string text, Brush? background, Brush? foreground)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Padding = new Thickness(8, 2, 8, 3) };
        if (foreground is not null)
        {
            label.Foreground = foreground;
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }

        return new Border {
            Child = label,
            CornerRadius = new CornerRadius(10),
            Background = background ?? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
        };
    }

    private void BuildData(LeadItem lead)
    {
        DataPanel.Children.Clear();
        var d = lead.Data;
        var draft = ContactLinks.EmailFor(d, Model.Signature);
        if (!string.IsNullOrWhiteSpace(d.CustomerName))
        {
            DataPanel.Children.Add(Row("Заказчик", d.CustomerName));
        }

        foreach (var phone in d.Phones)
        {
            var actions = new List<(string, string, Action)> { ("", "Копировать", () => Launcher.Copy(phone)) };
            if (ContactLinks.TelUri(phone) is { } tel)
            {
                actions.Add(("", "Позвонить", () => Call(lead, phone, tel)));
            }

            DataPanel.Children.Add(Row("Телефон", phone, [.. actions]));
        }

        foreach (var email in d.Emails)
        {
            var actions = new List<(string, string, Action)> { ("", "Копировать", () => Launcher.Copy(email)) };
            if (ContactLinks.MailtoUri(email, draft.Subject, draft.Body) is { } mail)
            {
                actions.Add(("", "Написать письмо с черновиком", () => Write(lead, email, mail)));
            }

            DataPanel.Children.Add(Row("Email", email, [.. actions]));
        }

        foreach (var (key, value) in d.Contacts)
        {
            if (value.Contains('@') && value.Contains('.') && !value.StartsWith('@') || d.Phones.Contains(value))
            {
                continue; // уже показано выше
            }

            var actions = new List<(string, string, Action)> { ("", "Копировать", () => Launcher.Copy(value)) };
            if (TelegramLink(key, value) is { } link)
            {
                actions.Add(("", "Открыть", () => _ = Launcher.OpenWebAsync(link)));
            }
            else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                actions.Add(("", "Открыть", () => _ = Launcher.OpenWebAsync(value)));
            }

            DataPanel.Children.Add(Row(key, value, [.. actions]));
        }

        if (!string.IsNullOrWhiteSpace(d.Website))
        {
            var site = d.Website!;
            DataPanel.Children.Add(Row("Сайт", site, ("", "Копировать", () => Launcher.Copy(site)), ("", "Открыть", () => _ = Launcher.OpenWebAsync(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site))));
        }

        AddIf("Адрес", d.Address);
        AddIf("Регион", d.Region);
        AddIf("Категория", d.Category ?? d.ProjectType);
        if (d.Budget is { } budget)
        {
            AddIf("Бюджет", $"{(long)budget} {d.Currency}".Trim());
        }

        AddIf("Опубликовано", d.PublishedAt is null ? null : Fmt.Date(d.PublishedAt));
        AddIf("Найдено", Fmt.Date(lead.DiscoveredAt));
        if (DataPanel.Children.Count == 0)
        {
            DataPanel.Children.Add(new TextBlock { Text = "Контактов нет.", Style = (Style)Application.Current.Resources["MutedTextStyle"] });
        }

        void AddIf(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                DataPanel.Children.Add(Row(label, value));
            }
        }
    }

    private static string? TelegramLink(string key, string value)
    {
        var v = value.Trim();
        if (key.Contains("telegram", StringComparison.OrdinalIgnoreCase) || key.Equals("tg", StringComparison.OrdinalIgnoreCase) || v.Contains("t.me/", StringComparison.OrdinalIgnoreCase))
        {
            var name = v.StartsWith('@') ? v[1..] : v;
            if (name.Contains("t.me/", StringComparison.OrdinalIgnoreCase))
            {
                return name.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? name : "https://" + name;
            }

            return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? "https://t.me/" + name : null;
        }

        return null;
    }

    private static Grid Row(string label, string value, params (string Glyph, string Tip, Action Do)[] actions)
    {
        var grid = new Grid { ColumnSpacing = 8, MinHeight = 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["MutedTextStyle"], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var v = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        if (actions.Length > 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (glyph, tip, action) in actions)
            {
                var button = new Button {
                    Content = new FontIcon { Glyph = glyph, FontSize = 12 },
                    Padding = new Thickness(6),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                };
                ToolTipService.SetToolTip(button, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
                var a = action;
                button.Click += (_, _) => a();
                panel.Children.Add(button);
            }

            Grid.SetColumn(panel, 2);
            grid.Children.Add(panel);
        }

        return grid;
    }

    private void Call(LeadItem lead, string phone, Uri tel)
    {
        _ = Launcher.OpenAsync(tel);
        Model.LogEvent(lead.Id, $"Набран номер {phone}");
    }

    private void Write(LeadItem lead, string email, Uri mail)
    {
        _ = Launcher.OpenAsync(mail);
        Model.LogEvent(lead.Id, $"Открыто письмо для {email}");
    }

    private void BuildReasons(LeadItem lead)
    {
        var d = lead.Data;
        ScoreTitle.Text = $"Почему оценка {Math.Round(d.Score)} из 100";
        ReasonsPanel.Children.Clear();
        if (d.ScoreReasons.Count == 0)
        {
            ReasonsPanel.Children.Add(Muted("Причины не записаны."));
        }

        foreach (var reason in d.ScoreReasons)
        {
            ReasonsPanel.Children.Add(new TextBlock { Text = "•  " + reason, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
    }

    private void BuildFacts(LeadItem lead)
    {
        FactsPanel.Children.Clear();
        var d = lead.Data;
        if (d.Facts.Count > 0)
        {
            FactsPanel.Children.Add(new TextBlock { Text = "Факты (из источника)", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            foreach (var f in d.Facts)
            {
                FactsPanel.Children.Add(new TextBlock { Text = "•  " + f, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            }
        }

        if (d.Assumptions.Count > 0)
        {
            FactsPanel.Children.Add(new TextBlock { Text = "Предположения (оценка системы, не факт)", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
            foreach (var a in d.Assumptions)
            {
                FactsPanel.Children.Add(new TextBlock { Text = "•  " + a, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            }
        }

        FactsExpander.Visibility = Fmt.Vis(FactsPanel.Children.Count > 0);
    }

    private static TextBlock Muted(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["MutedTextStyle"] };

    private void BuildHistory(LeadItem lead)
    {
        HistoryPanel.Children.Clear();
        var events = Model.Events(lead.Id);
        if (events.Count == 0)
        {
            HistoryPanel.Children.Add(Muted("Пока ничего не происходило."));
            return;
        }

        foreach (var e in events.Take(60))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Muted(Fmt.Date(e.At)));
            var text = new TextBlock { Text = e.Action, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            HistoryPanel.Children.Add(row);
        }
    }

    // MARK: Заметка

    private void OnNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (!_filling)
        {
            _noteTimer.Stop();
            _noteTimer.Start();
        }
    }

    private void OnNoteLostFocus(object sender, RoutedEventArgs e) => SaveNote();

    private void SaveNote()
    {
        _noteTimer.Stop();
        if (_shownId is { } id && !_filling && Model.Lead(id) is { } lead && lead.Note != NoteBox.Text)
        {
            Model.SetNote(id, NoteBox.Text);
        }
    }

    // MARK: Черновик

    private DraftWriter.Channel Channel => (ChannelCombo.SelectedItem as ComboBoxItem)?.Tag is DraftWriter.Channel c ? c : DraftWriter.Channel.Message;

    private void OnChannelChanged(object sender, SelectionChangedEventArgs e) => RebuildDraft();

    private void RebuildDraft()
    {
        if (_shown is not { } lead)
        {
            return;
        }

        _draft = DraftWriter.Draft(lead.Data, Channel, Model.Signature);
        DraftBox.Text = _draft;
        switch (Channel)
        {
            case DraftWriter.Channel.Email:
                DraftActionButton.Content = "Открыть письмо";
                DraftActionButton.IsEnabled = lead.Data.Emails.Count > 0;
                break;
            case DraftWriter.Channel.Call:
                DraftActionButton.Content = "Позвонить";
                DraftActionButton.IsEnabled = lead.Data.Phones.Any(p => ContactLinks.TelUri(p) is not null);
                break;
            default:
                DraftActionButton.Content = "Открыть чат";
                DraftActionButton.IsEnabled = lead.Data.Contacts.Any(kv => TelegramLink(kv.Key, kv.Value) is not null);
                break;
        }
    }

    private void OnCopyDraftClick(object sender, RoutedEventArgs e)
    {
        Launcher.Copy(DraftBox.Text);
        App.Instance.Notifier.Success("Черновик скопирован");
    }

    private void OnDraftActionClick(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } lead)
        {
            return;
        }

        switch (Channel)
        {
            case DraftWriter.Channel.Email:
                var parts = ContactLinks.Split(DraftBox.Text);
                if (lead.Data.Emails.FirstOrDefault() is { } email && ContactLinks.MailtoUri(email, parts.Subject, parts.Body) is { } mail)
                {
                    Write(lead, email, mail);
                }

                break;
            case DraftWriter.Channel.Call:
                if (lead.Data.Phones.FirstOrDefault(p => ContactLinks.TelUri(p) is not null) is { } phone)
                {
                    Call(lead, phone, ContactLinks.TelUri(phone)!);
                }

                break;
            default:
                if (lead.Data.Contacts.Select(kv => TelegramLink(kv.Key, kv.Value)).FirstOrDefault(x => x is not null) is { } chat)
                {
                    Launcher.Copy(DraftBox.Text);
                    _ = Launcher.OpenWebAsync(chat);
                    App.Instance.Notifier.Info("Текст скопирован", "Вставьте его в открывшийся чат.");
                }

                break;
        }
    }

    // MARK: Кнопки

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_shown is { } lead)
        {
            _ = Launcher.OpenWebAsync(lead.Data.Url);
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (_shown is { } lead)
        {
            Launcher.Copy(lead.Data.Url);
            App.Instance.Notifier.Success("Ссылка скопирована");
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_shown is { } lead)
        {
            Model.ToggleFavorite([lead.Id]);
        }
    }

    private void OnArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_shown is { } lead)
        {
            LeadActions.ToggleArchive(Model, [lead]);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_shown is { } lead)
        {
            await LeadActions.DeleteAsync(Model, [lead]);
        }
    }

    private void OnExcludeMenuOpening(object? sender, object e)
    {
        ExcludeMenu.Items.Clear();
        if (_shown is { } lead)
        {
            LeadActions.FillExcludeItems(Model, ExcludeMenu.Items, [lead]);
        }
    }

    private void OnMultiFavoriteClick(object sender, RoutedEventArgs e) => Model.ToggleFavorite([.. Model.SelectedIds]);

    private void OnMultiArchiveClick(object sender, RoutedEventArgs e) => LeadActions.ToggleArchive(Model, Model.SelectedItems);

    private void OnMultiExportClick(object sender, RoutedEventArgs e) => _ = ExportFlow.RunAsync(Model, Model.SelectedItems, "выбранные лиды");

    private void OnMultiCrmClick(object sender, RoutedEventArgs e) => _ = CrmFlow.RunAsync(Model, Model.SelectedItems);

    private async void OnMultiDeleteClick(object sender, RoutedEventArgs e) => await LeadActions.DeleteAsync(Model, Model.SelectedItems);
}
