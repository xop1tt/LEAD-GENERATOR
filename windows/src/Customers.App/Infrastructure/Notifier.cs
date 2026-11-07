using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Customers.Ui;

internal enum NoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

internal sealed record Notice(NoticeKind Kind, string Title, string Message, string? ActionText = null, Action? Action = null);

/// <summary>Уведомления: плашки InfoBar внутри окна и (по настройке) системные уведомления Windows, когда окно не в фокусе.</summary>
internal sealed class Notifier
{
    private bool _systemReady;

    public event Action<Notice>? Posted;

    public bool WindowActive { get; set; } = true;

    public void Post(Notice notice) => Posted?.Invoke(notice);

    public void Info(string title, string message = "") => Post(new Notice(NoticeKind.Info, title, message));

    public void Success(string title, string message = "") => Post(new Notice(NoticeKind.Success, title, message));

    public void Warning(string title, string message = "") => Post(new Notice(NoticeKind.Warning, title, message));

    public void Error(string title, string message = "") => Post(new Notice(NoticeKind.Error, title, message));

    /// <summary>Подключает системные уведомления. Если Windows не даёт их зарегистрировать, приложение работает без них.</summary>
    public void InitializeSystem()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, _) => App.Instance.Ui.Post(() => App.Instance.MainWindow.BringToFront());
            manager.Register();
            _systemReady = true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            App.Instance.Env.Log.Warn("Системные уведомления недоступны: " + e.Message);
        }
    }

    public void ShutdownSystem()
    {
        if (!_systemReady)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    /// <summary>Системное уведомление; показывается только пока окно приложения не активно (иначе хватает плашки).</summary>
    public bool Toast(string title, string message, bool force = false)
    {
        if (!_systemReady || (WindowActive && !force))
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder().AddText(title).AddText(message);
            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            App.Instance.Env.Log.Warn("Уведомление не показано: " + e.Message);
            return false;
        }
    }
}
