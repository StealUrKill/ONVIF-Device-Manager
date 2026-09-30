using System;
using System.Linq;
using System.Windows;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using onvif.services;
using onvif.utils;
using utils;

namespace odm.ui.viewModels {
	/// <summary>The video source mode (Media2) and the rotation (Media1) of one video source.</summary>
	public class VideoSourceViewModel : FeatureChannelViewModel {
		public VideoSourceViewModel(IUnityContainer container) : base(container) {
			SetModeCommand = new DelegateCommand(SetMode, () => SelectedMode != null && !SelectedMode.Enabled);
			SaveRotationCommand = new DelegateCommand(SaveRotation, () => HasRotation);
			RefreshCommand = new DelegateCommand(Reload);
		}

		VideoSourcePageData pageData;
		public DelegateCommand SetModeCommand { get; private set; }
		public DelegateCommand SaveRotationCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }

		public VideoSourceModeItem[] Modes { get; private set; }
		public string ModesError { get; private set; }
		public bool HasModes { get { return Modes != null && Modes.Length > 0; } }
		public string Notice { get; private set; }

		VideoSourceModeItem selectedMode;
		public VideoSourceModeItem SelectedMode {
			get { return selectedMode; }
			set { selectedMode = value; OnPropertyChanged(() => SelectedMode); SetModeCommand.RaiseCanExecuteChanged(); }
		}

		public bool HasRotation { get; private set; }
		public string RotationError { get; private set; }
		public RotateMode[] RotateModes { get; private set; }
		public int[] RotateDegrees { get; private set; }
		RotateMode rotateMode;
		public RotateMode RotateMode {
			get { return rotateMode; }
			set { rotateMode = value; OnPropertyChanged(() => RotateMode); OnPropertyChanged(() => IsDegreeEnabled); }
		}
		/// <summary>The degree applies only in the "ON" mode.</summary>
		public bool IsDegreeEnabled { get { return rotateMode == RotateMode.on && RotateDegrees != null && RotateDegrees.Length > 0; } }
		int? rotateDegree;
		public int? RotateDegree {
			get { return rotateDegree; }
			set { rotateDegree = value; OnPropertyChanged(() => RotateDegree); }
		}

		void SetNotice(string text) { Notice = text; OnPropertyChanged(() => Notice); }

		protected override void Reload() {
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(FeaturePages.loadVideoSource(session, profileToken), data => {
				pageData = data;
				Modes = data.Modes;
				ModesError = HasModes ? "" : (String.IsNullOrEmpty(data.ModesError) ? Strings.notSupported : FeatureCalls.Message(new Exception(data.ModesError)));
				SelectedMode = Modes.FirstOrDefault(x => x.Enabled) ?? Modes.FirstOrDefault();
				OnPropertyChanged(() => Modes);
				OnPropertyChanged(() => ModesError);
				OnPropertyChanged(() => HasModes);

				var options = data.RotateOptions;
				var current = data.Configuration != null && data.Configuration.extension != null ? data.Configuration.extension.rotate : null;
				RotateModes = options != null && options.mode != null ? options.mode : new RotateMode[0];
				RotateDegrees = options != null && options.degreeList != null && options.degreeList.items != null ? options.degreeList.items : new int[0];
				// "Off" alone means that the camera cannot rotate.
				HasRotation = RotateModes.Any(m => m != RotateMode.off);
				RotationError = HasRotation ? "" : Strings.rotationNotSupported;
				RotateMode = current != null ? current.mode : RotateMode.off;
				RotateDegree = current != null && current.degreeSpecified ? current.degree : (int?)(RotateDegrees.Length > 0 ? RotateDegrees[0] : (int?)null);
				OnPropertyChanged(() => RotateModes);
				OnPropertyChanged(() => RotateDegrees);
				OnPropertyChanged(() => HasRotation);
				OnPropertyChanged(() => RotationError);
				OnPropertyChanged(() => IsDegreeEnabled);
				SaveRotationCommand.RaiseCanExecuteChanged();
				Current = States.Common;
			}, err => ShowError(err, true)));
		}

		void SetMode() {
			var mode = SelectedMode;
			if (mode.Reboot) {
				var answer = MessageBox.Show(Strings.modeRebootWarning, Titles.videoSource, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
				if (answer != MessageBoxResult.OK)
					return;
			}
			SetNotice(null);
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(VideoSourceModes.set(session, pageData.SourceToken, mode.Token), reboot => {
				if (reboot) {
					SetNotice(Strings.modeRebooting);
					Current = States.Common;
				} else {
					Reload();
				}
			}, err => ShowError(err, false)));
		}

		void SaveRotation() {
			SetNotice(null);
			Current = States.Loading;
			var degree = rotateMode == RotateMode.on && rotateDegree.HasValue ? new Nullable<int>(rotateDegree.Value) : null;
			subscription.Add(FeatureCalls.Run(FeaturePages.setRotation(session, pageData.Configuration, rotateMode, degree),
				_ => Reload(), err => ShowError(err, false)));
		}
	}
}
