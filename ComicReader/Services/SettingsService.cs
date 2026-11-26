using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Persists application window size and reader settings to settings.json.
    /// </summary>
    public sealed class SettingsService : ISettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly SemaphoreSlim _ioLock = new(1, 1);

        /// <inheritdoc />
        public async Task<AppSettings> LoadAsync()
        {
            await _ioLock.WaitAsync();
            try
            {
                if (!File.Exists(AppConstants.SettingsFilePath))
                {
                    return new AppSettings();
                }

                try
                {
                    var info = new FileInfo(AppConstants.SettingsFilePath);
                    if (info.Length == 0)
                    {
                        return new AppSettings();
                    }

                    await using var stream = File.OpenRead(AppConstants.SettingsFilePath);
                    return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
                }
                catch (JsonException)
                {
                    return new AppSettings();
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(AppSettings settings)
        {
            await _ioLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(AppConstants.AppDataDirectory);
                string tempPath = AppConstants.SettingsFilePath + ".tmp";
                await using (var stream = File.Create(tempPath))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
                    await stream.FlushAsync();
                }

                File.Move(tempPath, AppConstants.SettingsFilePath, overwrite: true);
            }
            finally
            {
                _ioLock.Release();
            }
        }
    }
}
