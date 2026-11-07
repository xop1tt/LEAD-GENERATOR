using System.Diagnostics;

namespace Customers.Ui;

/// <summary>
/// Обновление: последний релиз на GitHub, скачивание установщика и проверка его SHA-256 (Customers.Core.UpdateChecker),
/// затем тихая установка поверх. Данные пользователя лежат в %LOCALAPPDATA%\Customers и установщиком не затрагиваются.
/// </summary>
internal sealed class UpdateService
{
    private readonly AppEnvironment _env;

    public UpdateService(AppEnvironment env) => _env = env;

    public static AppVersion Current => AppVersion.Parse(AppBuild.Version) ?? AppVersion.Parse("1.0.0")!;

    public UpdateOffer? Offer { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    public async Task<UpdateOffer?> CheckAsync(CancellationToken ct = default)
    {
        Offer = await new UpdateChecker(_env.SearchTransport()).CheckAsync(Current, ct).ConfigureAwait(true);
        CheckedAt = DateTimeOffset.Now;
        return Offer;
    }

    /// <summary>Скачивает и проверяет установщик, запускает его и возвращает true — приложение должно завершиться.</summary>
    public async Task<bool> InstallAsync(UpdateOffer offer, CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Customers-update");
        var path = await new UpdateChecker(_env.SearchTransport()).DownloadAsync(offer, folder, ct).ConfigureAwait(true);
        _env.Log.Info("Установщик обновления скачан и проверен: " + path);
        using var process = Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
        return process is not null;
    }
}
