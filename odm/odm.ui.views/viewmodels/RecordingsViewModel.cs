using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Windows;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using odm.core;
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

	public class RecordingsViewModel : FeatureDeviceViewModel {
		public RecordingsViewModel(IUnityContainer container) : base(container) {
			Items = new ObservableCollection<RecordingItemViewModel>();
			GetReplayUriCommand = new DelegateCommand(GetReplayUri, () => Selected != null);
			CopyCommand = new DelegateCommand(() => {
				try { Clipboard.SetText(ReplayUri); } catch (Exception err) { dbg.Error(err); }
			}, () => !String.IsNullOrEmpty(ReplayUri));
			RefreshCommand = new DelegateCommand(Reload);
			PlayCommand = new DelegateCommand(Play, () => Selected != null);
			StopCommand = new DelegateCommand(StopPlayback, () => PlayerContent != null);
		}

		public ObservableCollection<RecordingItemViewModel> Items { get; private set; }
		public DelegateCommand GetReplayUriCommand { get; private set; }
		public DelegateCommand CopyCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }
		public DelegateCommand PlayCommand { get; private set; }
		public DelegateCommand StopCommand { get; private set; }
		public string Summary { get; private set; }

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
					PlayDate = start.Date;
					PlayTime = start.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
					OnPropertyChanged(() => PlayDate);
					OnPropertyChanged(() => PlayTime);
				}
				GetReplayUriCommand.RaiseCanExecuteChanged();
				PlayCommand.RaiseCanExecuteChanged();
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

		void Play() {
			var item = Selected;
			TimeSpan time;
			if (!PlayDate.HasValue || !TimeSpan.TryParse(PlayTime, CultureInfo.InvariantCulture, out time)) {
				SetPlayError(Strings.recordingFrom + ": yyyy-MM-dd HH:mm:ss");
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
			var res = (profiles ?? new Profile[0])
				.Where(p => p.videoEncoderConfiguration != null && p.videoEncoderConfiguration.resolution != null)
				.Select(p => p.videoEncoderConfiguration.resolution)
				.FirstOrDefault(r => r.width > 0 && r.height > 0);
			return res;
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
			base.Dispose();
		}
	}
}
