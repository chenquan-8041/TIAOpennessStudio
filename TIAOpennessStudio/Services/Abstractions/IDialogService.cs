using System;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Abstraction for UI dialog interactions to maintain MVVM separation.
    /// </summary>
    public interface IDialogService
    {
        /// <summary>
        /// Shows a warning message dialog.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="title">The dialog title.</param>
        void ShowWarning(string message, string title);

        /// <summary>
        /// Shows an information message dialog.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="title">The dialog title.</param>
        void ShowInformation(string message, string title);

        /// <summary>
        /// Shows an error message dialog.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="title">The dialog title.</param>
        void ShowError(string message, string title);

        /// <summary>
        /// Shows a confirmation dialog with OK/Cancel buttons.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="title">The dialog title.</param>
        /// <returns>True if confirmed, false otherwise.</returns>
        bool ShowConfirmation(string message, string title);

        /// <summary>
        /// Shows a diff preview dialog for applying fixes.
        /// </summary>
        /// <param name="targetName">The target block name.</param>
        /// <param name="diffText">The diff text to display.</param>
        /// <returns>True if the user approves the diff, false otherwise.</returns>
        bool ShowDiffConfirmDialog(string targetName, string diffText);
    }
}