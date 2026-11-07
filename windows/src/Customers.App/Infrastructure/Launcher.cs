using Windows.ApplicationModel.DataTransfer;

namespace Customers.Ui;

/// <summary>Открытие ссылок и папок, копирование в буфер обмена.</summary>
internal static class Launcher
{
    /// <summary>Открывает http(s)-адрес в браузере по умолчанию. Другие схемы (file:, javascript: и т. п.) не открываются.</summary>
    public static async Task<bool> OpenWebAsync(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    public static async Task<bool> OpenAsync(Uri? uri) => uri is not null && await Windows.System.Launcher.LaunchUriAsync(uri);

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            App.Instance.Env.Log.Error("Не удалось открыть папку", e);
        }
    }

    /// <summary>Показывает файл в Проводнике.</summary>
    public static void Reveal(string file)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            App.Instance.Env.Log.Error("Не удалось показать файл", e);
        }
    }

    public static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
