using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using onvif.services;
using utils;

namespace odm.ui.viewModels {
	public class IPAddressFilterViewModel : FeatureDeviceViewModel {
		public IPAddressFilterViewModel(IUnityContainer container) : base(container) {
			Addresses = new ObservableCollection<string>();
			FilterTypes = new[] { IPAddressFilterType.allow, IPAddressFilterType.deny };
			AddCommand = new DelegateCommand(Add);
			RemoveCommand = new DelegateCommand(() => Addresses.Remove(SelectedAddress), () => SelectedAddress != null);
			SaveCommand = new DelegateCommand(Save);
			RefreshCommand = new DelegateCommand(Reload);
		}

		IPAddressFilter filter;
		public IPAddressFilterType[] FilterTypes { get; private set; }
		public ObservableCollection<string> Addresses { get; private set; }
		public DelegateCommand AddCommand { get; private set; }
		public DelegateCommand RemoveCommand { get; private set; }
		public DelegateCommand SaveCommand { get; private set; }
		public DelegateCommand RefreshCommand { get; private set; }
		public string NewAddress { get; set; }
		public string InputError { get; private set; }

		IPAddressFilterType filterType;
		public IPAddressFilterType FilterType {
			get { return filterType; }
			set { filterType = value; OnPropertyChanged(() => FilterType); OnPropertyChanged(() => AllowWarning); }
		}
		public string AllowWarning { get { return filterType == IPAddressFilterType.allow ? Strings.filterAllowWarning : ""; } }

		string selectedAddress;
		public string SelectedAddress {
			get { return selectedAddress; }
			set { selectedAddress = value; OnPropertyChanged(() => SelectedAddress); RemoveCommand.RaiseCanExecuteChanged(); }
		}

		protected override void Reload() {
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(session.GetIPAddressFilter(), f => {
				filter = f ?? new IPAddressFilter { type = IPAddressFilterType.deny };
				FilterType = filter.type;
				Addresses.Clear();
				foreach (var a in filter.iPv4Address ?? new PrefixedIPv4Address[0])
					Addresses.Add(a.address + "/" + a.prefixLength);
				Current = States.Common;
			}, err => ShowError(err, true)));
		}

		/// <summary>Accepts "a.b.c.d" (prefix 32) or "a.b.c.d/n".</summary>
		public static PrefixedIPv4Address Parse(string text) {
			var parts = (text ?? "").Trim().Split('/');
			System.Net.IPAddress ip;
			if (parts.Length > 2 || !System.Net.IPAddress.TryParse(parts[0], out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
				return null;
			int prefix = 32;
			if (parts.Length == 2 && (!Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > 32))
				return null;
			return new PrefixedIPv4Address { address = ip.ToString(), prefixLength = prefix };
		}

		void Add() {
			var parsed = Parse(NewAddress);
			InputError = parsed == null ? Strings.filterAddress + ": a.b.c.d/n" : "";
			OnPropertyChanged(() => InputError);
			if (parsed == null)
				return;
			var text = parsed.address + "/" + parsed.prefixLength;
			if (!Addresses.Contains(text))
				Addresses.Add(text);
			NewAddress = "";
			OnPropertyChanged(() => NewAddress);
		}

		void Save() {
			// Keep the IPv6 addresses and the extension of the camera.
			filter.type = FilterType;
			filter.iPv4Address = Addresses.Select(Parse).Where(x => x != null).ToArray();
			Current = States.Loading;
			subscription.Add(FeatureCalls.Run(session.SetIPAddressFilter(filter), _ => Reload(), err => ShowError(err, false)));
		}
	}
}
