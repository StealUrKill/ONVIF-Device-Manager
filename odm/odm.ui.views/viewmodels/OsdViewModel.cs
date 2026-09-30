using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using onvif.utils;
using utils;

namespace odm.ui.viewModels {
	/// <summary>One OSD item that the page edits. The flags tell which fields apply.</summary>
	public class OsdItemViewModel : NotifyBase {
		public OsdItemViewModel(OsdItem item) { Item = item; }
		public readonly OsdItem Item;

		public string Token { get { return Item.IsNew ? "(new)" : Item.Token; } }
		public bool IsText { get { return Item.IsText; } }
		public bool IsNotText { get { return !Item.IsText; } }
		public string Caption {
			get {
				if (!Item.IsText) return Token + ": " + Item.OsdType;
				if (Item.TextType == "Plain") return Token + ": " + Item.PlainText;
				return Token + ": " + Item.TextType;
			}
		}
		public string TextType {
			get { return Item.TextType; }
			set { Item.TextType = value; Notify("TextType", "IsPlain", "HasDate", "HasTime", "Caption"); }
		}
		public string PlainText {
			get { return Item.PlainText; }
			set { Item.PlainText = value; Notify("PlainText", "Caption"); }
		}
		public string DateFormat { get { return Item.DateFormat; } set { Item.DateFormat = value; Notify("DateFormat"); } }
		public string TimeFormat { get { return Item.TimeFormat; } set { Item.TimeFormat = value; Notify("TimeFormat"); } }
		/// <summary>Empty text means that the camera uses its default size.</summary>
		public string FontSize {
			get { return Item.FontSize.HasValue ? Item.FontSize.Value.ToString(CultureInfo.InvariantCulture) : ""; }
			set {
				int v;
				Item.FontSize = Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? new Nullable<int>(v) : null;
				Notify("FontSize");
			}
		}
		public string PositionType {
			get { return Item.PositionType; }
			set { Item.PositionType = value; Notify("PositionType", "IsCustomPosition"); }
		}
		public double X { get { return Item.X; } set { Item.X = Math.Max(-1, Math.Min(1, value)); Notify("X"); } }
		public double Y { get { return Item.Y; } set { Item.Y = Math.Max(-1, Math.Min(1, value)); Notify("Y"); } }

		public bool IsPlain { get { return Item.TextType == "Plain"; } }
		public bool HasDate { get { return Item.TextType == "Date" || Item.TextType == "DateAndTime"; } }
		public bool HasTime { get { return Item.TextType == "Time" || Item.TextType == "DateAndTime"; } }
		public bool IsCustomPosition { get { return Item.PositionType == "Custom"; } }
		public void Refresh() { Notify("Token", "Caption"); }
	}

	public class OsdViewModel : FeatureChannelViewModel {
		public OsdViewModel(IUnityContainer container) : base(container) {
			Items = new ObservableCollection<OsdItemViewModel>();
			AddCommand = new DelegateCommand(Add, () => pageData != null);
			DeleteCommand = new DelegateCommand(Delete, () => Selected != null);
			SaveCommand = new DelegateCommand(Save, () => Selected != null && Selected.IsText);
			RefreshCommand = new DelegateCommand(Reload);
		}

		OsdPageData pageData;
		public ObservableCollection<OsdItemViewModel> Items { get; private set; }
		public DelegateCommand AddCommand { get; private set; }
		public DelegateCommand DeleteCommand { get; private set; }
		public bool HasSelected { get { return selected != null; } }
		public DelegateCommand SaveCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }

		public string[] TextTypes { get; private set; }
		public string[] PositionOptions { get; private set; }
		public string[] DateFormats { get; private set; }
		public string[] TimeFormats { get; private set; }
		public string FontSizeRange { get; private set; }
		public string Limits { get; private set; }

		OsdItemViewModel selected;
		public OsdItemViewModel Selected {
			get { return selected; }
			set {
				selected = value;
				OnPropertyChanged(() => Selected);
				OnPropertyChanged(() => HasSelected);
				DeleteCommand.RaiseCanExecuteChanged();
				SaveCommand.RaiseCanExecuteChanged();
			}
		}

		protected override void Reload() {
			Current = States.Loading;
			var keep = selected != null ? selected.Item.Token : null;
			subscription.Add(FeatureCalls.Run(FeaturePages.loadOsd(session, profileToken), data => {
				pageData = data;
				var o = data.Options;
				// Show the camera values also when the options do not list them.
				TextTypes = Merge(o.TextTypes, new[] { "Plain", "Date", "Time", "DateAndTime" });
				PositionOptions = Merge(o.PositionOptions, new[] { "Custom" });
				DateFormats = o.DateFormats;
				TimeFormats = o.TimeFormats;
				FontSizeRange = o.FontSizeMin.HasValue && o.FontSizeMax.HasValue ? String.Format("{0} - {1}", o.FontSizeMin.Value, o.FontSizeMax.Value) : "";
				Limits = String.Format("{0}: {1}", Strings.osdLimits, o.MaxTotal.HasValue ? o.MaxTotal.Value.ToString() : "?");
				OnPropertyChanged(() => TextTypes);
				OnPropertyChanged(() => PositionOptions);
				OnPropertyChanged(() => DateFormats);
				OnPropertyChanged(() => TimeFormats);
				OnPropertyChanged(() => FontSizeRange);
				OnPropertyChanged(() => Limits);
				Items.Clear();
				foreach (var item in data.Items)
					Items.Add(new OsdItemViewModel(item));
				Selected = Items.FirstOrDefault(x => x.Item.Token == keep) ?? Items.FirstOrDefault();
				AddCommand.RaiseCanExecuteChanged();
				Current = States.Common;
			}, err => ShowError(err, true)));
		}

		static string[] Merge(string[] fromCamera, string[] fallback) {
			return fromCamera != null && fromCamera.Length > 0 ? fromCamera : fallback;
		}

		void Add() {
			var item = new OsdItem {
				VideoSourceConfigurationToken = pageData.ConfigToken,
				TextType = TextTypes.Contains("Plain") ? "Plain" : TextTypes.FirstOrDefault(),
				PlainText = "",
				PositionType = PositionOptions.Contains("UpperLeft") ? "UpperLeft" : PositionOptions.FirstOrDefault(),
				X = -0.9, Y = 0.9,
				DateFormat = DateFormats.FirstOrDefault() ?? "",
				TimeFormat = TimeFormats.FirstOrDefault() ?? ""
			};
			var vm = new OsdItemViewModel(item);
			Items.Add(vm);
			Selected = vm;
		}

		void Delete() {
			var vm = Selected;
			if (vm.Item.IsNew) {
				Items.Remove(vm);
				Selected = Items.FirstOrDefault();
				return;
			}
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(Osd.delete(session, vm.Item.Token), _ => Reload(), err => ShowError(err, false)));
		}

		void Save() {
			var vm = Selected;
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(Osd.save(session, vm.Item), _ => {
				vm.Refresh();
				// Read the item again: the camera can change or refuse some values.
				Reload();
			}, err => ShowError(err, false)));
		}
	}
}
