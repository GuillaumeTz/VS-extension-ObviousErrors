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

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Collections.Generic;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ObviousErrors
{
	internal sealed class ObviousErrors_TextAdornment : IDisposable
	{
		private readonly IAdornmentLayer layer;
		private readonly IWpfTextView view;

		private readonly Brush errorBrush;
		private readonly Pen errorPen;

		private string filePath;

		private object tagErrors = new object();

		private System.Timers.Timer tickTimer = new System.Timers.Timer();
		private Dictionary<int, List<IVsTaskItem>> errorTags = new Dictionary<int, List<IVsTaskItem>>();

		private Dictionary<uint, Brush> colorByCategory = new Dictionary<uint, Brush>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ObviousErrors_TextAdornment"/> class.
        /// </summary>
        /// <param name="view">Text view to create the adornment for</param>
        public ObviousErrors_TextAdornment(IWpfTextView view, string InFilePath)
		{
            tickTimer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
			tickTimer.Interval = 500;
			tickTimer.AutoReset = true;
			tickTimer.Enabled = true;
			tickTimer.Start();

			colorByCategory.Add(0, Brushes.GreenYellow);
            colorByCategory.Add(1, Brushes.Yellow);
            colorByCategory.Add(2, Brushes.Red);

            filePath = InFilePath;

			// Get the ITextBuffer from the IWpfTextView
			this.view = view;
			this.layer = this.view.GetAdornmentLayer("ObviousErrors_TextAdornment");

			this.view.LayoutChanged += OnLayoutChanged;
			this.view.Closed += (sender, e) =>
			{
				this.Dispose();
			};

			// Create the pen and brush to color the box behind the a's
			this.errorBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0xff));
			this.errorBrush.Freeze();

			var penBrush = new SolidColorBrush(Colors.Red);
			penBrush.Freeze();
			this.errorPen = new Pen(penBrush, 0.5);
			this.errorPen.Freeze();
		}

        internal void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs textViewLayoutChangedEventArgs)
        {
           foreach (ITextViewLine line in textViewLayoutChangedEventArgs.NewOrReformattedLines)
           {
				int lineNumber = line.Start.GetContainingLineNumber();
				if (errorTags.ContainsKey(lineNumber))
				{
					RefreshVisualForLine(lineNumber);
				}
           }
        }

        private void OnTimedEvent(object Sender, ElapsedEventArgs ElapsedEvent)
		{
			// switch to main thread ui 
			ThreadHelper.JoinableTaskFactory.Run(async () =>
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                List<int> dirtyLines = new List<int>();
				List<int> allItemLines = new List<int>();

                var taskList = (IVsTaskList)ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList));
                if (taskList == null)
                    return;

                IVsEnumTaskItems enumTaskItems;
                int hr = taskList.EnumTaskItems(out enumTaskItems);
                if (hr != VSConstants.S_OK || enumTaskItems == null)
                    return;

				// Remove empty text items
				foreach (var pair in errorTags)
				{
					for (int index = 0; index < pair.Value.Count; ++index)
					{
						pair.Value[index].get_Text(out string alreadyAddedText);
						if (alreadyAddedText == null)
						{ 
							pair.Value.RemoveAt(index);
							--index;
						}
                    }
				}

                IVsTaskItem[] items = new IVsTaskItem[1];
                while (enumTaskItems.Next(1, items) == VSConstants.S_OK)
                {
                    IVsTaskItem item = items[0];
                    // Try to get error info
                    IVsErrorItem errorTask = item as IVsErrorItem;
                    if (errorTask != null)
                    {
                        errorTask.GetCategory(out uint taskCategory);
                        item.get_Text(out string text);
                        item.Line(out int line);
                        item.Column(out int column);
                        item.Document(out string document);

						if (document != filePath)
							continue;

						allItemLines.Add(line);

                        if (!errorTags.ContainsKey(line))
                        {
                            errorTags.Add(line, new List<IVsTaskItem>());
                            dirtyLines.Add(line);
                        }

						bool bHasItemAlready = false;
						foreach (IVsTaskItem itemAlreadyAdded in errorTags[line])
						{
							itemAlreadyAdded.get_Text(out string alreadyAddedText);
							if (alreadyAddedText == text)
							{
								bHasItemAlready = true;
								break;
							}
						}

						if (bHasItemAlready)
							continue;

                        errorTags[line].Add(item);
                        dirtyLines.Add(line);
                    }
                }

				for (int line = 0; line < this.view.TextSnapshot.LineCount; ++line)
                {
                    if (!allItemLines.Contains(line))
					{
						if (errorTags.Remove(line))
							dirtyLines.Add(line);
                    }
				}

                foreach (int line in dirtyLines)
                {
                    RefreshVisualForLine(line);
                }

                tickTimer.Start();
            });
		}

		private void RefreshVisualForLine(int lineNumber)
		{
			string allMessages = string.Empty;

            if (lineNumber >= this.view.TextSnapshot.LineCount)
                return;

            var startLine = this.view.TextSnapshot.GetLineFromLineNumber(lineNumber);
            var endLine = this.view.TextSnapshot.GetLineFromLineNumber(lineNumber);

            SnapshotSpan span = new SnapshotSpan(this.view.TextSnapshot, Span.FromBounds(startLine.Start.Position, endLine.Start.Position + endLine.Length));

            this.layer.RemoveAdornmentsByVisualSpan(span);
            if (!errorTags.ContainsKey(lineNumber))
			{
				return;
			}

			List<IVsTaskItem> tasks = errorTags[lineNumber];

			uint maxCategory = 0;
			bool bAlreadyCreatedVisual = false;
			foreach (IVsTaskItem task in tasks)
			{
                task.get_Text(out string text);
                task.Column(out int column);

				uint categoryIndex = 0;
                IVsErrorItem errorTask = task as IVsErrorItem;
				if (errorTask != null)
				{
                    errorTask.GetCategory(out categoryIndex);
                }

				maxCategory = Math.Max(maxCategory, categoryIndex);

                allMessages += "# " + text + "\n  ";

				if (bAlreadyCreatedVisual)
					continue;

				if (categoryIndex != 2)
					continue;

                Geometry geometry = this.view.TextViewLines.GetMarkerGeometry(span);
				if (geometry != null)
				{
					var drawing = new GeometryDrawing(this.errorBrush, this.errorPen, geometry);
					drawing.Freeze();

					var drawingImage = new DrawingImage(drawing);
					drawingImage.Freeze();

					var image = new Image
					{
						Source = drawingImage,
					};

					// Align the image with the top of the bounds of the text geometry
					Canvas.SetLeft(image, geometry.Bounds.Left);
					Canvas.SetTop(image, geometry.Bounds.Top);

					this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, tagErrors, image, null);
					bAlreadyCreatedVisual = true;
				}
			}

			if (span != null)
			{
				Geometry geometry = this.view.TextViewLines.GetMarkerGeometry(span);
				if (geometry != null)
				{
					Brush textBrush = Brushes.Red;
					if (colorByCategory.ContainsKey(maxCategory))
					{
						textBrush = colorByCategory[maxCategory];
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
					this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, tagErrors, adornmentText, null);
				}
			}
		}

		// Implement IDisposable.
		// Do not make this method virtual.
		// A derived class should not be able to override this method.
		public void Dispose()
		{
			this.view.LayoutChanged -= OnLayoutChanged;
			GC.SuppressFinalize(this);
		}
	}
}
