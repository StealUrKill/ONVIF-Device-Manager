using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using odm.core;
using odm.ui.core;
using odm.player;
using odm.ui.activities;
using onvif.services;
using onvif.utils;
using utils;
using DateTime = System.DateTime;

namespace odm.ui.viewModels {
	public class RecordingItemViewModel {
		public RecordingItemViewModel(RecordingItem item) { Item = item; }
		public readonly RecordingItem Item;
		public string Token { get { return Item.Token; } }
		public string Source { get { return String.IsNullOrEmpty(Item.SourceLocation) ? Item.SourceName : Item.SourceName + " (" + Item.SourceLocation + ")"; } }
		public string From { get { return RecordingsViewModel.Local(Item.EarliestRecording); } }
		public string Until { get { return RecordingsViewModel.Local(Item.LatestRecording); } }
		public string Status { get { return Item.Status; } }
		public string Tracks { get { return String.Join(", ", Item.Tracks.Select(t => t.TrackType)); } }
	}

	public class ClipViewModel {
		public ClipViewModel(RecordingClip clip) { Clip = clip; }
		public readonly RecordingClip Clip;
		public string Start { get { return Clip.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } }
		public string End { get { return Clip.End.ToLocalTime().ToString("HH:mm:ss") + (Clip.IsOpen ? " ..." : ""); } }
		public string Duration { get { return RecordingsViewModel.FormatDuration(Clip.Duration); } }
	}

	public class RecordingsViewModel : FeatureDeviceViewModel {
		public RecordingsViewModel(IUnityContainer container) : base(container) {
			Items = new ObservableCollection<RecordingItemViewModel>();
			Clips = new ObservableCollection<ClipViewModel>();
			GetReplayUriCommand = new DelegateCommand(GetReplayUri, () => Selected != null);
			CopyCommand = new DelegateCommand(() => {
				try { Clipboard.SetText(ReplayUri); } catch (Exception err) { dbg.Error(err); }
			}, () => !String.IsNullOrEmpty(ReplayUri));
			RefreshCommand = new DelegateCommand(Reload);
			PlayCommand = new DelegateCommand(Play, () => Selected != null);
			StopCommand = new DelegateCommand(StopPlayback, () => PlayerContent != null);
			FindClipsCommand = new DelegateCommand(FindClips, () => Selected != null && ClipDay.HasValue);
			DownloadCommand = new DelegateCommand(Download, () => SelectedClip != null && downloadCancel == null);
			CancelDownloadCommand = new DelegateCommand(() => downloadCancel.Cancel(), () => downloadCancel != null);
		}

		public DelegateCommand DownloadCommand { get; private set; }
		public DelegateCommand CancelDownloadCommand { get; private set; }
		public string DownloadStatus { get; private set; }
		CancellationTokenSource downloadCancel;

		void SetDownloadStatus(string text) { DownloadStatus = text; OnPropertyChanged(() => DownloadStatus); }

		void SetDownloading(CancellationTokenSource cancel) {
			downloadCancel = cancel;
			DownloadCommand.RaiseCanExecuteChanged();
			CancelDownloadCommand.RaiseCanExecuteChanged();
		}

		/// <summary>Saves the selected clip to an MP4 file. The download runs in the background.</summary>
		void Download() {
			var clip = SelectedClip.Clip;
			var recording = Selected;
			var dialog = new Microsoft.Win32.SaveFileDialog {
				FileName = String.Format("{0}_{1:yyyyMMdd_HHmmss}.mp4", SafeFileName(recording.Token), clip.Start.ToLocalTime()),
				DefaultExt = ".mp4",
				Filter = "MP4 (*.mp4)|*.mp4"
			};
			if (dialog.ShowDialog() != true)
				return;
			var path = dialog.FileName;
			var cancel = new CancellationTokenSource();
			SetDownloading(cancel);
			SetDownloadStatus(Strings.downloadStarting);
			subscription.Add(FeatureCalls.Run(Recordings.getReplayUri(session, recording.Token), uri => {
				var downloader = new ReplayDownloader(uri, session.credentials);
				var lastUpdate = DateTime.MinValue;
				downloader.Progress = (part, bytes) => {
					// Update the page a few times each second, not for each frame.
					if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 250) return;
					lastUpdate = DateTime.UtcNow;
					dispatch.BeginInvoke(new Action(() => SetDownloadStatus(String.Format(Strings.downloadProgress, part * 100, bytes / 1048576.0))));
				};
				Task.Factory.StartNew(() => downloader.Download(clip.Start, clip.End, path, cancel.Token), TaskCreationOptions.LongRunning)
					.ContinueWith(task => dispatch.BeginInvoke(new Action(() => {
						SetDownloading(null);
						if (task.IsFaulted || cancel.IsCancellationRequested) {
							// Remove the part of the file that is not complete.
							try { if (File.Exists(path)) File.Delete(path); } catch (Exception err) { dbg.Error(err); }
						}
						if (cancel.IsCancellationRequested) {
							SetDownloadStatus(Strings.downloadCanceled);
						} else if (task.IsFaulted) {
							dbg.Error(task.Exception);
							SetDownloadStatus(String.Format(Strings.downloadFailed, FeatureCalls.Message(task.Exception)));
						} else {
							SetDownloadStatus(String.Format(Strings.downloadDone, path, task.Result));
						}
					})));
			}, err => {
				SetDownloading(null);
				SetDownloadStatus(String.Format(Strings.downloadFailed, FeatureCalls.Message(err)));
			}));
		}

		static string SafeFileName(string name) {
			foreach (var c in Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');
			return name;
		}

		public ObservableCollection<RecordingItemViewModel> Items { get; private set; }
		public ObservableCollection<ClipViewModel> Clips { get; private set; }
		public DelegateCommand GetReplayUriCommand { get; private set; }
		public DelegateCommand CopyCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }
		public DelegateCommand PlayCommand { get; private set; }
		public DelegateCommand StopCommand { get; private set; }
		public DelegateCommand FindClipsCommand { get; private set; }
		public string Summary { get; private set; }
		public string ClipsStatus { get; private set; }
		readonly SerialDisposable clipSearch = new SerialDisposable();

		RecordingItemViewModel selected;
		public RecordingItemViewModel Selected {
			get { return selected; }
			set {
				selected = value;
				OnPropertyChanged(() => Selected);
				SetReplayUri(null);
				// Start near the end of the recording, where the newest video is.
				if (value != null && value.Item.LatestRecording.HasValue) {
					var start = value.Item.LatestRecording.Value.ToLocalTime().AddMinutes(-5);
					if (value.Item.EarliestRecording.HasValue && start < value.Item.EarliestRecording.Value.ToLocalTime())
						start = value.Item.EarliestRecording.Value.ToLocalTime();
					SetPlayStart(start);
					ClipDay = value.Item.LatestRecording.Value.ToLocalTime().Date;
				} else {
					ClipDay = DateTime.Today;
				}
				GetReplayUriCommand.RaiseCanExecuteChanged();
				PlayCommand.RaiseCanExecuteChanged();
				FindClips();
			}
		}

		/// <summary>The local day that the clip list shows.</summary>
		DateTime? clipDay;
		public DateTime? ClipDay {
			get { return clipDay; }
			set { clipDay = value; OnPropertyChanged(() => ClipDay); FindClipsCommand.RaiseCanExecuteChanged(); }
		}

		ClipViewModel selectedClip;
		public ClipViewModel SelectedClip {
			get { return selectedClip; }
			set {
				selectedClip = value;
				OnPropertyChanged(() => SelectedClip);
				DownloadCommand.RaiseCanExecuteChanged();
				if (value != null) SetPlayStart(value.Clip.Start.ToLocalTime());
			}
		}

		/// <summary>The local date and time where the playback starts.</summary>
		public DateTime? PlayDate { get; set; }
		public string PlayTime { get; set; }
		public string PlayError { get; private set; }

		string replayUri;
		public string ReplayUri { get { return replayUri; } }
		void SetReplayUri(string uri) {
			replayUri = uri;
			OnPropertyChanged(() => ReplayUri);
			CopyCommand.RaiseCanExecuteChanged();
		}

		/// <summary>The player view while a recording plays.</summary>
		public FrameworkElement PlayerContent { get; private set; }
		readonly SerialDisposable player = new SerialDisposable();

		/// <summary>The cameras give the times in UTC. The page shows local time.</summary>
		public static string Local(DateTime? utc) {
			return utc.HasValue ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "";
		}

		public static string FormatDuration(TimeSpan d) {
			return d.TotalHours >= 1 ? String.Format("{0}:{1:mm\\:ss}", (int)d.TotalHours, d) : d.ToString("mm\\:ss");
		}

		protected override void Reload() {
			StopPlayback();
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(FeaturePages.loadRecordings(session), data => {
				var s = data.Summary;
				Summary = String.Format("{0}: {1}   {2}: {3}   {4}: {5}",
					Strings.recordingSummary, s.NumberRecordings.HasValue ? s.NumberRecordings.Value.ToString() : "?",
					Strings.recordingFrom, Local(s.DataFrom), Strings.recordingUntil, Local(s.DataUntil));
				OnPropertyChanged(() => Summary);
				Items.Clear();
				foreach (var r in data.Recordings)
					Items.Add(new RecordingItemViewModel(r));
				Selected = Items.FirstOrDefault();
				Current = States.Common;
			}, err => ShowError(err, true)));
		}

		void GetReplayUri() {
			var item = Selected;
			subscription.Add(FeatureCalls.Run(Recordings.getReplayUri(session, item.Token),
				uri => SetReplayUri(uri), err => ShowError(err, false)));
		}

		void SetPlayError(string text) { PlayError = text; OnPropertyChanged(() => PlayError); }

		void SetPlayStart(DateTime local) {
			PlayDate = local.Date;
			PlayTime = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
			OnPropertyChanged(() => PlayDate);
			OnPropertyChanged(() => PlayTime);
		}

		void SetClipsStatus(string text) { ClipsStatus = text; OnPropertyChanged(() => ClipsStatus); }

		/// <summary>Lists the clips of the selected recording on the selected day.</summary>
		void FindClips() {
			var item = Selected;
			Clips.Clear();
			if (item == null || !ClipDay.HasValue) {
				SetClipsStatus(null);
				return;
			}
			SetClipsStatus(AppStrings.loading);
			var from = DateTime.SpecifyKind(ClipDay.Value.Date, DateTimeKind.Local);
			var video = item.Item.Tracks.FirstOrDefault(t => t.TrackType == "Video");
			clipSearch.Disposable = FeatureCalls.Run(
				Recordings.findClips(session, item.Token, video != null ? video.Token : "", from.ToUniversalTime(), from.AddDays(1).ToUniversalTime()),
				clips => {
					foreach (var c in clips)
						Clips.Add(new ClipViewModel(c));
					var total = TimeSpan.FromSeconds(clips.Sum(c => c.Duration.TotalSeconds));
					SetClipsStatus(clips.Length == 0 ? Strings.clipsNone
						: String.Format(Strings.clipsFound, clips.Length, FormatDuration(total)) + "  " + Strings.clipDoubleClick);
				},
				err => SetClipsStatus(FeatureCalls.Message(err)));
		}

		/// <summary>Plays the clip from its start. The view calls it on a double click.</summary>
		public void PlayClip(ClipViewModel clip) {
			if (clip == null) return;
			SelectedClip = clip;
			Play();
		}

		void Play() {
			var item = Selected;
			TimeSpan time;
			if (!PlayDate.HasValue || !TimeSpan.TryParse(PlayTime, CultureInfo.InvariantCulture, out time)) {
				SetPlayError(Strings.playFrom + ": yyyy-MM-dd HH:mm:ss");
				return;
			}
			SetPlayError(null);
			StopPlayback();
			var start = DateTime.SpecifyKind(PlayDate.Value.Date + time, DateTimeKind.Local);
			subscription.Add(FeatureCalls.Run(Recordings.getReplayUri(session, item.Token), uri => {
				SetReplayUri(uri);
				subscription.Add(FeatureCalls.Run(session.GetProfiles(), profiles => {
					StartPlayer(uri, start, EncoderResolution(profiles));
				}, err => StartPlayer(uri, start, null)));
			}, err => SetPlayError(FeatureCalls.Message(err))));
		}

		/// <summary>The size of the video buffer. The recording has no size in ONVIF, so use the first encoder.</summary>
		static VideoResolution EncoderResolution(Profile[] profiles) {
			return (profiles ?? new Profile[0])
				.Where(p => p.videoEncoderConfiguration != null && p.videoEncoderConfiguration.resolution != null)
				.Select(p => p.videoEncoderConfiguration.resolution)
				.FirstOrDefault(r => r.width > 0 && r.height > 0);
		}

		void StartPlayer(string uri, DateTime start, VideoResolution resolution) {
			var child = container.CreateChildContainer();
			child.RegisterInstance<IViewPresenter>(ViewPresenter.Create(view => {
				PlayerContent = view;
				OnPropertyChanged(() => PlayerContent);
				StopCommand.RaiseCanExecuteChanged();
				return Disposable.Create(() => {
					PlayerContent = null;
					OnPropertyChanged(() => PlayerContent);
					StopCommand.RaiseCanExecuteChanged();
				});
			}));
			child.RegisterInstance<INvtSession>(session);
			child.RegisterInstance(new ReplayStartTime(MediaStreamInfo.ReplayTime(start)));
			var model = new VideoPlayerView.Model(
				streamSetup: new StreamSetup { stream = StreamType.rtpUnicast, transport = new Transport { protocol = TransportProtocol.rtsp } },
				mediaUri: new MediaUri { uri = uri },
				encoderResolution: resolution ?? new VideoResolution { width = 1920, height = 1080 },
				isUriEnabled: false,
				metadataReceiver: null);
			// Stop the player first, then release its container.
			var run = new CompositeDisposable();
			run.Add(VideoPlayerView.Show(child, model).Subscribe(_ => { }, err => SetPlayError(FeatureCalls.Message(err))));
			run.Add(child);
			player.Disposable = run;
		}

		void StopPlayback() {
			player.Disposable = Disposable.Empty;
		}

		public override void Dispose() {
			StopPlayback();
			clipSearch.Disposable = Disposable.Empty;
			if (downloadCancel != null) downloadCancel.Cancel();
			base.Dispose();
		}
	}
}
