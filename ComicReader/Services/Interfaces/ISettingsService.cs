using System.Threading.Tasks;
using ComicReader.Models;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Persists and loads application-wide window and reader settings.
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>
        /// Loads settings from settings.json, or returns defaults if none exist.
        /// </summary>
        Task<AppSettings> LoadAsync();

        /// <summary>
        /// Saves the provided settings to settings.json.
        /// </summary>
        Task SaveAsync(AppSettings settings);
    }
}
