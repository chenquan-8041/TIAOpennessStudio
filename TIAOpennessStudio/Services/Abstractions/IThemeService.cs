namespace TIAOpennessStudio.Services.Abstractions
{
    public enum ThemeMode
    {
        Dark,
        Light
    }

    public interface IThemeService
    {
        ThemeMode CurrentTheme { get; }
        void ApplySavedTheme();
        void SetTheme(ThemeMode mode);

        /// <summary>
        /// Raised after the active theme dictionary has been swapped. Subscribers (e.g. controls
        /// that need to re-apply runtime brush injection) can use this hook to refresh themselves.
        /// </summary>
        event System.EventHandler<ThemeMode> ThemeChanged;
    }
}
