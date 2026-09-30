using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using onvif.utils;
using utils;

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
		}

		public ObservableCollection<RecordingItemViewModel> Items { get; private set; }
		public DelegateCommand GetReplayUriCommand { get; private set; }
		public DelegateCommand CopyCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }
		public string Summary { get; private set; }

		RecordingItemViewModel selected;
		public RecordingItemViewModel Selected {
			get { return selected; }
			set {
				selected = value;
				OnPropertyChanged(() => Selected);
				SetReplayUri(null);
				GetReplayUriCommand.RaiseCanExecuteChanged();
			}
		}

		string replayUri;
		public string ReplayUri { get { return replayUri; } }
		void SetReplayUri(string uri) {
			replayUri = uri;
			OnPropertyChanged(() => ReplayUri);
			CopyCommand.RaiseCanExecuteChanged();
		}

		/// <summary>The cameras give the times in UTC. The page shows local time.</summary>
		public static string Local(DateTime? utc) {
			return utc.HasValue ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "";
		}

		protected override void Reload() {
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
	}
}
