using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Customers.Ui;

/// <summary>
/// Одно окно приложения на пользователя. Второй запуск передаёт первому сигнал (и ссылку из письма, если она есть)
/// и сразу завершается; первое окно выходит на передний план.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static string PipeName => "Customers-" + Environment.UserName.ToLowerInvariant();

    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, @"Local\Customers.SingleInstance", out var created);
            return created;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return true; // не удалось создать замок: лучше открыть второе окно, чем не открыть ни одного
        }
    }

    /// <summary>Вызывается вторым запуском: передаёт ссылку или просьбу показать окно.</summary>
    public static void NotifyRunningInstance(string? link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var data = Encoding.UTF8.GetBytes(link ?? "activate");
            client.Write(data, 0, data.Length);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // Первое окно не отвечает: ничего не поделать, второй запуск просто завершается.
        }
    }

    /// <summary>Слушает сообщения от повторных запусков. Обработчик вызывается не в потоке интерфейса.</summary>
    public static void Listen(Action<string> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    var buffer = new byte[4096];
                    var read = server.Read(buffer, 0, buffer.Length);
                    onMessage(Encoding.UTF8.GetString(buffer, 0, read));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true, Name = "Customers.SingleInstance" };
        thread.Start();
    }

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hwnd);
}
