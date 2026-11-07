using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Customers.Ui;

/// <summary>Таблица лидов с поиском, фильтрами, сортировкой, командами и карточкой выбранного лида.</summary>
public sealed partial class LeadsView : UserControl, ISectionAware, IDetachable
{
    private readonly ShellView _shell;
    private readonly DevicePreferences _device = App.Instance.Env.Device;
    private readonly LeadInspector _inspector;
    private bool _syncing;
    private bool _filling;
    private double _dragStartWidth;
    private string? _pendingSeen;

    public LeadsView(AppModel model, ShellView shell)
    {
        Model = model;
        _shell = shell;
        InitializeComponent();
        List.ItemsSource = Model.Rows;
        _inspector = new LeadInspector(model, shell);
        InspectorHost.Child = _inspector;

        KindCombo.Items.Add("Все типы");
        KindCombo.Items.Add(LeadKind.Order.Title() + "ы");
        KindCombo.Items.Add(LeadKind.Organization.Title() + "и");

        InspectorSplitter.DragStarted += () => _dragStartWidth = _device.InspectorWidth;
        InspectorSplitter.Dragged += delta => SetInspectorWidth(_dragStartWidth - delta, save: false);
        InspectorSplitter.DragCompleted += () =>
        {
            if (_pendingWidth > 0)
            {
                _device.InspectorWidth = _pendingWidth;
            }
        };
        InspectorSplitter.ResetRequested += () => SetInspectorWidth(400, save: true);

        Model.PropertyChanged += OnModelChanged;
        Model.DataReloaded += OnDataReloaded;
        Model.SelectionChanged += OnSelectionChanged;
        _device.PropertyChanged += OnDeviceChanged;

        Layout.KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, () => SearchBox.Focus(FocusState.Keyboard)));
        Layout.KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.I, Windows.System.VirtualKeyModifiers.Control, () => _device.InspectorShown = !_device.InspectorShown));
        ApplyInspector();
        ApplyRowStyle();
        RefreshAll();
    }

    public AppModel Model { get; }

    private static KeyboardAccelerator Accelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return accelerator;
    }

    public void OnShown(NavSection section)
    {
        MarkPendingSeen();
        if (SearchBox.Text != Model.SearchText)
        {
            SearchBox.Text = Model.SearchText;
        }

        RefreshAll();
    }

    public void Detach()
    {
        MarkPendingSeen();
        Model.PropertyChanged -= OnModelChanged;
        Model.DataReloaded -= OnDataReloaded;
        Model.SelectionChanged -= OnSelectionChanged;
        _device.PropertyChanged -= OnDeviceChanged;
        _inspector.Detach();
    }

    // MARK: Состояние

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Searching):
            case nameof(AppModel.SearchProgress):
                RefreshSearch();
                break;
            case nameof(AppModel.LastReport):
                ShowReport();
                break;
            case nameof(AppModel.Sort):
            case nameof(AppModel.SortDescending):
                UpdateHeaders();
                break;
            case nameof(AppModel.SearchText):
                if (SearchBox.Text != Model.SearchText)
                {
                    SearchBox.Text = Model.SearchText;
                }

                break;
            case nameof(AppModel.VisibleCount):
            case nameof(AppModel.IsEmpty):
            case nameof(AppModel.EmptyText):
                RefreshEmpty();
                break;
        }
    }

    private void OnDataReloaded()
    {
        RestoreSelection();
        RefreshAll();
    }

    private void RefreshAll()
    {
        var section = Model.Section;
        SectionTitle.Text = section.Title();
        CountText.Text = Model.VisibleCount == Model.CountOf(section) || !section.IsLeadList()
            ? Fmt.Leads(Model.VisibleCount)
            : $"{Fmt.Leads(Model.VisibleCount)} из {Model.CountOf(section)}";
        UpdateHeaders();
        RefreshEmpty();
        RefreshSearch();
        RefreshCommands();
    }

    private void RefreshEmpty()
    {
        var empty = Model.IsEmpty;
        EmptyPanel.Visibility = Fmt.Vis(empty);
        TableScroll.Visibility = Fmt.NotVis(empty);
        if (!empty)
        {
            return;
        }

        var noData = Model.AllLeads.Count == 0;
        EmptyIcon.Glyph = noData ? "" : "";
        EmptyTitle.Text = noData ? "Лидов пока нет" : Model.FiltersActive ? "Ничего не найдено" : "Здесь пока пусто";
        EmptyText.Text = Model.EmptyText;
        if (Model.FiltersActive)
        {
            EmptyAction.Content = "Сбросить фильтры";
            EmptyAction.Visibility = Visibility.Visible;
        }
        else if (noData)
        {
            EmptyAction.Content = Model.Searching ? "Идёт поиск…" : "Найти лиды";
            EmptyAction.Visibility = Visibility.Visible;
            EmptyAction.IsEnabled = !Model.Searching;
        }
        else
        {
            EmptyAction.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshSearch()
    {
        var searching = Model.Searching;
        ProgressCard.Visibility = Fmt.Vis(searching);
        ProgressText.Text = Model.SearchProgress ?? "Поиск…";
        FindText.Text = searching ? "Остановить" : "Найти лиды";
        FindGlyph.Glyph = searching ? "" : "";
        if (searching)
        {
            ReportBar.IsOpen = false;
        }

        RefreshEmpty();
    }

    private void ShowReport()
    {
        if (Model.LastReport is not { } report || Model.Searching)
        {
            return;
        }

        var failed = report.Failed;
        ReportBar.Severity = failed.Count > 0 ? InfoBarSeverity.Warning : report.Added > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        ReportBar.Title = report.Summary;
        ReportBar.Message = failed.Count == 0
            ? string.Join("  ", report.Sources.Where(s => s.Skipped is null).Select(s => $"{SourceCatalog.Title(s.Source)}: найдено {s.Found}, новых {s.Added}"))
            : string.Join("\n", failed.Select(f => $"{SourceCatalog.Title(f.Source)}: {(f.Errors.FirstOrDefault() ?? "ошибка")}"));
        ReportBar.IsOpen = true;
    }

    private void RefreshCommands()
    {
        var any = Model.SelectionCount > 0;
        FavoriteButton.IsEnabled = any;
        ArchiveButton.IsEnabled = any;
        ExcludeButton.IsEnabled = any;
        ExportButton.IsEnabled = Model.AllLeads.Count > 0;
    }

    private void UpdateHeaders()
    {
        SetHeader(HeadScore, "Оценка", SortColumn.Score);
        SetHeader(HeadTitle, "Лид", SortColumn.Title);
        SetHeader(HeadSource, "Источник", SortColumn.Source);
        SetHeader(HeadFound, "Найден", SortColumn.Found);
    }

    private void SetHeader(Button button, string title, SortColumn column)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        if (Model.Sort == column)
        {
            panel.Children.Add(new FontIcon { Glyph = Model.SortDescending ? "" : "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
        }

        button.Content = panel;
    }

    // MARK: Выбор строк

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        // Если строки исчезли из-за перестроения таблицы (поиск, действие, синхронизация), выбор сохраняется в модели, а не сбрасывается.
        var rows = Model.Rows;
        var removed = e.RemovedItems.OfType<LeadRow>().Where(r => rows.Contains(r)).Select(r => r.Id);
        var added = e.AddedItems.OfType<LeadRow>().Select(r => r.Id);
        var next = Model.SelectedIds.Except(removed).Union(added).ToList();
        if (next.Count == Model.SelectedIds.Count && !next.Except(Model.SelectedIds).Any())
        {
            return;
        }

        Model.SetSelection(next);
    }

    private void RestoreSelection()
    {
        _syncing = true;
        try
        {
            List.SelectedItems.Clear();
            var ids = Model.SelectedIds.ToHashSet();
            if (ids.Count > 0)
            {
                foreach (var row in Model.Rows.Where(r => ids.Contains(r.Id)))
                {
                    List.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelectionChanged()
    {
        // Прочитанным лид становится, когда пользователь переходит к другому: в разделе «Новые» строка не исчезает из-под курсора.
        var current = Model.SelectedLead?.Id;
        if (_pendingSeen is not null && _pendingSeen != current)
        {
            MarkPendingSeen();
        }

        if (current is not null && Model.SelectedLead is { IsNew: true })
        {
            _pendingSeen = current;
        }

        if (!_syncing && Model.SelectionCount == 0 && List.SelectedItems.Count > 0)
        {
            RestoreSelection();
        }

        RefreshCommands();
    }

    private void MarkPendingSeen()
    {
        if (_pendingSeen is { } id)
        {
            _pendingSeen = null;
            Model.MarkSeen([id]);
        }
    }

    private IReadOnlyCollection<string> SelectedIds => Model.SelectedIds;

    private IReadOnlyList<LeadItem> TargetItems(bool allVisibleWhenNone = false) =>
        Model.SelectionCount > 0 ? Model.SelectedItems : allVisibleWhenNone ? [.. Model.Visible()] : [];

    // MARK: Поиск и фильтры

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput || sender.Text.Length == 0)
        {
            Model.SearchText = sender.Text;
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => Model.SearchText = sender.Text;

    private void OnFilterFlyoutOpening(object? sender, object e)
    {
        _filling = true;
        try
        {
            KindCombo.SelectedIndex = Model.KindFilter switch { LeadKind.Order => 1, LeadKind.Organization => 2, _ => 0 };
            Fill(SourceCombo, Model.AllSources.Select(s => (Text: SourceCatalog.Title(s), Value: s)), Model.SourceFilter, "Все источники");
            Fill(RegionCombo, Model.AllRegions.Select(r => (Text: r, Value: r)), Model.RegionFilter, "Все регионы");
            ScoreSlider.Value = Model.MinScore;
            HideRejectedSwitch.IsOn = Model.HideRejected;
        }
        finally
        {
            _filling = false;
        }
    }

    private static void Fill(ComboBox combo, IEnumerable<(string Text, string Value)> items, string? selected, string all)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = all, Tag = null });
        var index = 0;
        foreach (var (text, value) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
            if (value == selected)
            {
                index = combo.Items.Count - 1;
            }
        }

        combo.SelectedIndex = index;
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        Model.KindFilter = KindCombo.SelectedIndex switch { 1 => LeadKind.Order, 2 => LeadKind.Organization, _ => null };
        Model.SourceFilter = (SourceCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        Model.RegionFilter = (RegionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void OnScoreSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_filling)
        {
            Model.MinScore = e.NewValue;
        }
    }

    private void OnHideRejectedToggled(object sender, RoutedEventArgs e)
    {
        if (!_filling)
        {
            Model.HideRejected = HideRejectedSwitch.IsOn;
        }
    }

    private void OnResetFiltersClick(object sender, RoutedEventArgs e)
    {
        Model.ResetFilters();
        SearchBox.Text = "";
        OnFilterFlyoutOpening(null, EventArgs.Empty);
    }

    // MARK: Сортировка

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var column))
        {
            Model.SortBy(column);
        }
    }

    private void OnSortMenuOpening(object? sender, object e)
    {
        SortMenu.Items.Clear();
        foreach (var (column, title) in new[] {
            (SortColumn.Score, "По оценке"), (SortColumn.Relevance, "По релевантности"), (SortColumn.Title, "По названию"), (SortColumn.Kind, "По типу"),
            (SortColumn.Source, "По источнику"), (SortColumn.Region, "По региону"), (SortColumn.Found, "По дате находки") })
        {
            var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = Model.Sort == column };
            var c = column;
            item.Click += (_, _) => Model.SortBy(c);
            SortMenu.Items.Add(item);
        }

        SortMenu.Items.Add(new MenuFlyoutSeparator());
        var direction = new MenuFlyoutItem { Text = Model.SortDescending ? "Сначала меньшие (по возрастанию)" : "Сначала большие (по убыванию)" };
        direction.Click += (_, _) => Model.SortBy(Model.Sort);
        SortMenu.Items.Add(direction);
    }

    // MARK: Команды

    private void OnFindClick(object sender, RoutedEventArgs e)
    {
        if (Model.Searching)
        {
            Model.StopSearch();
        }
        else
        {
            _ = Model.RunSearchAsync();
        }
    }

    private void OnEmptyActionClick(object sender, RoutedEventArgs e)
    {
        if (Model.FiltersActive)
        {
            OnResetFiltersClick(sender, e);
        }
        else
        {
            OnFindClick(sender, e);
        }
    }

    private void OnRowStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            Model.ToggleFavorite([id]);
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e) => Model.ToggleFavorite([.. SelectedIds]);

    private void OnArchiveClick(object sender, RoutedEventArgs e) => LeadActions.ToggleArchive(Model, Model.SelectedItems);

    private void OnExportClick(object sender, RoutedEventArgs e) => _ = ExportFlow.RunAsync(Model, TargetItems(allVisibleWhenNone: true), Model.SelectionCount > 0 ? "выбранные лиды" : "лиды из текущего списка");

    private void OnCrmClick(object sender, RoutedEventArgs e) => _ = CrmFlow.RunAsync(Model, TargetItems(allVisibleWhenNone: true));

    private void OnMarkAllSeenClick(object sender, RoutedEventArgs e) => Model.MarkAllSeen();

    private async void OnImportClick(object sender, RoutedEventArgs e) => await ExportFlow.ImportAsync(Model);

    private async void OnDeleteClick(object sender, RoutedEventArgs e) => await LeadActions.DeleteAsync(Model, Model.SelectedItems);

    private void OnExcludeMenuOpening(object? sender, object e) => LeadActions.FillExcludeMenu(Model, ExcludeMenu, Model.SelectedItems);

    // MARK: Контекстное меню и клавиатура

    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LeadRow row && !Model.SelectedIds.Contains(row.Id))
        {
            Model.SetSelection([row.Id]);
            RestoreSelection();
        }
    }

    private void OnRowMenuOpening(object? sender, object e)
    {
        RowMenu.Items.Clear();
        var items = Model.SelectedItems;
        if (items.Count == 0)
        {
            return;
        }

        var first = items[0];
        var single = items.Count == 1;
        if (single && !string.IsNullOrWhiteSpace(first.Data.Url))
        {
            RowMenu.Items.Add(MenuItem("Открыть страницу", "", () => _ = Launcher.OpenWebAsync(first.Data.Url)));
            RowMenu.Items.Add(MenuItem("Копировать ссылку", "", () => Launcher.Copy(first.Data.Url)));
            RowMenu.Items.Add(new MenuFlyoutSeparator());
        }

        var allFav = items.All(l => l.Favorite);
        RowMenu.Items.Add(MenuItem(allFav ? "Убрать из избранного" : "В избранное", allFav ? "" : "", () => Model.ToggleFavorite([.. SelectedIds])));
        var allArchived = items.All(l => l.Archived);
        RowMenu.Items.Add(MenuItem(allArchived ? "Вернуть из архива" : "В архив", "", () => LeadActions.ToggleArchive(Model, items)));
        var exclude = new MenuFlyoutSubItem { Text = "Исключить", Icon = new FontIcon { Glyph = "" } };
        LeadActions.FillExcludeItems(Model, exclude.Items, items);
        RowMenu.Items.Add(exclude);
        RowMenu.Items.Add(new MenuFlyoutSeparator());
        RowMenu.Items.Add(MenuItem("Экспортировать…", "", () => _ = ExportFlow.RunAsync(Model, items, "выбранные лиды")));
        RowMenu.Items.Add(MenuItem("Передать в CRM…", "", () => _ = CrmFlow.RunAsync(Model, items)));
        RowMenu.Items.Add(new MenuFlyoutSeparator());
        RowMenu.Items.Add(MenuItem("Удалить", "", () => _ = LeadActions.DeleteAsync(Model, items)));
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && Model.SelectionCount > 0)
        {
            e.Handled = true;
            _ = LeadActions.DeleteAsync(Model, Model.SelectedItems);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && Model.SelectedLead is { } row && !string.IsNullOrWhiteSpace(row.Data.Url))
        {
            e.Handled = true;
            _ = Launcher.OpenWebAsync(row.Data.Url);
        }
    }

    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LeadRow row && !string.IsNullOrWhiteSpace(row.Data.Url))
        {
            _ = Launcher.OpenWebAsync(row.Data.Url);
        }
    }

    // MARK: Таблица: размеры и оформление

    private void OnTableScrollSizeChanged(object sender, SizeChangedEventArgs e) => Table.Width = Math.Max(Table.MinWidth, e.NewSize.Width);

    private void ApplyRowStyle()
    {
        List.ItemTemplate = (DataTemplate)Resources[_device.CompactRows ? "CompactRow" : "NormalRow"];
        // Пересоздать контейнеры, чтобы строки взяли новый шаблон сразу.
        var source = List.ItemsSource;
        List.ItemsSource = null;
        List.ItemsSource = source;
        RestoreSelection();
    }

    // MARK: Карточка

    private void ApplyInspector()
    {
        var shown = _device.InspectorShown;
        InspectorToggle.IsChecked = shown;
        InspectorHost.Visibility = Fmt.Vis(shown);
        InspectorSplitter.Visibility = Fmt.Vis(shown);
        SplitterColumn.Width = new GridLength(shown ? 14 : 0);
        InspectorColumn.Width = new GridLength(shown ? _device.InspectorWidth : 0);
    }

    private void SetInspectorWidth(double width, bool save)
    {
        var max = Math.Max(280, Math.Min(640, Layout.ActualWidth - 320 - 14));
        var w = Math.Clamp(width, 280, max);
        InspectorColumn.Width = new GridLength(w);
        if (save)
        {
            _device.InspectorWidth = w;
        }
        else
        {
            _pendingWidth = w;
        }
    }

    private double _pendingWidth;

    private void OnInspectorToggleClick(object sender, RoutedEventArgs e) => _device.InspectorShown = InspectorToggle.IsChecked == true;

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DevicePreferences.InspectorShown):
            case nameof(DevicePreferences.InspectorWidth):
                ApplyInspector();
                break;
            case nameof(DevicePreferences.CompactRows):
                ApplyRowStyle();
                break;
        }
    }
}
