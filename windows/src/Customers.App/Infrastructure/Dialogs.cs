using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Customers.Ui;

/// <summary>Диалоги ContentDialog. WinUI показывает только один диалог за раз, поэтому остальные ждут своей очереди.</summary>
internal static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            var root = App.Instance.MainWindow.RootElement;
            dialog.XamlRoot = root.XamlRoot;
            dialog.RequestedTheme = root.RequestedTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string primary = "Да", string close = "Отмена")
    {
        var dialog = new ContentDialog {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task InfoAsync(string title, string message)
    {
        var dialog = new ContentDialog {
            Title = title,
            Content = new ScrollViewer {
                MaxHeight = 420,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "Закрыть",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog);
    }

    /// <summary>Запрашивает одну строку (или пароль). Возвращает null, если пользователь отказался.</summary>
    public static async Task<string?> PromptAsync(string title, string message, string placeholder = "", bool password = false, string initial = "", string primary = "Готово")
    {
        FrameworkElement input;
        Func<string> read;
        if (password)
        {
            var box = new PasswordBox { PlaceholderText = placeholder };
            input = box;
            read = () => box.Password;
        }
        else
        {
            var box = new TextBox { PlaceholderText = placeholder, Text = initial };
            input = box;
            read = () => box.Text;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
        if (message.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(input);
        var dialog = new ContentDialog {
            Title = title,
            Content = panel,
            PrimaryButtonText = primary,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? read() : null;
    }
}
