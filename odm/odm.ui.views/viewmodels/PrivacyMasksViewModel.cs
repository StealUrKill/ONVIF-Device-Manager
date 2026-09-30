using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using onvif.utils;
using utils;

namespace odm.ui.viewModels {
	public class MaskItemViewModel : NotifyBase {
		public MaskItemViewModel(MaskItem item) { Item = item; }
		public readonly MaskItem Item;

		public string Token { get { return Item.IsNew ? "(new)" : Item.Token; } }
		public string Caption { get { return String.Format("{0}: {1}{2}", Token, Item.MaskType, Item.Enabled ? "" : " (off)"); } }
		public string MaskType { get { return Item.MaskType; } set { Item.MaskType = value; Notify("MaskType", "Caption"); } }
		public bool Enabled { get { return Item.Enabled; } set { Item.Enabled = value; Notify("Enabled", "Caption"); } }

		/// <summary>The points as text, "x,y x,y ...". Invalid text keeps the old points.</summary>
		public string PointsText {
			get { return String.Join(" ", Item.Points.Select(p => p.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.###", CultureInfo.InvariantCulture))); }
			set {
				var points = ParsePoints(value);
				if (points != null) {
					Item.Points = points;
					var h = PointsChanged;
					if (h != null) h();
				}
				Notify("PointsText");
			}
		}
		public event Action PointsChanged;

		public static Point[] ParsePoints(string text) {
			var result = new System.Collections.Generic.List<Point>();
			foreach (var pair in (text ?? "").Split(new[] { ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
				var xy = pair.Split(',');
				double x, y;
				if (xy.Length != 2 ||
					!Double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
					!Double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
					return null;
				result.Add(new Point(Math.Max(-1, Math.Min(1, x)), Math.Max(-1, Math.Min(1, y))));
			}
			return result.Count >= 3 ? result.ToArray() : null;
		}
		public void Refresh() { Notify("Token", "Caption", "PointsText"); }
	}

	public class PrivacyMasksViewModel : FeatureChannelViewModel {
		public PrivacyMasksViewModel(IUnityContainer container) : base(container) {
			Items = new ObservableCollection<MaskItemViewModel>();
			DeleteCommand = new DelegateCommand(Delete, () => Selected != null);
			SaveCommand = new DelegateCommand(Save, () => Selected != null);
			RefreshCommand = new DelegateCommand(Reload);
		}

		MaskPageData pageData;
		public ObservableCollection<MaskItemViewModel> Items { get; private set; }
		public DelegateCommand DeleteCommand { get; private set; }
		public bool HasSelected { get { return selected != null; } }
		public DelegateCommand SaveCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }
		public string[] MaskTypes { get; private set; }
		public string Limits { get; private set; }
		public string Notice { get; private set; }
		public ImageSource Snapshot { get; private set; }

		/// <summary>The view draws the masks again when this event comes.</summary>
		public event Action MasksChanged;
		void RaiseMasksChanged() { var h = MasksChanged; if (h != null) h(); }

		MaskItemViewModel selected;
		public MaskItemViewModel Selected {
			get { return selected; }
			set {
				selected = value;
				OnPropertyChanged(() => Selected);
				OnPropertyChanged(() => HasSelected);
				DeleteCommand.RaiseCanExecuteChanged();
				SaveCommand.RaiseCanExecuteChanged();
				RaiseMasksChanged();
			}
		}

		void SetNotice(string text) { Notice = text; OnPropertyChanged(() => Notice); }

		protected override void Reload() {
			Reload(null);
		}

		void Reload(string expectedToken) {
			Current = States.Loading;
			var keep = expectedToken ?? (selected != null ? selected.Item.Token : null);
			subscription.Add(FeatureCalls.Run(FeaturePages.loadMasks(session, profileToken), data => {
				pageData = data;
				var o = data.Options;
				MaskTypes = o.Types != null && o.Types.Length > 0 ? o.Types : new[] { "Color" };
				var limits = String.Format("{0}: {1}", Strings.maskMax, o.MaxMasks.HasValue ? o.MaxMasks.Value.ToString() : "?");
				if (o.RectangleOnly) limits += Environment.NewLine + Strings.maskRectangleOnly;
				Limits = limits;
				OnPropertyChanged(() => MaskTypes);
				OnPropertyChanged(() => Limits);
				Items.Clear();
				foreach (var item in data.Items) {
					var vm = new MaskItemViewModel(item);
					vm.PointsChanged += RaiseMasksChanged;
					Items.Add(vm);
				}
				// Some cameras remove a mask that is not enabled. Tell the user when the saved mask is gone.
				if (expectedToken != null && !Items.Any(x => x.Item.Token == expectedToken))
					SetNotice(Strings.maskRemovedByCamera);
				Selected = Items.FirstOrDefault(x => x.Item.Token == keep) ?? Items.FirstOrDefault();
				Current = States.Common;
				RaiseMasksChanged();
				if (Snapshot == null) LoadSnapshot();
			}, err => ShowError(err, true)));
		}

		void LoadSnapshot() {
			subscription.Add(FeatureCalls.Run(new OdmSession(session).GetSnapshot(profileToken), stream => {
				try {
					// The download stream cannot seek. Then WPF decodes later and the size is not known yet.
					var data = new System.IO.MemoryStream();
					using (stream) stream.CopyTo(data);
					data.Position = 0;
					var bitmap = new BitmapImage();
					bitmap.BeginInit();
					bitmap.CacheOption = BitmapCacheOption.OnLoad;
					bitmap.StreamSource = data;
					bitmap.EndInit();
					bitmap.Freeze();
					Snapshot = bitmap;
					OnPropertyChanged(() => Snapshot);
					RaiseMasksChanged();
				} catch (Exception err) {
					dbg.Error(err);
				}
			}, err => dbg.Error(err)));
		}

		/// <summary>Adds a rectangle mask. The values are normalized, -1 to 1, y up.</summary>
		public void AddRectangle(double left, double top, double right, double bottom) {
			if (pageData == null)
				return;
			SetNotice(null);
			var item = new MaskItem {
				ConfigurationToken = pageData.ConfigToken,
				MaskType = MaskTypes.FirstOrDefault() ?? "Color",
				Enabled = true,
				Points = Masks.rectangle(left, top, right, bottom)
			};
			var vm = new MaskItemViewModel(item);
			vm.PointsChanged += RaiseMasksChanged;
			Items.Add(vm);
			Selected = vm;
		}

		void Delete() {
			var vm = Selected;
			SetNotice(null);
			if (vm.Item.IsNew) {
				Items.Remove(vm);
				Selected = Items.FirstOrDefault();
				return;
			}
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(Masks.delete(session, vm.Item.Token), _ => Reload(), err => ShowError(err, false)));
		}

		void Save() {
			var vm = Selected;
			SetNotice(null);
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(Masks.save(session, vm.Item, pageData.Options), _ => {
				vm.Refresh();
				Reload(vm.Item.Token);
			}, err => ShowError(err, false)));
		}
	}
}
