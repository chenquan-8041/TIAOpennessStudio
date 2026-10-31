using System;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// On theme switch, refreshes controls that the WPF DynamicResource chain cannot
    /// update on its own. AvalonEdit's syntax colorizer resolves brushes from
    /// Application resources on each use, but the editor still needs a forced redraw
    /// so ColorizeLine runs again and picks up the new theme colors.
    /// </summary>
    public static class RuntimeThemeRefresher
    {
        /// <summary>
        /// Refreshes affected controls. Call after ThemeService.SetTheme finishes.
        /// Safe to call: if a control is not yet initialized or not on the visual
        /// tree, skip it rather than throw.
        /// </summary>
        public static void RefreshAll(Window mainWindow)
        {
            try
            {
                RefreshCodeEditorTabs(mainWindow);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[RuntimeThemeRefresher] RefreshAll caught: {ex.Message}");
            }
        }

        // ---- Per-control refresh logic ----

        /// <summary>
        /// Refreshes the AvalonEdit TextEditor controls in all dynamic code tabs.
        /// </summary>
        private static void RefreshCodeEditorTabs(Window mainWindow)
        {
            var tabsCode = TryGetField(mainWindow, "tabsCode") as TabControl;
            if (tabsCode == null) return;

            foreach (TabItem tab in tabsCode.Items)
            {
                if (tab.Visibility != Visibility.Visible) continue;
                var grid = tab.Content as Grid;
                if (grid == null) continue;
                foreach (var child in grid.Children)
                {
                    if (child is TextEditor editor)
                    {
                        editor.TextArea.TextView.Redraw();
                    }
                }
            }
        }

        private static object TryGetField(object target, string name)
        {
            if (target == null) return null;
            try
            {
                var type = target.GetType();
                var field = type.GetField(name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (field != null) return field.GetValue(target);
                var prop = type.GetProperty(name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                return prop?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }
    }
}
