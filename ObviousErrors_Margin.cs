using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ObviousErrors
{
	/// <summary>
	/// Margin's canvas and visual definition including both size and content
	/// </summary>
	internal class ObviousErrors_Margin : Canvas, IWpfTextViewMargin
	{
		public const string MarginName = "ObviousErrors_Margin";
		private readonly string filePath;
		private bool isDisposed;

		private IVerticalScrollBar scrollBar = null;
		private IWpfTextView textView = null;

		private FilePathErrors filePathErrors = null;
		private object lockFilePathErrors = new object();

		public ObviousErrors_Margin(IWpfTextView inTextView, IVerticalScrollBar inScrollBar, string inFilePath)
		{
			this.scrollBar = inScrollBar;
			this.textView = inTextView;
			this.filePath = Utils.NormalizeFilePath(inFilePath);

			this.textView.TextBuffer.Changed += TextView_TextBufferChanged;
			this.scrollBar.TrackSpanChanged += OnMappingChanged;

			ObviousErrorsManager.Instance().onErrorListChanged += OnErrorListChanged;
			lock (lockFilePathErrors)
			{
				filePathErrors = ObviousErrorsManager.Instance().GetClonedErrorsForFilepath(filePath);
			}
		}

		void OnErrorListChanged(string filePath, FilePathErrors originalFilePathErrors)
		{
			if (filePath != this.filePath)
				return;

			lock (lockFilePathErrors)
			{
				this.filePathErrors = originalFilePathErrors.Clone() as FilePathErrors;
			}

			ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				InvalidateVisual();
			});
		}

		void TextView_TextBufferChanged(object sender, TextContentChangedEventArgs args)
		{
			if (args.Changes.Count == 0)
				return;

			// check if has line number diff
			bool bHasLineDeltaCountDiff = false;
			foreach (ITextChange change in args.Changes)
			{
				if (change.LineCountDelta != 0)
				{
					bHasLineDeltaCountDiff = true;
					break;
				}
			}

			if (!bHasLineDeltaCountDiff)
				return;

			InvalidateVisual();
		}

		void OnMappingChanged(object sender, EventArgs e)
		{
			InvalidateVisual();
		}
		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);
			if (this.textView.IsClosed != false)
				return;

			DrawMarkers(drawingContext);
		}

		void DrawMarkers(DrawingContext drawingContext)
		{
			lock (lockFilePathErrors)
			{
				if (filePathErrors == null)
				return;

				SolidColorBrush redPenBrush = new SolidColorBrush(Colors.Red);
				foreach (KeyValuePair<int, List<ErrorItem>> pair in filePathErrors.errorListItemsByOriginalLineNumber)
				{
					foreach (ErrorItem item in pair.Value)
					{
						if (item.currentLineNumber < 0 || item.currentLineNumber >= this.textView.TextSnapshot.LineCount)
							continue;

						ObviousErrorsManager.Instance().colorByCategory.TryGetValue(item.category, out Brush maybeBrush);
						Pen pen = new Pen(maybeBrush != null ? maybeBrush : redPenBrush, 2);
						double y = this.scrollBar.GetYCoordinateOfBufferPosition(new SnapshotPoint(this.textView.TextSnapshot, this.textView.TextSnapshot.GetLineFromLineNumber(item.currentLineNumber).Start));
						drawingContext.DrawLine(pen, new Point(-150, y), new Point(100, y));
					}
				}
			}
		}

		#region IWpfTextViewMargin

		/// <summary>
		/// Gets the <see cref="Sytem.Windows.FrameworkElement"/> that implements the visual representation of the margin.
		/// </summary>
		/// <exception cref="ObjectDisposedException">The margin is disposed.</exception>
		public FrameworkElement VisualElement
		{
			// Since this margin implements Canvas, this is the object which renders
			// the margin.
			get
			{
				this.ThrowIfDisposed();
				return this;
			}
		}

		#endregion

		#region ITextViewMargin

		/// <summary>
		/// Gets the size of the margin.
		/// </summary>
		/// <remarks>
		/// For a horizontal margin this is the height of the margin,
		/// since the width will be determined by the <see cref="ITextView"/>.
		/// For a vertical margin this is the width of the margin,
		/// since the height will be determined by the <see cref="ITextView"/>.
		/// </remarks>
		/// <exception cref="ObjectDisposedException">The margin is disposed.</exception>
		public double MarginSize
		{
			get
			{
				this.ThrowIfDisposed();

				// Since this is a horizontal margin, its width will be bound to the width of the text view.
				// Therefore, its size is its height.
				return this.ActualHeight;
			}
		}

		/// <summary>
		/// Gets a value indicating whether the margin is enabled.
		/// </summary>
		/// <exception cref="ObjectDisposedException">The margin is disposed.</exception>
		public bool Enabled
		{
			get
			{
				this.ThrowIfDisposed();

				// The margin should always be enabled
				return true;
			}
		}

		/// <summary>
		/// Gets the <see cref="ITextViewMargin"/> with the given <paramref name="marginName"/> or null if no match is found
		/// </summary>
		/// <param name="marginName">The name of the <see cref="ITextViewMargin"/></param>
		/// <returns>The <see cref="ITextViewMargin"/> named <paramref name="marginName"/>, or null if no match is found.</returns>
		/// <remarks>
		/// A margin returns itself if it is passed its own name. If the name does not match and it is a container margin, it
		/// forwards the call to its children. Margin name comparisons are case-insensitive.
		/// </remarks>
		/// <exception cref="ArgumentNullException"><paramref name="marginName"/> is null.</exception>
		public ITextViewMargin GetTextViewMargin(string marginName)
		{
			return string.Equals(marginName, ObviousErrors_Margin.MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;
		}

		/// <summary>
		/// Disposes an instance of <see cref="ObviousErrors_Margin"/> class.
		/// </summary>
		public void Dispose()
		{
			if (!this.isDisposed)
			{
				this.textView.TextBuffer.Changed -= TextView_TextBufferChanged;
				this.scrollBar.TrackSpanChanged -= OnMappingChanged;

				ObviousErrorsManager.Instance().onErrorListChanged -= OnErrorListChanged;

				GC.SuppressFinalize(this);
				this.isDisposed = true;
			}
		}

		#endregion

		private void ThrowIfDisposed()
		{
			if (this.isDisposed)
			{
				throw new ObjectDisposedException(MarginName);
			}
		}
	}
}
