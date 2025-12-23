/* Copyright(C) 2025 guillaume.taze@proton.me

This program is free software : you can redistribute it and /or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.If not, see < https://www.gnu.org/licenses/>.
*/

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ObviousErrors
{
	internal sealed class ObviousErrors_TextAdornment : IDisposable
	{
		private readonly IAdornmentLayer layer;
		private readonly IWpfTextView textView;

		private readonly Brush errorBrush;
		private readonly Pen errorPen;

		private readonly string filePath;

		FilePathErrors filePathErrors = null;
		private object lockFilePathErrors = new object();

		/// <summary>
		/// Initializes a new instance of the <see cref="ObviousErrors_TextAdornment"/> class.
		/// </summary>
		/// <param name="view">Text view to create the adornment for</param>
		public ObviousErrors_TextAdornment(IWpfTextView inTextView, string inFilePath)
		{
			this.textView = inTextView;
			this.textView.Properties.AddProperty(typeof(ObviousErrors_TextAdornment), this);
			this.filePath = Utils.NormalizeFilePath(inFilePath); ;
			this.layer = this.textView.GetAdornmentLayer("ObviousErrors_TextAdornment");

			this.textView.TextBuffer.Changed += OnTextBufferChanged;
			this.textView.LayoutChanged += OnLayoutChanged;
			this.textView.Closed += (sender, e) =>
			{
				this.Dispose();
			};

			// Create the pen and brush to color the box behind the a's
			this.errorBrush = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0xff));
			this.errorBrush.Freeze();

			var penBrush = new SolidColorBrush(Colors.Red);
			penBrush.Freeze();
			this.errorPen = new Pen(penBrush, 0.5);
			this.errorPen.Freeze();

			ObviousErrorsManager.Instance().onErrorListChanged += OnErrorListChanged;
			lock (lockFilePathErrors)
			{
				filePathErrors = ObviousErrorsManager.Instance().GetClonedErrorsForFilepath(filePath);
			}
		}

		private void OnErrorListChanged(string originalFilePath, FilePathErrors originalFilePathErrors)
		{
			if (originalFilePath != this.filePath)
				return;

			lock(lockFilePathErrors)
			{
				filePathErrors = originalFilePathErrors.Clone() as FilePathErrors;
			}

			ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				lock (lockFilePathErrors)
				{
					foreach (int line in this.filePathErrors.dirtyCurrentLines)
					{
						RefreshVisualForLine(line);
					}
					this.filePathErrors.dirtyCurrentLines.Clear();
				}
			});
		}

		private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
		{
			ObviousErrorsManager.Instance().OnTextBufferChanged(filePath, e);
		}

		private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs textViewLayoutChangedEventArgs)
		{
			ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				lock (lockFilePathErrors)
				{
					foreach (ITextViewLine line in textViewLayoutChangedEventArgs.NewOrReformattedLines)
					{
						int lineNumber = line.Start.GetContainingLineNumber();
						RefreshVisualForLine(lineNumber);
					}
				}
			});
		}

		private void RefreshVisualForLine(int currentLineNumber)
		{
			if (currentLineNumber < 0 || currentLineNumber >= this.textView.TextSnapshot.LineCount || filePathErrors == null)
				return;

			ITextSnapshotLine startLine = this.textView.TextSnapshot.GetLineFromLineNumber(currentLineNumber);
			ITextSnapshotLine endLine = this.textView.TextSnapshot.GetLineFromLineNumber(currentLineNumber);

			SnapshotSpan span = new SnapshotSpan(this.textView.TextSnapshot, Span.FromBounds(startLine.Start.Position, endLine.Start.Position + endLine.Length));
			this.layer.RemoveAdornmentsByVisualSpan(span);

			string allMessages = string.Empty;
			uint minCategory = 10000;
			bool bAlreadyCreatedVisual = false;
			bool bHasItem = false;
			foreach (KeyValuePair<int, List<ErrorItem>> pair in filePathErrors.errorListItemsByOriginalLineNumber)
			{
				foreach (ErrorItem item in pair.Value)
				{
					if (item.currentLineNumber != currentLineNumber)
						continue;

					bHasItem = true;
					minCategory = Math.Min(minCategory, item.category);
					allMessages += "# " + item.text + "  |  ";

					if (bAlreadyCreatedVisual)
						continue;

					if (item.category != 0)
						continue;

					Geometry geometry = this.textView.TextViewLines.GetMarkerGeometry(span);
					if (geometry != null)
					{
						GeometryDrawing drawing = new GeometryDrawing(this.errorBrush, this.errorPen, geometry);
						drawing.Freeze();

						DrawingImage drawingImage = new DrawingImage(drawing);
						drawingImage.Freeze();

						Image image = new Image
						{
							Source = drawingImage,
						};

						// Align the image with the top of the bounds of the text geometry
						Canvas.SetLeft(image, geometry.Bounds.Left);
						Canvas.SetTop(image, geometry.Bounds.Top);

						this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, image, null);
						bAlreadyCreatedVisual = true;
					}
				}
			}

			if (bHasItem)
			{
				Geometry geometry = this.textView.TextViewLines.GetMarkerGeometry(span);
				if (geometry != null)
				{
					Brush textBrush = Brushes.Red;
					ObviousErrorsManager.Instance().colorByCategory.TryGetValue(minCategory, out Brush maybeBrush);
					if (maybeBrush != null)
					{
						textBrush = maybeBrush;
					}

					// Create a label with the text adornment
					TextBlock adornmentText = new TextBlock
					{
						Text = allMessages,
						Foreground = textBrush,
						Background = Brushes.Transparent,
						Padding = new Thickness(5),
						FontSize = 12
					};

					// Measure the adornment width and set the position at the end of the line
					Canvas.SetLeft(adornmentText, geometry.Bounds.Right + 10.0); // Place it at the end of the line
					Canvas.SetTop(adornmentText, geometry.Bounds.Top - 5.0);

					// Add the adornment to the adornment layer
					this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, adornmentText, null);
				}
			}
		}

		// Implement IDisposable.
		// Do not make this method virtual.
		// A derived class should not be able to override this method.
		public void Dispose()
		{
			ObviousErrorsManager.Instance().onErrorListChanged -= OnErrorListChanged;
			this.textView.TextBuffer.Changed -= OnTextBufferChanged;
			this.textView.LayoutChanged -= OnLayoutChanged;
			GC.SuppressFinalize(this);
		}
	}
}
