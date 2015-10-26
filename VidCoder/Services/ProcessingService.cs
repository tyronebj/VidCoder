﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using HandBrake.ApplicationServices.Interop.EventArgs;
using HandBrake.ApplicationServices.Interop.Json.Scan;
using VidCoder.Messages;
using VidCoder.Model;
using VidCoder.Resources;
using VidCoder.Services.Windows;
using VidCoder.ViewModel;
using VidCoder.ViewModel.DataModels;
using VidCoderCommon.Extensions;
using VidCoderCommon.Model;
using Color = System.Windows.Media.Color;

namespace VidCoder.Services
{
	/// <summary>
	/// Controls the queue and actual processing of encode jobs.
	/// </summary>
	public class ProcessingService : ObservableObject
	{
		public const int QueuedTabIndex = 0;
		public const int CompletedTabIndex = 1;

		private const double StopWarningThresholdMinutes = 5;

		private ILogger logger = Ioc.Get<ILogger>();
		private IProcessAutoPause autoPause = Ioc.Get<IProcessAutoPause>();
		private ISystemOperations systemOperations = Ioc.Get<ISystemOperations>();
		private IMessageBoxService messageBoxService = Ioc.Get<IMessageBoxService>();
		private MainViewModel main = Ioc.Get<MainViewModel>();
		private OutputPathService outputVM = Ioc.Get<OutputPathService>();
		private PresetsService presetsService = Ioc.Get<PresetsService>();
		private PickersService pickersService = Ioc.Get<PickersService>();
		private IWindowManager windowManager = Ioc.Get<IWindowManager>();

		private ObservableCollection<EncodeJobViewModel> encodeQueue;
		private bool encoding;
		private bool paused;
		private bool encodeStopped;
		private int totalTasks;
		private int taskNumber;
		private bool encodeSpeedDetailsAvailable;
		private Stopwatch elapsedQueueEncodeTime;
		private long pollCount = 0;
		private string estimatedTimeRemaining;
		private double currentFps;
		private double averageFps;
		private double completedQueueWork;
		private double totalQueueCost;
		private double overallEncodeProgressFraction;
		private TimeSpan overallEtaSpan;
		private TimeSpan currentJobEta; // Kept around to check if the job finished early
		private TaskbarItemProgressState encodeProgressState;
		private ObservableCollection<EncodeResultViewModel> completedJobs;
		private List<EncodeCompleteAction> encodeCompleteActions; 
		private EncodeCompleteAction encodeCompleteAction;
		private IEncodeProxy encodeProxy;
		private bool profileEditedSinceLastQueue;

		private int selectedTabIndex;

		public ProcessingService()
		{
			this.encodeQueue = new ObservableCollection<EncodeJobViewModel>();
			this.encodeQueue.CollectionChanged += (sender, e) => { this.SaveEncodeQueue(); };
			IList<EncodeJobWithMetadata> jobs = EncodeJobStorage.EncodeJobs;
			foreach (EncodeJobWithMetadata job in jobs)
			{
				this.encodeQueue.Add(new EncodeJobViewModel(job.Job) { SourceParentFolder = job.SourceParentFolder, ManualOutputPath = job.ManualOutputPath, NameFormatOverride = job.NameFormatOverride, PresetName = job.PresetName});
			}

			this.autoPause.PauseEncoding += this.AutoPauseEncoding;
			this.autoPause.ResumeEncoding += this.AutoResumeEncoding;

			this.encodeQueue.CollectionChanged +=
				(o, e) =>
					{
						if (e.Action != NotifyCollectionChangedAction.Replace && e.Action != NotifyCollectionChangedAction.Move)
						{
							this.RefreshEncodeCompleteActions();
						}

						this.EncodeCommand.RaiseCanExecuteChanged();
						this.RaisePropertyChanged(() => this.QueueHasItems);
					};

			Messenger.Default.Register<VideoSourceChangedMessage>(
				this,
				message =>
					{
						RefreshCanEnqueue();
						this.EncodeCommand.RaiseCanExecuteChanged();
					});

			Messenger.Default.Register<OutputPathChangedMessage>(
				this,
				message =>
					{
						RefreshCanEnqueue();
						this.EncodeCommand.RaiseCanExecuteChanged();
					});

			Messenger.Default.Register<SelectedTitleChangedMessage>(
				this,
				message =>
					{
						this.QueueTitlesCommand.RaiseCanExecuteChanged();
					});

			Messenger.Default.Register<EncodingProfileChangedMessage>(
				this,
				message =>
					{
						this.profileEditedSinceLastQueue = true;
					});

			this.completedJobs = new ObservableCollection<EncodeResultViewModel>();
			this.completedJobs.CollectionChanged +=
				(o, e) =>
				{
					if (e.Action != NotifyCollectionChangedAction.Replace && e.Action != NotifyCollectionChangedAction.Move)
					{
						this.RefreshEncodeCompleteActions();
					}
				};

			this.RefreshEncodeCompleteActions();

			if (Config.ResumeEncodingOnRestart && this.encodeQueue.Count > 0)
			{
				DispatchUtilities.BeginInvoke(() =>
					{
						this.StartEncodeQueue();
					});
			}
		}

		public ObservableCollection<EncodeJobViewModel> EncodeQueue
		{
			get
			{
				return this.encodeQueue;
			}
		}

		public EncodeJobViewModel CurrentJob
		{
			get
			{
				return this.EncodeQueue[0];
			}
		}

		public IEncodeProxy EncodeProxy
		{
			get
			{
				return this.encodeProxy;
			}
		}

		public ObservableCollection<EncodeResultViewModel> CompletedJobs
		{
			get
			{
				return this.completedJobs;
			}
		}

		public int CompletedItemsCount
		{
			get
			{
				return this.completedJobs.Count();
			}
		}

		public bool CanTryEnqueue
		{
			get
			{
				return this.main.HasVideoSource;
			}
		}

		public bool Encoding
		{
			get
			{
				return this.encoding;
			}

			set
			{
				this.encoding = value;

				if (value)
				{
					SystemSleepManagement.PreventSleep();
					this.elapsedQueueEncodeTime = Stopwatch.StartNew();
				}
				else
				{
					this.EncodeSpeedDetailsAvailable = false;
					SystemSleepManagement.AllowSleep();
					this.elapsedQueueEncodeTime.Stop();
				}

				this.PauseCommand.RaiseCanExecuteChanged();
				this.RaisePropertyChanged(() => this.PauseVisible);
				this.RaisePropertyChanged(() => this.Encoding);
				this.RaisePropertyChanged(() => this.EncodeButtonText);

				if (!value)
				{
					this.EncodeProgress = new EncodeProgress { Encoding = false };
				}
			}
		}

		public bool QueueHasItems
		{
			get
			{
				return this.EncodeQueue.Count > 0;
			}
		}

		public bool Paused
		{
			get
			{
				return this.paused;
			}

			set
			{
				this.paused = value;

				if (this.elapsedQueueEncodeTime != null)
				{
					if (value)
					{
						this.elapsedQueueEncodeTime.Stop();
					}
					else
					{
						this.elapsedQueueEncodeTime.Start();
					}
				}

				this.RaisePropertyChanged(() => this.PauseVisible);
				this.RaisePropertyChanged(() => this.ProgressBarColor);
				this.RaisePropertyChanged(() => this.Paused);
			}
		}

		public string EncodeButtonText
		{
			get
			{
				if (this.Encoding)
				{
					return MainRes.Resume;
				}
				else
				{
					return MainRes.Encode;
				}
			}
		}

		public bool PauseVisible
		{
			get
			{
				return this.Encoding && !this.Paused;
			}
		}

		public string QueuedTabHeader
		{
			get
			{
				if (this.EncodeQueue.Count == 0)
				{
					return MainRes.Queued;
				}

				return string.Format(MainRes.QueuedWithTotal, this.EncodeQueue.Count);
			}
		}

		public string CompletedTabHeader
		{
			get
			{
				return string.Format(MainRes.CompletedWithTotal, this.CompletedJobs.Count);
			}
		}

		public bool CanTryEnqueueMultipleTitles
		{
			get
			{
				return this.main.HasVideoSource && this.main.SourceData.Titles.Count > 1;
			}
		}

		public bool EncodeSpeedDetailsAvailable
		{
			get
			{
				return this.encodeSpeedDetailsAvailable;
			}

			set
			{
				this.encodeSpeedDetailsAvailable = value;
				this.RaisePropertyChanged(() => this.EncodeSpeedDetailsAvailable);
			}
		}

		public string EstimatedTimeRemaining
		{
			get
			{
				return this.estimatedTimeRemaining;
			}

			set
			{
				this.estimatedTimeRemaining = value;
				this.RaisePropertyChanged(() => this.EstimatedTimeRemaining);
			}
		}

		public List<EncodeCompleteAction> EncodeCompleteActions
		{
			get
			{
				return this.encodeCompleteActions;
			}
		} 

		public EncodeCompleteAction EncodeCompleteAction
		{
			get
			{
				return this.encodeCompleteAction;
			}

			set
			{
				this.encodeCompleteAction = value;
				this.RaisePropertyChanged(() => this.EncodeCompleteAction);
			}
		}

		public double CurrentFps
		{
			get
			{
				return this.currentFps;
			}

			set
			{
				this.currentFps = value;
				this.RaisePropertyChanged(() => this.CurrentFps);
			}
		}

		public double AverageFps
		{
			get
			{
				return this.averageFps;
			}

			set
			{
				this.averageFps = value;
				this.RaisePropertyChanged(() => this.AverageFps);
			}
		}

		public double OverallEncodeProgressFraction
		{
			get
			{
				return this.overallEncodeProgressFraction;
			}

			set
			{
				this.overallEncodeProgressFraction = value;
				this.RaisePropertyChanged(() => this.OverallEncodeProgressPercent);
				this.RaisePropertyChanged(() => this.OverallEncodeProgressFraction);
			}
		}

		public double OverallEncodeProgressPercent
		{
			get
			{
				return this.overallEncodeProgressFraction * 100;
			}
		}

		public TaskbarItemProgressState EncodeProgressState
		{
			get
			{
				return this.encodeProgressState;
			}

			set
			{
				this.encodeProgressState = value;
				this.RaisePropertyChanged(() => this.EncodeProgressState);
			}
		}

		public Brush ProgressBarColor
		{
			get
			{
				if (this.Paused)
				{
					return new SolidColorBrush(Color.FromRgb(255, 230, 0));
				}
				else
				{
					return new SolidColorBrush(Color.FromRgb(0, 200, 0));
				}
			}
		}

		private EncodeProgress encodeProgress;
		public EncodeProgress EncodeProgress
		{
			get { return this.encodeProgress; }
			set { this.Set(ref this.encodeProgress, value); }
		}

		public int SelectedTabIndex
		{
			get
			{
				return this.selectedTabIndex;
			}

			set
			{
				this.selectedTabIndex = value;
				this.RaisePropertyChanged(() => this.SelectedTabIndex);
			}
		}

		private RelayCommand encodeCommand;
		public RelayCommand EncodeCommand
		{
			get
			{
				return this.encodeCommand ?? (this.encodeCommand = new RelayCommand(() =>
					{
						if (this.Encoding)
						{
							this.ResumeEncoding();
							this.autoPause.ReportResume();
						}
						else
						{
							if (this.EncodeQueue.Count == 0)
							{
								if (!this.TryQueue())
								{
									return;
								}
							}
							else if (profileEditedSinceLastQueue)
							{
								// If the encoding profile has changed since the last time we queued an item, we'll prompt to apply the current
								// encoding profile to all queued items.

								var messageBoxService = Ioc.Get<IMessageBoxService>();
								MessageBoxResult result = messageBoxService.Show(
									this.main,
									MainRes.EncodingSettingsChangedMessage,
									MainRes.EncodingSettingsChangedTitle,
									MessageBoxButton.YesNo);

								if (result == MessageBoxResult.Yes)
								{
									var newJobs = new List<EncodeJobViewModel>();

									foreach (EncodeJobViewModel job in this.EncodeQueue)
									{
										VCProfile newProfile = this.presetsService.SelectedPreset.Preset.EncodingProfile;
										job.Job.EncodingProfile = newProfile;
										job.Job.OutputPath = Path.ChangeExtension(job.Job.OutputPath, OutputPathService.GetExtensionForProfile(newProfile));

										newJobs.Add(job);
									}

									// Clear out the queue and re-add the updated jobs so all the changes get reflected.
									this.EncodeQueue.Clear();
									foreach (var job in newJobs)
									{
										this.EncodeQueue.Add(job);
									}
								}
							}

							this.SelectedTabIndex = QueuedTabIndex;

							this.StartEncodeQueue();
						}
					},
					() =>
					{
						return this.EncodeQueue.Count > 0 || this.CanTryEnqueue;
					}));
			}
		}

		private RelayCommand addToQueueCommand;
		public RelayCommand AddToQueueCommand
		{
			get
			{
				return this.addToQueueCommand ?? (this.addToQueueCommand = new RelayCommand(() =>
					{
						this.TryQueue();
					},
					() =>
					{
						return this.CanTryEnqueue;
					}));
			}
		}

		private RelayCommand queueFilesCommand;
		public RelayCommand QueueFilesCommand
		{
			get
			{
				return this.queueFilesCommand ?? (this.queueFilesCommand = new RelayCommand(() =>
					{
						if (!this.EnsureDefaultOutputFolderSet())
						{
							return;
						}

						IList<string> fileNames = FileService.Instance.GetFileNames(Config.RememberPreviousFiles ? Config.LastInputFileFolder : null);
						if (fileNames != null && fileNames.Count > 0)
						{
							Config.LastInputFileFolder = Path.GetDirectoryName(fileNames[0]);

							this.QueueMultiple(fileNames);
						}
					}));
			}
		}

		private RelayCommand queueTitlesCommand;
		public RelayCommand QueueTitlesCommand
		{
			get
			{
				return this.queueTitlesCommand ?? (this.queueTitlesCommand = new RelayCommand(() =>
					{
						if (!this.EnsureDefaultOutputFolderSet())
						{
							return;
						}

						this.windowManager.OpenOrFocusWindow(typeof(QueueTitlesWindowViewModel));
					},
					() =>
					{
						return this.CanTryEnqueueMultipleTitles;
					}));
			}
		}

		private RelayCommand pauseCommand;
		public RelayCommand PauseCommand
		{
			get
			{
				return this.pauseCommand ?? (this.pauseCommand = new RelayCommand(() =>
					{
						this.PauseEncoding();
						this.autoPause.ReportPause();
					},
					() =>
					{
						return this.Encoding && this.encodeProxy != null &&  this.encodeProxy.IsEncodeStarted;
					}));
			}
		}

		private RelayCommand stopEncodeCommand;
		public RelayCommand StopEncodeCommand
		{
			get
			{
				return this.stopEncodeCommand ?? (this.stopEncodeCommand = new RelayCommand(() =>
					{
						if (this.CurrentJob.EncodeTime > TimeSpan.FromMinutes(StopWarningThresholdMinutes))
						{
							MessageBoxResult dialogResult = Utilities.MessageBox.Show(
								MainRes.StopEncodeConfirmationMessage,
								MainRes.StopEncodeConfirmationTitle,
								MessageBoxButton.YesNo);
							if (dialogResult == MessageBoxResult.No)
							{
								return;
							}
						}

						// Signify that we stopped the encode manually rather than it completing.
						this.encodeStopped = true;
						this.encodeProxy.StopEncode();

						this.logger.ShowStatus(MainRes.StoppedEncoding);
					},
					() =>
					{
						return this.Encoding && this.encodeProxy != null && this.encodeProxy.IsEncodeStarted;
					}));
			}
		}

		private RelayCommand moveSelectedJobsToTopCommand;
		public RelayCommand MoveSelectedJobsToTopCommand
		{
			get
			{
				return this.moveSelectedJobsToTopCommand ?? (this.moveSelectedJobsToTopCommand = new RelayCommand(() =>
					{
						List<EncodeJobViewModel> jobsToMove = this.EncodeQueue.Where(j => j.IsSelected && !j.Encoding).ToList();
						if (jobsToMove.Count > 0)
						{
							foreach (EncodeJobViewModel jobToMove in jobsToMove)
							{
								this.EncodeQueue.Remove(jobToMove);
							}

							int insertPosition = this.Encoding ? 1 : 0;

							for (int i = jobsToMove.Count - 1; i >= 0; i--)
							{
								this.EncodeQueue.Insert(insertPosition, jobsToMove[i]);
							}
						}
					}));
			}
		}

		private RelayCommand moveSelectedJobsToBottomCommand;
		public RelayCommand MoveSelectedJobsToBottomCommand
		{
			get
			{
				return this.moveSelectedJobsToBottomCommand ?? (this.moveSelectedJobsToBottomCommand = new RelayCommand(() =>
					{
						List<EncodeJobViewModel> jobsToMove = this.EncodeQueue.Where(j => j.IsSelected && !j.Encoding).ToList();
						if (jobsToMove.Count > 0)
						{
							foreach (EncodeJobViewModel jobToMove in jobsToMove)
							{
								this.EncodeQueue.Remove(jobToMove);
							}

							foreach (EncodeJobViewModel jobToMove in jobsToMove)
							{
								this.EncodeQueue.Add(jobToMove);
							}
						}
					}));
			}
		}

		private RelayCommand importQueueCommand;
		public RelayCommand ImportQueueCommand
		{
			get
			{
				return this.importQueueCommand ?? (this.importQueueCommand = new RelayCommand(() =>
				{
					string presetFileName = FileService.Instance.GetFileNameLoad(
						null,
						MainRes.ImportQueueFilePickerTitle,
						CommonRes.QueueFileFilter + "|*.xml;*.vjqueue");
					if (presetFileName != null)
					{
						try
						{
							Ioc.Get<IQueueImportExport>().Import(presetFileName);
							this.messageBoxService.Show(MainRes.QueueImportSuccessMessage, CommonRes.Success, System.Windows.MessageBoxButton.OK);
						}
						catch (Exception)
						{
							this.messageBoxService.Show(MainRes.QueueImportErrorMessage, MainRes.ImportErrorTitle, System.Windows.MessageBoxButton.OK);
						}
					}
				}));
			}
		}

		private RelayCommand exportQueueCommand;
		public RelayCommand ExportQueueCommand
		{
			get
			{
				return this.exportQueueCommand ?? (this.exportQueueCommand = new RelayCommand(() =>
					{
						var encodeJobs = new List<EncodeJobWithMetadata>();
						foreach (EncodeJobViewModel jobVM in this.EncodeQueue)
						{
							encodeJobs.Add(
								new EncodeJobWithMetadata
								{
									Job = jobVM.Job,
									SourceParentFolder = jobVM.SourceParentFolder,
									ManualOutputPath = jobVM.ManualOutputPath,
									NameFormatOverride = jobVM.NameFormatOverride,
									PresetName = jobVM.PresetName
								});
						}

						Ioc.Get<IQueueImportExport>().Export(encodeJobs);
				}));
			}
		}

		private RelayCommand removeSelectedJobsCommand;
		public RelayCommand RemoveSelectedJobsCommand
		{
			get
			{
				return this.removeSelectedJobsCommand ?? (this.removeSelectedJobsCommand = new RelayCommand(() =>
					{
						this.RemoveSelectedQueueJobs();
					}));
			}
		}

		private RelayCommand clearCompletedCommand;
		public RelayCommand ClearCompletedCommand
		{
			get
			{
				return this.clearCompletedCommand ?? (this.clearCompletedCommand = new RelayCommand(() =>
					{
						var removedItems = new List<EncodeResultViewModel>(this.CompletedJobs);
						this.CompletedJobs.Clear();
						var deletionCandidates = new List<string>();

						foreach (var removedItem in removedItems)
						{
							// Delete file if setting is enabled and item succeeded
							if (Config.DeleteSourceFilesOnClearingCompleted && removedItem.EncodeResult.Succeeded)
							{
								// And if file exists and is not read-only
								string sourcePath = removedItem.Job.Job.SourcePath;
								var fileInfo = new FileInfo(sourcePath);
								var directoryInfo = new DirectoryInfo(sourcePath);

								if (fileInfo.Exists && !fileInfo.IsReadOnly || directoryInfo.Exists && !directoryInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
								{
									// And if it's not currently scanned or in the encode queue
									bool sourceInEncodeQueue = this.EncodeQueue.Any(job => string.Compare(job.Job.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) == 0);
									if (!sourceInEncodeQueue &&
									    (!this.main.HasVideoSource || string.Compare(this.main.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) != 0))
									{
										deletionCandidates.Add(sourcePath);
									}
								}
							}
						}

						if (deletionCandidates.Count > 0)
						{
							MessageBoxResult dialogResult = Utilities.MessageBox.Show(
								string.Format(MainRes.DeleteSourceFilesConfirmationMessage, deletionCandidates.Count), 
								MainRes.DeleteSourceFilesConfirmationTitle, 
								MessageBoxButton.YesNo);
							if (dialogResult == MessageBoxResult.Yes)
							{
								foreach (string pathToDelete in deletionCandidates)
								{
									try
									{
										if (File.Exists(pathToDelete))
										{
											File.Delete(pathToDelete);
										}
										else if (Directory.Exists(pathToDelete))
										{
											FileUtilities.DeleteDirectory(pathToDelete);
										}
									}
									catch (IOException exception)
									{
										Utilities.MessageBox.Show(string.Format(MainRes.CouldNotDeleteFile, pathToDelete, exception));
									}
								}
							}
						}

						this.RaisePropertyChanged(() => this.CompletedItemsCount);
						this.RaisePropertyChanged(() => this.CompletedTabHeader);
					}));
			}
		}

		/// <summary>
		/// Adds the given source to the encode queue.
		/// </summary>
		/// <param name="source">The path to the source file to encode.</param>
		/// <param name="destination">The destination path for the encoded file.</param>
		/// <param name="presetName">The name of the preset to use to encode.</param>
		/// <returns>True if the item was successfully queued for processing.</returns>
		public void Process(string source, string destination, string presetName, string pickerName)
		{
			if (string.IsNullOrWhiteSpace(source))
			{
				throw new ArgumentException("source cannot be null or empty.");
			}

			if (string.IsNullOrWhiteSpace(destination) && !this.EnsureDefaultOutputFolderSet())
			{
				throw new ArgumentException("If destination is not set, the default output folder must be set.");
			}

			if (destination != null && !Utilities.IsValidFullPath(destination))
			{
				throw new ArgumentException("Destination path is not valid: " + destination);
			}

			VCProfile profile = this.presetsService.GetProfileByName(presetName);
			if (profile == null)
			{
				throw new ArgumentException("Cannot find preset: " + presetName);
			}

			PickerViewModel pickerVM = this.pickersService.Pickers.FirstOrDefault(p => p.Picker.Name == pickerName);
			Picker picker = null;
			if (pickerVM != null)
			{
				picker = pickerVM.Picker;
			}
			

			var scanMultipleDialog = new ScanMultipleDialogViewModel(new List<string> { source });
			this.windowManager.OpenDialog(scanMultipleDialog);

			VideoSource videoSource = scanMultipleDialog.ScanResults[0];
			List<int> titleNumbers = this.PickTitles(videoSource, picker);

			foreach (int titleNumber in titleNumbers)
			{
				var jobVM = new EncodeJobViewModel(new VCJob
				{
					SourcePath = source,
					SourceType = Utilities.GetSourceType(source),
					Title = titleNumber,
					RangeType = VideoRangeType.All,
					EncodingProfile = profile,
					ChosenAudioTracks = new List<int> { 1 },
					OutputPath = destination,
					UseDefaultChapterNames = true,
				});

				jobVM.VideoSource = videoSource;
				jobVM.PresetName = presetName;
				jobVM.ManualOutputPath = !string.IsNullOrWhiteSpace(destination);

				VCJob job = jobVM.Job;

				SourceTitle title = jobVM.VideoSource.Titles.Single(t => t.Index == titleNumber);
				jobVM.Job.Length = title.Duration.ToSpan();

				// Choose the correct audio/subtitle tracks based on settings
				this.AutoPickAudio(job, title);
				this.AutoPickSubtitles(job, title);

				// Now that we have the title and subtitles we can determine the final output file name
				if (string.IsNullOrWhiteSpace(destination))
				{
					// Exclude all current queued files if overwrite is disabled
					HashSet<string> excludedPaths;
					if (CustomConfig.WhenFileExistsBatch == WhenFileExists.AutoRename)
					{
						excludedPaths = this.GetQueuedFiles();
					}
					else
					{
						excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					}

					string pathToQueue = job.SourcePath;

					excludedPaths.Add(pathToQueue);
					string outputFolder = this.outputVM.GetOutputFolder(pathToQueue, null, picker);
					string outputFileName = this.outputVM.BuildOutputFileName(
						pathToQueue,
						Utilities.GetSourceName(pathToQueue),
						job.Title, 
						title.Duration.ToSpan(), 
						title.ChapterList.Count,
						multipleTitlesOnSource: videoSource.Titles.Count > 1,
						picker: picker);
					string outputExtension = this.outputVM.GetOutputExtension();
					string queueOutputPath = Path.Combine(outputFolder, outputFileName + outputExtension);
					queueOutputPath = this.outputVM.ResolveOutputPathConflicts(queueOutputPath, excludedPaths, isBatch: true);

					job.OutputPath = queueOutputPath;
				}

				this.Queue(jobVM);
			}

			this.logger.Log("Queued " + titleNumbers.Count + " titles from " + source);

			if (titleNumbers.Count > 0 && !this.Encoding)
			{
				this.StartEncodeQueue();
			}
		}

		public bool TryQueue()
		{
			if (!this.EnsureDefaultOutputFolderSet())
			{
				return false;
			}

			if (!this.EnsureValidOutputPath())
			{
				return false;
			}

			var newEncodeJobVM = this.main.CreateEncodeJobVM();

			string resolvedOutputPath = this.outputVM.ResolveOutputPathConflicts(newEncodeJobVM.Job.OutputPath, isBatch: false);
			if (resolvedOutputPath == null)
			{
				return false;
			}

			newEncodeJobVM.Job.OutputPath = resolvedOutputPath;

			this.Queue(newEncodeJobVM);
			return true;
		}

		/// <summary>
		/// Queues the given Job. Assumed that the job has an associated HandBrake instance and populated Length.
		/// </summary>
		/// <param name="encodeJobVM">The job to add.</param>
		public void Queue(EncodeJobViewModel encodeJobVM)
		{
			if (this.Encoding)
			{
				if (this.totalTasks == 1)
				{
					this.EncodeQueue[0].IsOnlyItem = false;
				}

				this.totalTasks++;
				this.totalQueueCost += encodeJobVM.Cost;
			}

			Picker picker = this.pickersService.SelectedPicker.Picker;
			if (picker.UseEncodingPreset && !string.IsNullOrEmpty(picker.EncodingPreset))
			{
				// Override the encoding preset
				var presetViewModel = this.presetsService.AllPresets.FirstOrDefault(p => p.Preset.Name == picker.EncodingPreset);
				if (presetViewModel != null)
				{
					encodeJobVM.Job.EncodingProfile = presetViewModel.Preset.EncodingProfile.Clone();
					encodeJobVM.PresetName = picker.EncodingPreset;
				}
			}

			this.EncodeQueue.Add(encodeJobVM);

			this.profileEditedSinceLastQueue = false;

			this.RaisePropertyChanged(() => this.QueuedTabHeader);

			// Select the Queued tab.
			if (this.SelectedTabIndex != QueuedTabIndex)
			{
				this.SelectedTabIndex = QueuedTabIndex;
			}
		}

		public void QueueTitles(List<SourceTitle> titles, int titleStartOverride, string nameFormatOverride)
		{
			int currentTitleNumber = titleStartOverride;

			Picker picker = this.pickersService.SelectedPicker.Picker;

			// Queue the selected titles
			List<SourceTitle> titlesToQueue = titles;
			foreach (SourceTitle title in titlesToQueue)
			{
				VCProfile profile = this.presetsService.SelectedPreset.Preset.EncodingProfile;
				string queueSourceName = this.main.SourceName;
				if (this.main.SelectedSource.Type == SourceType.Dvd)
				{
					queueSourceName = this.outputVM.TranslateDvdSourceName(queueSourceName);
				}

				int titleNumber = title.Index;
				if (titleStartOverride >= 0)
				{
					titleNumber = currentTitleNumber;
					currentTitleNumber++;
				}

				string outputDirectoryOverride = null;
				if (picker.OutputDirectoryOverrideEnabled)
				{
					outputDirectoryOverride = picker.OutputDirectoryOverride;
				}

				var job = new VCJob
				{
					SourceType = this.main.SelectedSource.Type,
					SourcePath = this.main.SourcePath,
					EncodingProfile = profile.Clone(),
					Title = title.Index,
					ChapterStart = 1,
					ChapterEnd = title.ChapterList.Count,
					UseDefaultChapterNames = true,
					Length = title.Duration.ToSpan()
				};

				this.AutoPickAudio(job, title, useCurrentContext: true);
				this.AutoPickSubtitles(job, title, useCurrentContext: true);

				string queueOutputFileName = this.outputVM.BuildOutputFileName(
					this.main.SourcePath,
					queueSourceName,
					titleNumber,
					title.Duration.ToSpan(),
					title.ChapterList.Count,
					nameFormatOverride,
					multipleTitlesOnSource: true);

				string extension = this.outputVM.GetOutputExtension();
				string queueOutputPath = this.outputVM.BuildOutputPath(queueOutputFileName, extension, sourcePath: null, outputFolder: outputDirectoryOverride);

				job.OutputPath = this.outputVM.ResolveOutputPathConflicts(queueOutputPath, isBatch: true);

				var jobVM = new EncodeJobViewModel(job)
				{
					VideoSource = this.main.SourceData,
					VideoSourceMetadata = this.main.GetVideoSourceMetadata(),
					ManualOutputPath = false,
					NameFormatOverride = nameFormatOverride,
					PresetName = this.presetsService.SelectedPreset.DisplayName
				};

				this.Queue(jobVM);
			}
		}

		public void QueueMultiple(IEnumerable<string> pathsToQueue)
		{
			this.QueueMultiple(pathsToQueue.Select(p => new SourcePath { Path = p }));
		}

		// Queues a list of files or video folders.
		public void QueueMultiple(IEnumerable<SourcePath> sourcePaths)
		{
			if (!this.EnsureDefaultOutputFolderSet())
			{
				return;
			}

			// Exclude all current queued files if overwrite is disabled
			HashSet<string> excludedPaths;
			if (CustomConfig.WhenFileExistsBatch == WhenFileExists.AutoRename)
			{
				excludedPaths = this.GetQueuedFiles();
			}
			else
			{
				excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			}

			List<SourcePath> sourcePathList = sourcePaths.ToList();
			List<string> sourcePathStrings = sourcePathList.Select(p => p.Path).ToList();

			// This dialog will scan the items in the list, calculating length.
			var scanMultipleDialog = new ScanMultipleDialogViewModel(sourcePathStrings);
			this.windowManager.OpenDialog(scanMultipleDialog);

			List<VideoSource> videoSources = scanMultipleDialog.ScanResults;

			if (sourcePathList.Count != videoSources.Count)
			{
				// Scan dialog was closed before it could complete. Abort.
				this.logger.Log("Batch scan cancelled. Aborting queue operation.");
				return;
			}

			var itemsToQueue = new List<EncodeJobViewModel>();

			for (int i = 0; i < sourcePathList.Count; i++)
			{
				SourcePath sourcePath = sourcePathList[i];
				VideoSource videoSource = videoSources[i];

				List<int> titleNumbers = this.PickTitles(videoSource);

				foreach (int titleNumber in titleNumbers)
				{
					var job = new VCJob
					{
						SourcePath = sourcePath.Path,
						EncodingProfile = this.presetsService.SelectedPreset.Preset.EncodingProfile.Clone(),
						Title = titleNumber,
						RangeType = VideoRangeType.All,
						UseDefaultChapterNames = true
					};

					if (sourcePath.SourceType == SourceType.None)
					{
						if (Directory.Exists(sourcePath.Path))
						{
							job.SourceType = SourceType.VideoFolder;
						}
						else if (File.Exists(sourcePath.Path))
						{
							job.SourceType = SourceType.File;
						}
					}
					else
					{
						job.SourceType = sourcePath.SourceType;
					}

					if (job.SourceType != SourceType.None)
					{
						var jobVM = new EncodeJobViewModel(job);
						jobVM.VideoSource = videoSource;
						jobVM.SourceParentFolder = sourcePath.ParentFolder;
						jobVM.ManualOutputPath = false;
						jobVM.PresetName = this.presetsService.SelectedPreset.DisplayName;
						itemsToQueue.Add(jobVM);
					}
				}
			}

			var failedFiles = new List<string>();
			foreach (EncodeJobViewModel jobVM in itemsToQueue)
			{
				var titles = jobVM.VideoSource.Titles;

				// Only queue items with a successful scan
				if (titles.Count > 0)
				{
					VCJob job = jobVM.Job;
					SourceTitle title = titles.Single(t => t.Index == job.Title);
					job.Length = title.Duration.ToSpan();

					// Choose the correct audio/subtitle tracks based on settings
					this.AutoPickAudio(job, title);
					this.AutoPickSubtitles(job, title);

					// Now that we have the title and subtitles we can determine the final output file name
					string fileToQueue = job.SourcePath;

					excludedPaths.Add(fileToQueue);
					string outputFolder = this.outputVM.GetOutputFolder(fileToQueue, jobVM.SourceParentFolder);
					string outputFileName = this.outputVM.BuildOutputFileName(
						fileToQueue, 
						Utilities.GetSourceNameFile(fileToQueue),
						job.Title, 
						title.Duration.ToSpan(),
						title.ChapterList.Count,
						multipleTitlesOnSource: titles.Count > 1);
					string outputExtension = this.outputVM.GetOutputExtension();
					string queueOutputPath = Path.Combine(outputFolder, outputFileName + outputExtension);
					queueOutputPath = this.outputVM.ResolveOutputPathConflicts(queueOutputPath, excludedPaths, isBatch: true);

					job.OutputPath = queueOutputPath;

					excludedPaths.Add(queueOutputPath);

					this.Queue(jobVM);
				}
				else
				{
					failedFiles.Add(jobVM.Job.SourcePath);
				}
			}

			if (failedFiles.Count > 0)
			{
				Utilities.MessageBox.Show(
					string.Format(MainRes.QueueMultipleScanErrorMessage, string.Join(Environment.NewLine, failedFiles)),
					MainRes.QueueMultipleScanErrorTitle,
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
		}

		public void RemoveQueueJob(EncodeJobViewModel job)
		{
			this.EncodeQueue.Remove(job);

			if (this.Encoding)
			{
				this.totalTasks--;
				this.totalQueueCost -= job.Cost;

				if (this.totalTasks == 1)
				{
					this.EncodeQueue[0].IsOnlyItem = true;
				}
			}

			this.RaisePropertyChanged(() => this.QueuedTabHeader);
		}

		public void RemoveSelectedQueueJobs()
		{
			for (int i = this.EncodeQueue.Count - 1; i >= 0; i--)
			{
				EncodeJobViewModel jobVM = this.EncodeQueue[i];

				if (jobVM.IsSelected && !jobVM.Encoding)
				{
					this.EncodeQueue.RemoveAt(i);
				}
			}
		}

		public void StartEncodeQueue()
		{
			this.EncodeProgressState = TaskbarItemProgressState.Normal;
			this.logger.Log("Starting queue");
			this.logger.ShowStatus(MainRes.StartedEncoding);

			this.totalTasks = this.EncodeQueue.Count;
			this.taskNumber = 0;

			this.completedQueueWork = 0.0;
			this.totalQueueCost = 0.0;
			foreach (EncodeJobViewModel jobVM in this.EncodeQueue)
			{
				this.totalQueueCost += jobVM.Cost;
			}

			this.OverallEncodeProgressFraction = 0;

			this.pollCount = 0;
			this.Encoding = true;
			this.Paused = false;
			this.encodeStopped = false;
			this.autoPause.ReportStart();

			this.EncodeNextJob();

			// User had the window open when the encode ended last time, so we re-open when starting the queue again.
			if (Config.EncodeDetailsWindowOpen)
			{
				this.windowManager.OpenOrFocusWindow(typeof(EncodeDetailsWindowViewModel));
			}

			this.EncodeProgress = new EncodeProgress
			{
				Encoding = true,
				OverallProgressFraction = 0,
				TaskNumber = 1,
				TotalTasks = this.totalTasks,
				FileName = Path.GetFileName(this.CurrentJob.Job.OutputPath)
			};
		}

		public HashSet<string> GetQueuedFiles()
		{
			return new HashSet<string>(this.EncodeQueue.Select(j => j.Job.OutputPath), StringComparer.OrdinalIgnoreCase);
		}

		public IList<EncodeJobWithMetadata> GetQueueStorageJobs()
		{
			var jobs = new List<EncodeJobWithMetadata>();
			foreach (EncodeJobViewModel jobVM in this.EncodeQueue)
			{
				jobs.Add(
					new EncodeJobWithMetadata
					{
						Job = jobVM.Job,
						SourceParentFolder = jobVM.SourceParentFolder,
						ManualOutputPath = jobVM.ManualOutputPath,
						NameFormatOverride = jobVM.NameFormatOverride,
						PresetName = jobVM.PresetName
					});
			}

			return jobs;
		}

		private void EncodeNextJob()
		{
			this.taskNumber++;
			this.StartEncode();
		}

		private void StartEncode()
		{
			VCJob job = this.CurrentJob.Job;

			var encodeLogger = new Logger(this.logger, Path.GetFileName(job.OutputPath));
			this.CurrentJob.Logger = encodeLogger;

			encodeLogger.Log("Starting job " + this.taskNumber + "/" + this.totalTasks);
			encodeLogger.Log("  Path: " + job.SourcePath);
			encodeLogger.Log("  Title: " + job.Title);

			switch (job.RangeType)
			{
				case VideoRangeType.All:
					encodeLogger.Log("  Range: All");
					break;
				case VideoRangeType.Chapters:
					encodeLogger.Log("  Chapters: " + job.ChapterStart + "-" + job.ChapterEnd);
					break;
				case VideoRangeType.Seconds:
					encodeLogger.Log("  Seconds: " + job.SecondsStart + "-" + job.SecondsEnd);
					break;
				case VideoRangeType.Frames:
					encodeLogger.Log("  Frames: " + job.FramesStart + "-" + job.FramesEnd);
					break;
			}

			this.encodeProxy = Utilities.CreateEncodeProxy();
			this.encodeProxy.EncodeProgress += this.OnEncodeProgress;
			this.encodeProxy.EncodeCompleted += this.OnEncodeCompleted;
			this.encodeProxy.EncodeStarted += this.OnEncodeStarted;

			string destinationDirectory = Path.GetDirectoryName(this.CurrentJob.Job.OutputPath);
			if (!Directory.Exists(destinationDirectory))
			{
				try
				{
					Directory.CreateDirectory(destinationDirectory);
				}
				catch (IOException exception)
				{
					Utilities.MessageBox.Show(
						string.Format(MainRes.DirectoryCreateErrorMessage, exception),
						MainRes.DirectoryCreateErrorTitle,
						MessageBoxButton.OK,
						MessageBoxImage.Error);
				}
			}

			this.currentJobEta = TimeSpan.Zero;
			this.EncodeQueue[0].ReportEncodeStart(this.totalTasks == 1);
			this.encodeProxy.StartEncode(this.CurrentJob.Job, encodeLogger, false, 0, 0, 0);

			this.StopEncodeCommand.RaiseCanExecuteChanged();
			this.PauseCommand.RaiseCanExecuteChanged();
		}

		private void OnEncodeStarted(object sender, EventArgs e)
		{
			DispatchUtilities.BeginInvoke(() =>
			    {
					// After the encode has reported that it's started, we can now pause/stop it.
					this.StopEncodeCommand.RaiseCanExecuteChanged();
					this.PauseCommand.RaiseCanExecuteChanged();
			    });
		}

		private void OnEncodeProgress(object sender, EncodeProgressEventArgs e)
		{
			if (this.EncodeQueue.Count == 0)
			{
				return;
			}

			VCJob currentJob = this.EncodeQueue[0].Job;
			double passCost = currentJob.Length.TotalSeconds;
			double scanPassCost = passCost / EncodeJobViewModel.SubtitleScanCostFactor;
			double currentJobCompletedWork = 0.0;

			Debug.WriteLine("Pass id in encode progress: " + e.PassId);
			if (this.EncodeQueue[0].SubtitleScan)
			{
				switch (e.PassId)
				{
					case -1:
						currentJobCompletedWork += scanPassCost * e.FractionComplete;
						break;
					case 0:
					case 1:
						currentJobCompletedWork += scanPassCost;
						currentJobCompletedWork += passCost * e.FractionComplete;
						break;
					case 2:
						currentJobCompletedWork += scanPassCost;
						currentJobCompletedWork += passCost;
						currentJobCompletedWork += passCost * e.FractionComplete;
						break;
					default:
						break;
				}
			}
			else
			{
				switch (e.PassId)
				{
					case 0:
					case 1:
						currentJobCompletedWork += passCost * e.FractionComplete;
						break;
					case 2:
						currentJobCompletedWork += passCost;
						currentJobCompletedWork += passCost * e.FractionComplete;
						break;
					default:
						break;
				}
			}

			double totalCompletedWork = this.completedQueueWork + currentJobCompletedWork;

			this.OverallEncodeProgressFraction = this.totalQueueCost > 0 ? totalCompletedWork / this.totalQueueCost : 0;

			double queueElapsedSeconds = this.elapsedQueueEncodeTime.Elapsed.TotalSeconds;
			double overallWorkCompletionRate = queueElapsedSeconds > 0 ? totalCompletedWork / queueElapsedSeconds : 0;

			// Only update encode time every 5th update.
			if (Interlocked.Increment(ref this.pollCount) % 5 == 1)
			{
				if (this.elapsedQueueEncodeTime != null && queueElapsedSeconds > 0.5 && this.OverallEncodeProgressFraction != 0.0)
				{
					if (this.OverallEncodeProgressFraction == 1.0)
					{
						this.EstimatedTimeRemaining = Utilities.FormatTimeSpan(TimeSpan.Zero);
					}
					else
					{
						if (this.OverallEncodeProgressFraction == 0)
						{
							this.overallEtaSpan = TimeSpan.MaxValue;
						}
						else
						{
							try
							{
								this.overallEtaSpan =
									TimeSpan.FromSeconds((long)(((1.0 - this.OverallEncodeProgressFraction) * queueElapsedSeconds) / this.OverallEncodeProgressFraction));
							}
							catch (OverflowException)
							{
								this.overallEtaSpan = TimeSpan.MaxValue;
							}
						}

						this.EstimatedTimeRemaining = Utilities.FormatTimeSpan(this.overallEtaSpan);
					}

					double currentJobRemainingWork = this.EncodeQueue[0].Cost - currentJobCompletedWork;

					if (overallWorkCompletionRate == 0)
					{
						this.currentJobEta = TimeSpan.MaxValue;
					}
					else
					{
						try
						{
							this.currentJobEta = TimeSpan.FromSeconds(currentJobRemainingWork / overallWorkCompletionRate);
						}
						catch (OverflowException)
						{
							this.currentJobEta = TimeSpan.MaxValue;
						}
					}

					this.EncodeQueue[0].Eta = this.currentJobEta;
				}
			}

			double currentJobFractionComplete = currentJobCompletedWork / this.EncodeQueue[0].Cost;
			this.EncodeQueue[0].PercentComplete = (int)(currentJobFractionComplete * 100.0);

			if (e.EstimatedTimeLeft >= TimeSpan.Zero)
			{
				this.CurrentFps = Math.Round(e.CurrentFrameRate, 1);
				this.AverageFps = Math.Round(e.AverageFrameRate, 1);
				this.EncodeSpeedDetailsAvailable = true;
			}

			VCProfile currentProfile = currentJob.EncodingProfile;

			var newEncodeProgress = new EncodeProgress
			{
				Encoding = true,
				OverallProgressFraction = this.OverallEncodeProgressFraction,
				TaskNumber = this.taskNumber,
				TotalTasks = this.totalTasks,
				OverallElapsedTime = this.elapsedQueueEncodeTime.Elapsed,
				OverallEta = this.overallEtaSpan,
				FileName = Path.GetFileName(currentJob.OutputPath),
				FileProgressFraction = currentJobFractionComplete,
				FileElapsedTime = this.CurrentJob.EncodeTime,
				FileEta = this.currentJobEta,
				HasScanPass = this.CurrentJob.SubtitleScan,
				TwoPass = currentProfile.VideoEncodeRateType != VCVideoEncodeRateType.ConstantQuality && currentProfile.TwoPass,
				CurrentPassId = e.PassId,
				PassProgressFraction = e.FractionComplete,
				EncodeSpeedDetailsAvailable = this.EncodeSpeedDetailsAvailable,
				CurrentFps = this.CurrentFps,
				AverageFps = this.AverageFps
			};

			try
			{
				var outputFileInfo = new FileInfo(currentJob.OutputPath);
				newEncodeProgress.FileSizeBytes = outputFileInfo.Length;
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}

			this.EncodeProgress = newEncodeProgress;
		}

		private void OnEncodeCompleted(object sender, EncodeCompletedEventArgs e)
		{
			DispatchUtilities.BeginInvoke(() =>
			{
				ILogger encodeLogger = this.CurrentJob.Logger;
				string outputPath = this.CurrentJob.Job.OutputPath;

				if (this.encodeStopped)
				{
					// If the encode was stopped manually
					this.StopEncodingAndReport();
					this.CurrentJob.ReportEncodeEnd();

					if (this.totalTasks == 1)
					{
						this.EncodeQueue.Clear();
					}

					encodeLogger.Log("Encoding stopped");
				}
				else
				{
					// If the encode completed successfully
					this.completedQueueWork += this.CurrentJob.Cost;

					var outputFileInfo = new FileInfo(this.CurrentJob.Job.OutputPath);

					EncodeResultStatus status = EncodeResultStatus.Succeeded;
					if (e.Error)
					{
						status = EncodeResultStatus.Failed;
						encodeLogger.LogError("Encode failed.");
					}
					else if (!outputFileInfo.Exists)
					{
						status = EncodeResultStatus.Failed;
						encodeLogger.LogError("Encode failed. HandBrake reported no error but the expected output file was not found.");
					}
					else if (outputFileInfo.Length == 0)
					{
						status = EncodeResultStatus.Failed;
						encodeLogger.LogError("Encode failed. HandBrake reported no error but the output file was empty.");
					}

					EncodeJobViewModel finishedJob = this.CurrentJob;

					if (Config.PreserveModifyTimeFiles)
					{
						try
						{
							if (status != EncodeResultStatus.Failed && !FileUtilities.IsDirectory(finishedJob.Job.SourcePath))
							{
								FileInfo info = new FileInfo(finishedJob.Job.SourcePath);

								File.SetCreationTimeUtc(finishedJob.Job.OutputPath, info.CreationTimeUtc);
								File.SetLastWriteTimeUtc(finishedJob.Job.OutputPath, info.LastWriteTimeUtc);
							}
						}
						catch (IOException exception)
						{
							encodeLogger.LogError("Could not set create/modify dates on file: " + exception);
						}
						catch (UnauthorizedAccessException exception)
						{
							encodeLogger.LogError("Could not set create/modify dates on file: " + exception);
						} 
					}

					this.CompletedJobs.Add(new EncodeResultViewModel(
						new EncodeResult
						{
							Destination = this.CurrentJob.Job.OutputPath,
							Status = status,
							EncodeTime = this.CurrentJob.EncodeTime,
							LogPath = encodeLogger.LogPath
						},
						finishedJob));
					this.RaisePropertyChanged(() => this.CompletedItemsCount);
					this.RaisePropertyChanged(() => this.CompletedTabHeader);

					this.EncodeQueue.RemoveAt(0);
					this.RaisePropertyChanged(() => this.QueuedTabHeader);

					encodeLogger.Log("Job completed (Elapsed Time: " + Utilities.FormatTimeSpan(finishedJob.EncodeTime) + ")");

					if (this.EncodeQueue.Count == 0)
					{
						this.SelectedTabIndex = CompletedTabIndex;
						this.StopEncodingAndReport();

						this.logger.Log("Queue completed");
						this.logger.ShowStatus(MainRes.EncodeCompleted);
						this.logger.Log("");

						Ioc.Get<TrayService>().ShowBalloonMessage(MainRes.EncodeCompleteBalloonTitle, MainRes.EncodeCompleteBalloonMessage);

						EncodeCompleteActionType actionType = this.EncodeCompleteAction.ActionType;
						if (Config.PlaySoundOnCompletion &&
							actionType != EncodeCompleteActionType.Sleep && 
							actionType != EncodeCompleteActionType.LogOff &&
							actionType != EncodeCompleteActionType.Shutdown &&
							actionType != EncodeCompleteActionType.Hibernate)
						{
							string soundPath = null;
							if (Config.UseCustomCompletionSound)
							{
								if (File.Exists(Config.CustomCompletionSound))
								{
									soundPath = Config.CustomCompletionSound;
								}
								else
								{
									this.logger.LogError(string.Format("Cound not find custom completion sound \"{0}\" . Using default.", Config.CustomCompletionSound));
								}
							}

							if (soundPath == null)
							{
								soundPath = Path.Combine(Utilities.ProgramFolder, "Encode_Complete.wav");
							}

							var soundPlayer = new SoundPlayer(soundPath);

							try
							{
								soundPlayer.Play();
							}
							catch (InvalidOperationException)
							{
								this.logger.LogError(string.Format("Completion sound \"{0}\" was not a supported WAV file.", soundPath));
							}
						}

						switch (actionType)
						{
							case EncodeCompleteActionType.DoNothing:
								break;
							case EncodeCompleteActionType.EjectDisc:
								this.systemOperations.Eject(this.EncodeCompleteAction.DriveLetter);
								break;
							case EncodeCompleteActionType.Sleep:
							case EncodeCompleteActionType.LogOff:
							case EncodeCompleteActionType.Shutdown:
							case EncodeCompleteActionType.Hibernate:
								this.windowManager.OpenWindow(new ShutdownWarningWindowViewModel(actionType));
								break;
							default:
								throw new ArgumentOutOfRangeException();
						}
					}
					else
					{
						this.EncodeNextJob();
					}
				}

				if (this.encodeStopped || this.EncodeQueue.Count == 0)
				{
					this.windowManager.Close<EncodeDetailsWindowViewModel>(userInitiated: false);
				}

				string encodeLogPath = encodeLogger.LogPath;
				encodeLogger.Dispose();

				if (Config.CopyLogToOutputFolder)
				{
					string logCopyPath = Path.Combine(Path.GetDirectoryName(outputPath), Path.GetFileName(encodeLogPath));

					try
					{
						File.Copy(encodeLogPath, logCopyPath);
					}
					catch (IOException exception)
					{
						this.logger.LogError("Could not copy log file to output directory: " + exception);
					}
					catch (UnauthorizedAccessException exception)
					{
						this.logger.LogError("Could not copy log file to output directory: " + exception);
					}
				}
			});
		}

		private void PauseEncoding()
		{
			this.encodeProxy.PauseEncode();
			this.EncodeProgressState = TaskbarItemProgressState.Paused;
			this.CurrentJob.ReportEncodePause();

			this.Paused = true;
		}

		private void ResumeEncoding()
		{
			this.encodeProxy.ResumeEncode();
			this.EncodeProgressState = TaskbarItemProgressState.Normal;
			this.CurrentJob.ReportEncodeResume();

			this.Paused = false;
		}

		private void StopEncodingAndReport()
		{
			this.EncodeProgressState = TaskbarItemProgressState.None;
			this.Encoding = false;
			this.autoPause.ReportStop();
		}

		private void SaveEncodeQueue()
		{
			EncodeJobStorage.EncodeJobs = this.GetQueueStorageJobs();
		}

		private void RefreshCanEnqueue()
		{
			this.RaisePropertyChanged(() => this.CanTryEnqueueMultipleTitles);
			this.RaisePropertyChanged(() => this.CanTryEnqueue);

			this.AddToQueueCommand.RaiseCanExecuteChanged();
			this.QueueTitlesCommand.RaiseCanExecuteChanged();
		}

		private void RefreshEncodeCompleteActions()
		{
			if (this.EncodeQueue == null || this.CompletedJobs == null)
			{
				return;
			}

			EncodeCompleteAction oldCompleteAction = this.EncodeCompleteAction;

			this.encodeCompleteActions =
				new List<EncodeCompleteAction>
				{
					new EncodeCompleteAction { ActionType = EncodeCompleteActionType.DoNothing },
					new EncodeCompleteAction { ActionType = EncodeCompleteActionType.Sleep },
					new EncodeCompleteAction { ActionType = EncodeCompleteActionType.LogOff },
					new EncodeCompleteAction { ActionType = EncodeCompleteActionType.Hibernate },
					new EncodeCompleteAction { ActionType = EncodeCompleteActionType.Shutdown },
				};

			// Applicable drives to eject are those in the queue or completed items list
			var applicableDrives = new HashSet<string>();
			foreach (EncodeJobViewModel job in this.EncodeQueue)
			{
				if (job.Job.SourceType == SourceType.Dvd)
				{
					string driveLetter = job.Job.SourcePath.Substring(0, 1).ToUpperInvariant();
					if (!applicableDrives.Contains(driveLetter))
					{
						applicableDrives.Add(driveLetter);
					}
				}
			}

			foreach (EncodeResultViewModel result in this.CompletedJobs)
			{
				if (result.Job.Job.SourceType == SourceType.Dvd)
				{
					string driveLetter = result.Job.Job.SourcePath.Substring(0, 1).ToUpperInvariant();
					if (!applicableDrives.Contains(driveLetter))
					{
						applicableDrives.Add(driveLetter);
					}
				}
			}

			// Order backwards so repeated insertions put them in correct order
			var orderedDrives =
				from d in applicableDrives
				orderby d descending 
				select d;

			foreach (string drive in orderedDrives)
			{
				this.encodeCompleteActions.Insert(1, new EncodeCompleteAction { ActionType = EncodeCompleteActionType.EjectDisc, DriveLetter = drive });
			}

			this.RaisePropertyChanged(() => this.EncodeCompleteActions);

			// Transfer over the previously selected item
			this.encodeCompleteAction = this.encodeCompleteActions[0];
			for (int i = 1; i < this.encodeCompleteActions.Count; i++)
			{
				if (this.encodeCompleteActions[i].Equals(oldCompleteAction))
				{
					this.encodeCompleteAction = this.encodeCompleteActions[i];
					break;
				}
			}

			this.RaisePropertyChanged(() => this.EncodeCompleteAction);
		}

		private bool EnsureDefaultOutputFolderSet()
		{
			if (!string.IsNullOrEmpty(Config.AutoNameOutputFolder))
			{
				return true;
			}

			var messageService = Ioc.Get<IMessageBoxService>();
			var messageResult = messageService.Show(
				this.main,
				MainRes.OutputFolderRequiredMessage, 
				MainRes.OutputFolderRequiredTitle, 
				MessageBoxButton.OKCancel, 
				MessageBoxImage.Information);

			if (messageResult == MessageBoxResult.Cancel)
			{
				return false;
			}

			return this.outputVM.PickDefaultOutputFolder();
		}

		private bool EnsureValidOutputPath()
		{
			if (this.outputVM.PathIsValid())
			{
				return true;
			}

			Ioc.Get<IMessageBoxService>().Show(
				MainRes.OutputPathNotValidMessage,
				MainRes.OutputPathNotValidTitle, 
				MessageBoxButton.OK,
				MessageBoxImage.Error);

			return false;
		}

		/// <summary>
		/// Picks title numbers to encode from a video source.
		/// </summary>
		/// <param name="videoSource">The scanned instance.</param>
		/// <param name="picker">The picker to use to pick the titles.</param>
		/// <returns>List of title numbers (1-based)</returns>
		private List<int> PickTitles(VideoSource videoSource, Picker picker = null)
		{
			var result = new List<int>();
			if (picker == null)
			{
				picker = this.pickersService.SelectedPicker.Picker;
			}

			if (picker.TitleRangeSelectEnabled)
			{
				TimeSpan startDuration = TimeSpan.FromMinutes(picker.TitleRangeSelectStartMinutes);
				TimeSpan endDuration = TimeSpan.FromMinutes(picker.TitleRangeSelectEndMinutes);

				foreach (SourceTitle title in videoSource.Titles)
				{
					TimeSpan titleDuration = title.Duration.ToSpan();
					if (titleDuration >= startDuration && titleDuration <= endDuration)
					{
						result.Add(title.Index);
					}
				}
			}
			else if (videoSource.Titles.Count > 0)
			{
				SourceTitle titleToEncode = Utilities.GetFeatureTitle(videoSource.Titles, videoSource.FeatureTitle);
				result.Add(titleToEncode.Index);
			}

			return result;
		}

		private void AutoPauseEncoding(object sender, EventArgs e)
		{
			DispatchUtilities.Invoke(() =>
			{
				if (this.Encoding && !this.Paused)
				{
					this.PauseEncoding();
				}
			});
		}

		private void AutoResumeEncoding(object sender, EventArgs e)
		{
			DispatchUtilities.Invoke(() =>
			{
				if (this.Encoding && this.Paused)
				{
					this.ResumeEncoding();
				}
			});
		}

		// Automatically pick the correct audio on the given job.
		// Only relies on input from settings and the current title.
		private void AutoPickAudio(VCJob job, SourceTitle title, bool useCurrentContext = false)
		{
			Picker picker = this.pickersService.SelectedPicker.Picker;

			job.ChosenAudioTracks = new List<int>();
			switch (picker.AudioSelectionMode)
			{
				case AudioSelectionMode.Disabled:
					if (title.AudioList.Count > 0)
					{
						if (useCurrentContext)
						{
							// With previous context, pick similarly
							foreach (AudioChoiceViewModel audioVM in this.main.AudioChoices)
							{
								int audioIndex = audioVM.SelectedIndex;

								if (title.AudioList.Count > audioIndex && this.main.SelectedTitle.AudioList[audioIndex].LanguageCode == title.AudioList[audioIndex].LanguageCode)
								{
									job.ChosenAudioTracks.Add(audioIndex + 1);
								}
							}

							// If we didn't manage to match any existing audio tracks, use the first audio track.
							if (this.main.AudioChoices.Count > 0 && job.ChosenAudioTracks.Count == 0)
							{
								job.ChosenAudioTracks.Add(1);
							}
						}
						else
						{
							// With no previous context, just pick the first track
							job.ChosenAudioTracks.Add(1);
						}
					}

					break;
				case AudioSelectionMode.Language:
					for (int i = 0; i < title.AudioList.Count; i++)
					{
						SourceAudioTrack track = title.AudioList[i];

						if (track.LanguageCode == picker.AudioLanguageCode)
						{
							job.ChosenAudioTracks.Add(i + 1);

							if (!picker.AudioLanguageAll)
							{
								break;
							}
						}
					}

					break;
				case AudioSelectionMode.All:
					for (int i = 0; i < title.AudioList.Count; i++)
					{
						job.ChosenAudioTracks.Add(i + 1);
					}

					break;
				default:
					throw new ArgumentOutOfRangeException();
			}

			// If none get chosen, pick the first one.
			if (job.ChosenAudioTracks.Count == 0 && title.AudioList.Count > 0)
			{
				job.ChosenAudioTracks.Add(1);
			}
		}

		// Automatically pick the correct subtitles on the given job.
		// Only relies on input from settings and the current title.
		private void AutoPickSubtitles(VCJob job, SourceTitle title, bool useCurrentContext = false)
		{
			Picker picker = this.pickersService.SelectedPicker.Picker;

			job.Subtitles = new VCSubtitles { SourceSubtitles = new List<SourceSubtitle>(), SrtSubtitles = new List<SrtSubtitle>() };
			switch (picker.SubtitleSelectionMode)
			{
				case SubtitleSelectionMode.Disabled:
					// Only pick subtitles when we have previous context.
					if (useCurrentContext)
					{
						foreach (SourceSubtitle sourceSubtitle in this.main.CurrentSubtitles.SourceSubtitles)
						{
							if (sourceSubtitle.TrackNumber == 0)
							{
								job.Subtitles.SourceSubtitles.Add(sourceSubtitle.Clone());
							}
							else if (
								title.SubtitleList.Count > sourceSubtitle.TrackNumber - 1 &&
								this.main.SelectedTitle.SubtitleList[sourceSubtitle.TrackNumber - 1].LanguageCode == title.SubtitleList[sourceSubtitle.TrackNumber - 1].LanguageCode)
							{
								job.Subtitles.SourceSubtitles.Add(sourceSubtitle.Clone());
							}
						}
					}
					break;
				case SubtitleSelectionMode.ForeignAudioSearch:
					job.Subtitles.SourceSubtitles.Add(
						new SourceSubtitle
						{
							TrackNumber = 0,
							BurnedIn = picker.SubtitleForeignBurnIn,
							Forced = true,
							Default = true
						});
					break;
				case SubtitleSelectionMode.Language:
					string languageCode = picker.SubtitleLanguageCode;
					bool audioSame = false;
					bool burnIn = picker.SubtitleLanguageBurnIn;
					bool def = picker.SubtitleLanguageDefault;
					if (job.ChosenAudioTracks.Count > 0 && title.AudioList.Count > 0)
					{
						if (title.AudioList[job.ChosenAudioTracks[0] - 1].LanguageCode == languageCode)
						{
							audioSame = true;
						}
					}

					if (!picker.SubtitleLanguageOnlyIfDifferent || !audioSame)
					{
						// 0-based indices of the subtitles with the matching language
						var languageSubtitleIndices = new List<int>();
						for (int i = 0; i < title.SubtitleList.Count; i++)
						{
							SourceSubtitleTrack subtitle = title.SubtitleList[i];

							if (subtitle.LanguageCode == languageCode)
							{
								languageSubtitleIndices.Add(i);
							}
						}

						if (languageSubtitleIndices.Count > 1)
						{
							foreach (int subtitleIndex in languageSubtitleIndices)
							{
								job.Subtitles.SourceSubtitles.Add(new SourceSubtitle
								{
									BurnedIn = false,
									Default = false,
									Forced = false,
									TrackNumber = subtitleIndex + 1
								});
							}
						}
						else if (languageSubtitleIndices.Count > 0)
						{
							job.Subtitles.SourceSubtitles.Add(new SourceSubtitle
							{
								BurnedIn = burnIn,
								Default = def,
								Forced = false,
								TrackNumber = languageSubtitleIndices[0] + 1
							});
						}
					}

					break;
				case SubtitleSelectionMode.All:
					for (int i = 0; i < title.SubtitleList.Count; i++)
					{
						job.Subtitles.SourceSubtitles.Add(
							new SourceSubtitle
							{
								TrackNumber = i + 1,
								BurnedIn = false,
								Default = false,
								Forced = false
							});
					}

					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}
	}
}
