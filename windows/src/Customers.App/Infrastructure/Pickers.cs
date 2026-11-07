using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Customers.Ui;

/// <summary>Окна выбора файла и папки (привязываются к главному окну, чтобы открываться поверх него).</summary>
internal static class Pickers
{
    private static IntPtr Handle => WindowNative.GetWindowHandle(App.Instance.MainWindow);

    public static async Task<string?> OpenFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Handle);
        foreach (var e in extensions.Length == 0 ? ["*"] : extensions)
        {
            picker.FileTypeFilter.Add(e);
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveFileAsync(string suggestedName, string typeTitle, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        InitializeWithWindow.Initialize(picker, Handle);
        picker.FileTypeChoices.Add(typeTitle, [extension]);
        StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, Handle);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
