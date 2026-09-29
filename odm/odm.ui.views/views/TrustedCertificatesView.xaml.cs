using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Practices.Prism.Events;
using utils;

namespace odm.ui.views {

	/// <summary>One row of the trusted certificates list.</summary>
	public class TrustedCertificateItem {
		public string Endpoint { get; set; }
		public string Status { get; set; }
		public string Fingerprint { get; set; }
		public string FingerprintDetails { get; set; }
		public string Subject { get; set; }
		public string FirstSeen { get; set; }
		public bool IsChanged { get; set; }
		public string PresentedFingerprint { get; set; }
	}

	/// <summary>Show the pinned certificates and the cameras that have a changed certificate.
	/// The user can trust a new certificate or forget a camera.</summary>
	public partial class TrustedCertificatesView : Window {
		readonly IEventAggregator _eventAggregator;
		bool _modified;

		public TrustedCertificatesView(IEventAggregator eventAggregator) {
			_eventAggregator = eventAggregator;
			InitializeComponent();
			certGrid.SelectionChanged += (s, e) => UpdateButtons();
			Load();
		}

		void Load() {
			var store = CertificatePinStore.Instance;
			var mismatches = store.GetMismatches().ToDictionary(m => m.Endpoint, StringComparer.OrdinalIgnoreCase);
			var items = new List<TrustedCertificateItem>();
			foreach (var pin in store.GetPins()) {
				CertificatePinStore.Mismatch m;
				bool changed = mismatches.TryGetValue(pin.Endpoint, out m);
				items.Add(new TrustedCertificateItem {
					Endpoint = pin.Endpoint,
					Status = changed ? "Changed - blocked" : "Trusted",
					Fingerprint = pin.Fingerprint,
					FingerprintDetails = changed
						? "Trusted: " + pin.Fingerprint + "\nNow presented: " + m.PresentedFingerprint + "\nNew subject: " + m.PresentedSubject
						: pin.Fingerprint,
					Subject = pin.Subject,
					FirstSeen = pin.FirstSeen == default(DateTime) ? "" : pin.FirstSeen.ToString("g"),
					IsChanged = changed,
					PresentedFingerprint = changed ? m.PresentedFingerprint : null
				});
			}
			certGrid.ItemsSource = items;
			UpdateButtons();
		}

		TrustedCertificateItem Selected {
			get { return certGrid.SelectedItem as TrustedCertificateItem; }
		}

		void UpdateButtons() {
			var item = Selected;
			btRemove.IsEnabled = item != null;
			btTrustNew.IsEnabled = item != null && item.IsChanged;
		}

		void BtTrustNew_Click(object sender, RoutedEventArgs e) {
			var item = Selected;
			if (item == null || !item.IsChanged)
				return;
			var answer = MessageBox.Show(this,
				"Trust the new certificate for " + item.Endpoint + "?\n\nNow presented (SHA-256):\n" + item.PresentedFingerprint +
				"\n\nOnly do this if you know why the camera's certificate changed (factory reset, firmware update).",
				"Trust New Certificate", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
			if (answer != MessageBoxResult.Yes)
				return;
			CertificatePinStore.Instance.AcceptChange(item.Endpoint, item.PresentedFingerprint);
			_modified = true;
			Load();
		}

		void BtRemove_Click(object sender, RoutedEventArgs e) {
			var item = Selected;
			if (item == null)
				return;
			CertificatePinStore.Instance.Remove(item.Endpoint);
			_modified = true;
			Load();
		}

		void BtClose_Click(object sender, RoutedEventArgs e) {
			Close();
		}

		protected override void OnClosed(EventArgs e) {
			base.OnClosed(e);
			// Reconnect the cameras that a changed certificate blocked.
			if (_modified) {
				try {
					_eventAggregator.GetEvent<Refresh>().Publish(true);
				} catch (Exception err) {
					dbg.Error(err);
				}
			}
		}
	}
}
