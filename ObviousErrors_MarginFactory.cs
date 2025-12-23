using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace ObviousErrors
{
    /// <summary>
    /// Export a <see cref="IWpfTextViewMarginProvider"/>, which returns an instance of the margin for the editor to use.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(ObviousErrors_Margin.MarginName)]
    [Order(After = PredefinedMarginNames.OverviewChangeTracking, Before = PredefinedMarginNames.OverviewMark)]
    [MarginContainer(PredefinedMarginNames.VerticalScrollBar)]       
    [ContentType("text")]                                       
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class ObviousErrors_MarginFactory : IWpfTextViewMarginProvider
    {
        #region IWpfTextViewMarginProvider

        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        /// <summary>
        /// Creates an <see cref="IWpfTextViewMargin"/> for the given <see cref="IWpfTextViewHost"/>.
        /// </summary>
        /// <param name="wpfTextViewHost">The <see cref="IWpfTextViewHost"/> for which to create the <see cref="IWpfTextViewMargin"/>.</param>
        /// <param name="marginContainer">The margin that will contain the newly-created margin.</param>
        /// <returns>The <see cref="IWpfTextViewMargin"/>.
        /// The value may be null if this <see cref="IWpfTextViewMarginProvider"/> does not participate for this context.
        /// </returns>
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            string FilePath = string.Empty;
            // Get the ITextDocument from the ITextBuffer
            if (TextDocumentFactoryService != null &&
                TextDocumentFactoryService.TryGetTextDocument(wpfTextViewHost.TextView.TextBuffer, out ITextDocument textDocument))
            {
                FilePath = textDocument.FilePath;
            }

            IVerticalScrollBar scrollBar = marginContainer as IVerticalScrollBar;

            return new ObviousErrors_Margin(wpfTextViewHost.TextView, scrollBar, FilePath);
        }

        #endregion
    }
}
