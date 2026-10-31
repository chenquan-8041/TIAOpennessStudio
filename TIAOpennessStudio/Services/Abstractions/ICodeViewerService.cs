using System;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TIAOpennessStudio.Services.Abstractions
{
    /// <summary>
    /// Abstraction for code viewing functionality to maintain MVVM separation.
    /// </summary>
    public interface ICodeViewerService
    {
        /// <summary>
        /// Shows the code viewer for a given block or type.
        /// </summary>
        /// <param name="name">The name of the block/type.</param>
        /// <param name="type">The type string (Block/Type).</param>
        /// <param name="content">The content to display.</param>
        void ShowCodeViewer(string name, string type, object content);

        /// <summary>
        /// Creates a code editor with syntax highlighting and folding.
        /// </summary>
        /// <param name="content">The code content.</param>
        /// <returns>A configured TextEditor.</returns>
        TextEditor CreateCodeEditor(string content);

        /// <summary>
        /// Renders content to a grid, handling different content types.
        /// </summary>
        /// <param name="grid">The target grid.</param>
        /// <param name="content">The content to render.</param>
        void RenderContentToGrid(Grid grid, object content);

        /// <summary>
        /// Creates a multi-tab view for LAD/FBD/GRAPH blocks.
        /// </summary>
        /// <param name="xmlContent">The XML content.</param>
        /// <param name="mainHeader">The main tab header.</param>
        /// <param name="mainContent">The main content (UIElement or string).</param>
        /// <param name="sclContent">Optional SCL content for SCL tab.</param>
        /// <param name="jsonIrContent">Optional JSON IR content for JSON tab.</param>
        /// <param name="isSafety">True indicates this is a safety (F-Program) block; a safety badge is shown atop the main tab.</param>
        /// <returns>A UIElement containing the tab control.</returns>
        UIElement CreateMultiTabView(string xmlContent, string mainHeader, object mainContent, string sclContent = null, string jsonIrContent = null, bool isSafety = false);

        /// <summary>
        /// Tries to generate source code for a block or type.
        /// </summary>
        /// <param name="item">The block or type object.</param>
        /// <param name="software">The PLC software instance.</param>
        /// <returns>The generated source code, or null if not supported.</returns>
        string TryGenerateSource(object item, PlcSoftware software);

        /// <summary>
        /// Checks if content appears to be SCL code.
        /// </summary>
        /// <param name="content">The content to check.</param>
        /// <returns>True if content looks like SCL.</returns>
        bool IsSclContent(string content);
    }
}