using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Practices.Prism.Events;
using utils;

namespace odm.ui.views {

	/// <summary>Ask the user what to do when the TLS certificate of a camera changes. If the user
	/// trusts it, ODM pins the new certificate and refreshes the device list.</summary>
	public static class CertificateChangePrompt {
		static bool _attached;

		/// <summary>Subscribe one time for the life of the application.</summary>
		public static void Attach(Dispatcher dispatcher, IEventAggregator eventAggregator) {
			if (_attached)
				return;
			_attached = true;
			// This occurs on the TLS thread. Do not stop that thread for the dialog.
			CertificatePinStore.Instance.CertificateChanged += (s, e) =>
				dispatcher.BeginInvoke(new Action(() => Show(e.Mismatch, eventAggregator)));
		}

		static void Show(CertificatePinStore.Mismatch mismatch, IEventAggregator eventAggregator) {
			try {
				var text = string.Format(
					"The TLS certificate presented by {0} has changed since ODM first trusted it, so the connection was blocked.\n\n" +
					"This is expected after a factory reset, firmware update or certificate regeneration on the camera. " +
					"If none of those happened, someone may be intercepting the connection.\n\n" +
					"Previously trusted (SHA-256):\n{1}\n\n" +
					"Now presented (SHA-256):\n{2}\n\n" +
					"Subject: {3}\n\n" +
					"Trust the new certificate and reconnect?",
					mismatch.Endpoint, mismatch.PinnedFingerprint, mismatch.PresentedFingerprint, mismatch.PresentedSubject);

				var owner = Application.Current != null ? Application.Current.MainWindow : null;
				var result = owner != null
					? MessageBox.Show(owner, text, "Camera Certificate Changed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
					: MessageBox.Show(text, "Camera Certificate Changed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

				if (result == MessageBoxResult.Yes) {
					CertificatePinStore.Instance.AcceptChange(mismatch.Endpoint, mismatch.PresentedFingerprint);
					eventAggregator.GetEvent<Refresh>().Publish(true);
				}
			} catch (Exception err) {
				dbg.Error(err);
			}
		}
	}
}
