using System.Runtime.InteropServices;

namespace Customers.Ui;

/// <summary>Системное окно сообщения: единственный способ сказать об ошибке запуска, когда интерфейс ещё не создан.</summary>
internal static class NativeMessage
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);

    public static void Show(string title, string text) => MessageBox(IntPtr.Zero, text, title, 0x10 /* MB_ICONERROR */);
}
