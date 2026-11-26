using Avalonia.Controls;
using ComicReader.Helpers;

namespace ComicReader.Models
{
    /// <summary>
    /// Persisted application window and reader settings stored in settings.json.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Saved main window width.
        /// </summary>
        public double WindowWidth { get; set; } = AppConstants.DefaultWindowWidth;

        /// <summary>
        /// Saved main window height.
        /// </summary>
        public double WindowHeight { get; set; } = AppConstants.DefaultWindowHeight;

        /// <summary>
        /// Saved main window state (Normal or Maximized).
        /// </summary>
        public WindowState WindowState { get; set; } = WindowState.Normal;

        /// <summary>
        /// Default zoom level applied to newly opened comics.
        /// </summary>
        public double DefaultZoomLevel { get; set; } = AppConstants.DefaultZoomLevel;
    }
}
