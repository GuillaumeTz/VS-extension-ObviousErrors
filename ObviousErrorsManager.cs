using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ObviousErrors
{
	internal class Utils
	{
		public static string NormalizeFilePath(string FilePath)
		{
			return FilePath.Replace("\\\\", "/").Replace('\\', '/').ToLowerInvariant();
		}
	}

	internal class ErrorItem : ICloneable
	{
		public int originalLineNumber = 0;
		public int currentLineNumber = 0;
		public string text = string.Empty;
		public uint category = 0;
		public bool bIsFromIntellisense = false;

		public object Clone()
		{
			ErrorItem clone = new ErrorItem();
			clone.originalLineNumber = this.originalLineNumber;
			clone.currentLineNumber = this.currentLineNumber;
			clone.text = this.text.Clone() as string;
			clone.category = this.category;
			clone.bIsFromIntellisense = this.bIsFromIntellisense;
			return clone;
		}
	}

	internal class FilePathErrors : ICloneable
	{
		public Dictionary<int, List<ErrorItem>> errorListItemsByOriginalLineNumber = new Dictionary<int, List<ErrorItem>>();
		public HashSet<int> dirtyCurrentLines = new HashSet<int>();
		public HashSet<int> allItemOriginalLines = new HashSet<int>();

		public object Clone()
		{
			FilePathErrors clone = new FilePathErrors();
			foreach (KeyValuePair<int, List<ErrorItem>> originalPair in errorListItemsByOriginalLineNumber)
			{
				List<ErrorItem> errorItems = new List<ErrorItem>();
				foreach (ErrorItem originalErrorItem in originalPair.Value)
				{
					errorItems.Add(originalErrorItem.Clone() as ErrorItem);
				}
				clone.errorListItemsByOriginalLineNumber.Add(originalPair.Key, errorItems);
			}
			foreach (int line in dirtyCurrentLines)
			{
				clone.dirtyCurrentLines.Add(line);
			}
			foreach (int line in allItemOriginalLines)
			{
				clone.allItemOriginalLines.Add(line);
			}
			return clone;
		}
	}

	internal class ObviousErrorsManager
	{
		private static ObviousErrorsManager instance = null;
		private bool bRunning = false;
		private long LastTimeCheckStarted;
		private long LastTimeCheckEnded;

		private Dictionary<string, FilePathErrors> ErrorsByFilepath = new Dictionary<string, FilePathErrors>();

		public Dictionary<uint, Brush> colorByCategory = new Dictionary<uint, Brush>();
		public delegate void OnErrorListChangedDelegate(string filePath, FilePathErrors filePathErrors);
		public event OnErrorListChangedDelegate onErrorListChanged;

		public static void Init()
		{
			instance = new ObviousErrorsManager();
		}

		public static ObviousErrorsManager Instance()
		{
			return instance;
		}

		private ObviousErrorsManager()
		{
			colorByCategory.Add(0, Brushes.Red);
			colorByCategory.Add(1, Brushes.DarkOrange);
			colorByCategory.Add(2, Brushes.DarkGreen);

			bRunning = true;
			ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
			{
				await Task.Run(() => MainLoop());
			});
		}

		private void MainLoop()
		{
			while(bRunning)
			{
				Thread.Sleep(100);
				try
				{
					Tick();
				}
				catch (System.Exception ex)
				{
						
				}
			}
		}
		
		private void Tick()
		{
			if (Interlocked.Read(ref LastTimeCheckStarted) > Interlocked.Read(ref LastTimeCheckEnded))
				return; // Previous check is still running

			// call delegates if any file is dirty
			lock (ErrorsByFilepath)
			{
				foreach (KeyValuePair<string, FilePathErrors> pairFile in this.ErrorsByFilepath)
				{
					if (pairFile.Value.dirtyCurrentLines.Count > 0)
					{
						onErrorListChanged?.Invoke(pairFile.Key, pairFile.Value);
						pairFile.Value.dirtyCurrentLines.Clear();
					}
				}
			}

			if (DateTime.UtcNow.Ticks - Interlocked.Read(ref LastTimeCheckEnded) < TimeSpan.FromMilliseconds(1000).Ticks)
				return; // Too soon since last check

			// switch to main thread ui
			Interlocked.Exchange(ref LastTimeCheckStarted, DateTime.UtcNow.Ticks);
			ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				CheckErrors_MainThread();
				Interlocked.Exchange(ref LastTimeCheckEnded, DateTime.UtcNow.Ticks);
			});
		}

		private void CheckErrors_MainThread()
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			IVsTaskList taskList = (IVsTaskList)ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList));
			if (taskList == null)
				return;

			int hr = taskList.EnumTaskItems(out IVsEnumTaskItems enumTaskItems);
			if (hr != VSConstants.S_OK || enumTaskItems == null)
				return;

			lock (ErrorsByFilepath)
			{
				foreach (KeyValuePair<string, FilePathErrors> pairFile in this.ErrorsByFilepath)
				{
					pairFile.Value.dirtyCurrentLines.Clear();
					pairFile.Value.allItemOriginalLines.Clear();
				}

				IVsTaskItem[] taskItems = new IVsTaskItem[1];
				while (enumTaskItems.Next(1, taskItems) == VSConstants.S_OK)
				{
					IVsTaskItem taskItem = taskItems[0];
					// Try to get error info
					IVsErrorItem errorTask = taskItem as IVsErrorItem;
					if (errorTask != null)
					{
						taskItem.Document(out string filePath);
						filePath = Utils.NormalizeFilePath(filePath);

						if (!ErrorsByFilepath.ContainsKey(filePath))
						{
							ErrorsByFilepath.Add(filePath, new FilePathErrors());
						}

						FilePathErrors filePathErrors = ErrorsByFilepath[filePath];
						taskItem.get_Text(out string text);
						taskItem.Line(out int originalLine);

						filePathErrors.allItemOriginalLines.Add(originalLine);

						bool bAlreadyAdded = false;
						bool bHasKey = filePathErrors.errorListItemsByOriginalLineNumber.ContainsKey(originalLine);
						if (bHasKey)
						{
							foreach (ErrorItem item in filePathErrors.errorListItemsByOriginalLineNumber[originalLine])
							{
								if (item.originalLineNumber == originalLine && item.text == text)
								{
									bAlreadyAdded = true;
									break;
								}
							}
						}

						if (bAlreadyAdded)
							continue;

						errorTask.GetCategory(out uint categoryIndex);
						VSTASKCATEGORY[] pCat = new VSTASKCATEGORY[1];
						taskItem.Category(pCat);

						if (!bHasKey)
							filePathErrors.errorListItemsByOriginalLineNumber.Add(originalLine, new List<ErrorItem>());

						ErrorItem errorItem = new ErrorItem();
						errorItem.originalLineNumber = originalLine;
						errorItem.currentLineNumber = originalLine;
						errorItem.text = text;
						errorItem.category = categoryIndex;
						errorItem.bIsFromIntellisense = pCat[0] == VSTASKCATEGORY.CAT_CODESENSE;

						filePathErrors.errorListItemsByOriginalLineNumber[originalLine].Add(errorItem);
						filePathErrors.dirtyCurrentLines.Add(originalLine);
					}
				}

				List<int> toRemoveOriginalLines = new List<int>();
				foreach (KeyValuePair<string, FilePathErrors> pairFile in ErrorsByFilepath)
				{
					FilePathErrors filePathErrors = pairFile.Value;

					toRemoveOriginalLines.Clear();
					foreach (KeyValuePair<int, List<ErrorItem>> pair in pairFile.Value.errorListItemsByOriginalLineNumber)
					{
						if (!pairFile.Value.allItemOriginalLines.Contains(pair.Key))
						{
							foreach (ErrorItem item in pair.Value)
							{
								pairFile.Value.dirtyCurrentLines.Add(item.currentLineNumber);
							}
							toRemoveOriginalLines.Add(pair.Key);
							continue;
						}
					}

					foreach (int originalLine in toRemoveOriginalLines)
					{
						pairFile.Value.errorListItemsByOriginalLineNumber.Remove(originalLine);
					}
				}
			}
		}

		public void OnTextBufferChanged(string filePath, TextContentChangedEventArgs e)
		{
			lock (ErrorsByFilepath)
			{
				ErrorsByFilepath.TryGetValue(filePath, out FilePathErrors filePathErrors);
				if (filePathErrors == null)
				return;
			
				foreach (ITextChange change in e.Changes)
				{
					int fromLineNumber = e.Before.GetLineFromPosition(change.OldPosition).LineNumber;

					foreach (KeyValuePair<int, List<ErrorItem>> pair in filePathErrors.errorListItemsByOriginalLineNumber)
					{
						foreach (ErrorItem item in pair.Value)
						{
							if (item.bIsFromIntellisense || item.currentLineNumber < fromLineNumber)
								continue;

							item.currentLineNumber += change.LineCountDelta;
							filePathErrors.dirtyCurrentLines.Add(pair.Key);
						}
					}
				}
			}
		}

		public FilePathErrors GetClonedErrorsForFilepath(string filePath)
		{
			lock (ErrorsByFilepath)
			{
				ErrorsByFilepath.TryGetValue(filePath, out FilePathErrors filePathErrors);
				if (filePathErrors == null)
					return null;

				return filePathErrors.Clone() as FilePathErrors;
			}
		}
	}
}
