using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Customers.Ui;

/// <summary>Основная часть окна: левая панель навигации и выбранный раздел.</summary>
public sealed partial class ShellView : UserControl
{
    private readonly SessionModel _session;
    private readonly DevicePreferences _device = App.Instance.Env.Device;
    private readonly Dictionary<NavSection, NavigationViewItem> _items = [];
    private readonly Dictionary<NavSection, UserControl> _views = [];
    private double _dragStartWidth;
    private bool _detached;

    public ShellView(AppModel model, SessionModel session)
    {
        Model = model;
        _session = session;
        InitializeComponent();
        BuildMenu();
        Nav.OpenPaneLength = _device.NavWidth;
        PaneSplitter.DragStarted += () => _dragStartWidth = Nav.OpenPaneLength;
        PaneSplitter.Dragged += delta => SetPaneWidth(_dragStartWidth + delta, save: false);
        PaneSplitter.DragCompleted += () => _device.NavWidth = Nav.OpenPaneLength;
        PaneSplitter.ResetRequested += () => SetPaneWidth(260, save: true);

        _device.PropertyChanged += OnDeviceChanged;
        Model.PropertyChanged += OnModelChanged;
        Model.DataReloaded += UpdateBadges;
        Model.SearchCompleted += OnSearchCompleted;
        App.Instance.Notifier.Posted += OnNotice;
        Layout.KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control, StartSearch));
        Layout.KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None, StartSearch));
        Loaded += (_, _) => UpdatePaneVisibility();
        Layout.SizeChanged += (_, _) => UpdatePaneVisibility();

        UpdateBadges();
        ApplyAnimations();
        var start = Model.Section == NavSection.Settings ? NavSection.All : Model.Section;
        Nav.SelectedItem = _items[start];
        Show(start);
    }

    public AppModel Model { get; }

    public SessionModel Session => _session;

    private static Microsoft.UI.Xaml.Input.KeyboardAccelerator Accelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return accelerator;
    }

    /// <summary>Отключает подписки при замене учётной записи.</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _device.PropertyChanged -= OnDeviceChanged;
        Model.PropertyChanged -= OnModelChanged;
        Model.DataReloaded -= UpdateBadges;
        Model.SearchCompleted -= OnSearchCompleted;
        App.Instance.Notifier.Posted -= OnNotice;
        foreach (var view in _views.Values.OfType<IDetachable>())
        {
            view.Detach();
        }
    }

    // MARK: Меню

    private void BuildMenu()
    {
        void Add(NavSection s, IList<object> target)
        {
            var item = new NavigationViewItem { Content = s.Title(), Tag = s, Icon = new FontIcon { Glyph = s.Glyph() } };
            ToolTipService.SetToolTip(item, s.Title());
            _items[s] = item;
            target.Add(item);
        }

        Add(NavSection.Home, Nav.MenuItems);
        Nav.MenuItems.Add(new NavigationViewItemSeparator());
        Nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Лиды" });
        foreach (var s in NavSections.Lists)
        {
            Add(s, Nav.MenuItems);
        }

        Nav.MenuItems.Add(new NavigationViewItemSeparator());
        Add(NavSection.Sources, Nav.MenuItems);
        Add(NavSection.Runs, Nav.MenuItems);
        Add(NavSection.Settings, Nav.FooterMenuItems);
    }

    private void UpdateBadges()
    {
        foreach (var s in NavSections.Lists)
        {
            var count = Model.CountOf(s);
            _items[s].InfoBadge = count > 0
                ? new InfoBadge {
                    Value = count,
                    Style = s == NavSection.New ? null : (Style)Application.Current.Resources["InformationalValueInfoBadgeStyle"],
                }
                : null;
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: NavSection section })
        {
            Show(section);
        }
    }

    public void Navigate(NavSection section)
    {
        if (_items.TryGetValue(section, out var item))
        {
            Nav.SelectedItem = item;
        }
    }

    public void OpenSettings(SettingsSection section)
    {
        Navigate(NavSection.Settings);
        if (_views.TryGetValue(NavSection.Settings, out var view) && view is SettingsView settings)
        {
            settings.Select(section);
        }
    }

    private void Show(NavSection section)
    {
        Model.Section = section;
        // Один и тот же список показывает все пять разделов лидов; модель уже отфильтровала строки.
        var key = section.IsLeadList() ? NavSection.All : section;
        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch {
                NavSection.Home => new HomeView(Model, this),
                NavSection.Sources => new SourcesView(Model, this),
                NavSection.Runs => new RunsView(Model, this),
                NavSection.Settings => new SettingsView(Model, _session, this),
                _ => new LeadsView(Model, this),
            };
            _views[key] = view;
        }

        Host.Content = view;
        (view as ISectionAware)?.OnShown(section);
    }

    private void ApplyAnimations()
    {
        if (_device.Animations)
        {
            var transitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            transitions.Add(new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromVerticalOffset = 16 });
            Host.ContentTransitions = transitions;
        }
        else
        {
            Host.ContentTransitions = null;
        }
    }

    // MARK: Ширина и видимость панели

    private void SetPaneWidth(double width, bool save)
    {
        var w = Math.Clamp(width, 180, 420);
        Nav.OpenPaneLength = w;
        PositionSplitter();
        if (save)
        {
            _device.NavWidth = w;
        }
    }

    private void UpdatePaneVisibility()
    {
        Nav.IsPaneVisible = !_device.NavHidden;
        var expanded = Nav.DisplayMode == NavigationViewDisplayMode.Expanded;
        PaneSplitter.Visibility = !_device.NavHidden && expanded && Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        PositionSplitter();
    }

    private void PositionSplitter() => PaneSplitter.Margin = new Thickness(Math.Max(0, Nav.OpenPaneLength - 4), 0, 0, 0);

    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) => UpdatePaneVisibility();

    private void OnNavPaneChanged(NavigationView sender, object args) => UpdatePaneVisibility();

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DevicePreferences.NavHidden):
                UpdatePaneVisibility();
                break;
            case nameof(DevicePreferences.NavWidth):
                Nav.OpenPaneLength = _device.NavWidth;
                PositionSplitter();
                break;
            case nameof(DevicePreferences.Animations):
                ApplyAnimations();
                break;
        }
    }

    // MARK: Поиск с клавиатуры

    private void StartSearch()
    {
        if (!Model.Searching)
        {
            _ = Model.RunSearchAsync();
        }
    }

    // MARK: Сообщения модели и уведомления

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.LastError) && Model.LastError is { Length: > 0 } error)
        {
            App.Instance.Notifier.Error("Ошибка", error);
            Model.LastError = null;
        }
        else if (e.PropertyName == nameof(AppModel.LastMessage) && Model.LastMessage is { Length: > 0 } message)
        {
            App.Instance.Notifier.Success(message);
            Model.LastMessage = null;
        }
    }

    private void OnSearchCompleted(SearchReport report, bool quiet)
    {
        var prefs = Model.Prefs;
        if (report.Added > 0 && prefs.NotifyNewLeads)
        {
            App.Instance.Notifier.Toast("Найдены новые лиды", $"Новых: {report.Added}");
        }
        else if (report.Failed.Count > 0 && prefs.NotifyErrors)
        {
            App.Instance.Notifier.Toast("Поиск завершился с ошибками", report.Summary);
        }
    }

    private void OnNotice(Notice notice) => App.Instance.Ui.Post(() => AddToast(notice));

    private void AddToast(Notice notice)
    {
        var bar = new InfoBar {
            Title = notice.Title,
            Message = notice.Message,
            Severity = notice.Kind switch {
                NoticeKind.Success => InfoBarSeverity.Success,
                NoticeKind.Warning => InfoBarSeverity.Warning,
                NoticeKind.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            },
            IsOpen = true,
            IsClosable = true,
        };
        if (notice.ActionText is { } text && notice.Action is { } action)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) =>
            {
                action();
                bar.IsOpen = false;
            };
            bar.ActionButton = button;
        }

        bar.CloseButtonClick += (_, _) => Toasts.Children.Remove(bar);
        bar.Closed += (_, _) => Toasts.Children.Remove(bar);
        Toasts.Children.Add(bar);
        while (Toasts.Children.Count > 4)
        {
            Toasts.Children.RemoveAt(0);
        }

        if (notice.Kind is NoticeKind.Success or NoticeKind.Info && notice.Action is null)
        {
            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(6);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                bar.IsOpen = false;
                Toasts.Children.Remove(bar);
            };
            timer.Start();
        }
    }
}

/// <summary>Представление, которому нужно знать, какой именно раздел сейчас показан.</summary>
public interface ISectionAware
{
    void OnShown(NavSection section);
}

/// <summary>Представление с подписками, которые нужно снять при закрытии учётной записи.</summary>
public interface IDetachable
{
    void Detach();
}
