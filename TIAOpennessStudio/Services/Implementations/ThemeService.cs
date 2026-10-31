using System;
using System.Configuration;
using System.Linq;
using System.Windows;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    public sealed class ThemeService : IThemeService
    {
        private const string ThemeSettingKey = "ThemeMode";
        private const string ThemeDictionaryMarkerKey = "ThemeDictionaryMarker";

        public ThemeMode CurrentTheme { get; private set; } = ThemeMode.Dark;

        public event EventHandler<ThemeMode> ThemeChanged;

        public void ApplySavedTheme()
        {
            try
            {
                string raw = ReadThemeSettingOrDefault();
                ThemeMode mode;
                if (!Enum.TryParse(raw, true, out mode))
                {
                    mode = ThemeMode.Dark;
                }

                SetTheme(mode);
            }
            catch
            {
                // Never block startup because of theme state.
                SetTheme(ThemeMode.Dark);
            }
        }

        public void SetTheme(ThemeMode mode)
        {
            var app = Application.Current;
            if (app == null) return;

            string relativePath = mode == ThemeMode.Light
                ? "Themes/Theme.Light.xaml"
                : "Themes/Theme.Dark.xaml";
            var uri = new Uri(relativePath, UriKind.Relative);

            var dictionaries = app.Resources.MergedDictionaries;
            var existing = dictionaries.FirstOrDefault(d => d.Contains(ThemeDictionaryMarkerKey));
            var next = new ResourceDictionary { Source = uri };

            if (existing != null)
            {
                int idx = dictionaries.IndexOf(existing);
                if (idx >= 0) dictionaries[idx] = next;
                else dictionaries.Add(next);
            }
            else
            {
                dictionaries.Insert(0, next);
            }

            CurrentTheme = mode;
            WriteThemeSetting(mode);
            ThemeChanged?.Invoke(this, mode);

            // Refresh controls not reached by the WPF DynamicResource chain (centralized, enumerated)
            RuntimeThemeRefresher.RefreshAll(app.MainWindow);
        }

        private static string ReadThemeSettingOrDefault()
        {
            try
            {
                var settings = Properties.Settings.Default;
                bool hasProperty = settings.Properties
                    .Cast<SettingsProperty>()
                    .Any(p => string.Equals(p.Name, ThemeSettingKey, StringComparison.Ordinal));
                if (!hasProperty) return "Dark";

                return (settings[ThemeSettingKey] as string) ?? "Dark";
            }
            catch
            {
                return "Dark";
            }
        }

        private static void WriteThemeSetting(ThemeMode mode)
        {
            try
            {
                var settings = Properties.Settings.Default;
                bool hasProperty = settings.Properties
                    .Cast<SettingsProperty>()
                    .Any(p => string.Equals(p.Name, ThemeSettingKey, StringComparison.Ordinal));
                if (!hasProperty) return;

                settings[ThemeSettingKey] = mode.ToString();
                settings.Save();
            }
            catch
            {
                // Do not block UI startup on settings persistence failures.
            }
        }
    }
}
