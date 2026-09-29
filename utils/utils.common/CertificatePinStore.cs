using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace utils {

	/// <summary>Trust on first use for TLS certificates that fail normal validation. ODM pins a certificate
	/// by SHA-256 per host:port and refuses a changed one until the user accepts it.</summary>
	public sealed class CertificatePinStore {
		static readonly CertificatePinStore _instance = new CertificatePinStore();
		public static CertificatePinStore Instance { get { return _instance; } }

		public sealed class Pin {
			public string Endpoint { get; internal set; }
			public string Fingerprint { get; internal set; }
			public string Subject { get; internal set; }
			public DateTime FirstSeen { get; internal set; }
		}

		/// <summary>A refused certificate of a pinned endpoint. The user must make a decision.</summary>
		public sealed class Mismatch {
			public string Endpoint { get; internal set; }
			public string PinnedFingerprint { get; internal set; }
			public string PresentedFingerprint { get; internal set; }
			public string PresentedSubject { get; internal set; }
			public DateTime DetectedAt { get; internal set; }
		}

		public sealed class CertificateChangedEventArgs : EventArgs {
			public Mismatch Mismatch { get; internal set; }
		}

		/// <summary>Occurs on the TLS thread the first time that ODM refuses a changed certificate.
		/// Handlers must send their work to the UI thread.</summary>
		public event EventHandler<CertificateChangedEventArgs> CertificateChanged;

		readonly object _sync = new object();
		readonly Dictionary<string, Pin> _pins = new Dictionary<string, Pin>(StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string, Mismatch> _mismatches = new Dictionary<string, Mismatch>(StringComparer.OrdinalIgnoreCase);
		string _path;

		CertificatePinStore() {
		}

		/// <summary>Load the pins from <paramref name="path"/> and save changes to it.</summary>
		public void Initialize(string path) {
			lock (_sync) {
				_path = path;
				_pins.Clear();
				_mismatches.Clear();
				if (path == null)
					return;
				string tempPath = path + ".tmp";
				try {
					// Recover from a crash between the temp file write and the swap.
					if (!File.Exists(path) && File.Exists(tempPath))
						File.Move(tempPath, path);
					if (!File.Exists(path))
						return;
					foreach (var line in File.ReadAllLines(path)) {
						var parts = line.Split('\t');
						if (parts.Length < 4 || parts[0].Length == 0 || parts[1].Length == 0)
							continue;
						DateTime firstSeen;
						DateTime.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture,
							System.Globalization.DateTimeStyles.RoundtripKind, out firstSeen);
						_pins[parts[0]] = new Pin {
							Endpoint = parts[0], Fingerprint = parts[1], Subject = parts[2], FirstSeen = firstSeen
						};
					}
				} catch (Exception err) {
					dbg.Error(err);
				}
			}
		}

		public static string EndpointKey(string host, int port) {
			return (host ?? string.Empty).ToLowerInvariant() + ":" + port;
		}

		public static string Fingerprint(X509Certificate cert) {
			using (var sha = new SHA256Managed()) {
				var hash = sha.ComputeHash(cert.GetRawCertData());
				return string.Join(":", hash.Select(b => b.ToString("X2")).ToArray());
			}
		}

		/// <summary>Validate the certificate for a TLS connection to host:port.</summary>
		public bool Validate(string host, int port, X509Certificate cert, SslPolicyErrors errors) {
			if (errors == SslPolicyErrors.None)
				return true;
			if (cert == null)
				return false;

			var endpoint = EndpointKey(host, port);
			var fingerprint = Fingerprint(cert);
			Mismatch raised = null;
			lock (_sync) {
				Pin pin;
				if (!_pins.TryGetValue(endpoint, out pin)) {
					// First use: trust the certificate and keep it.
					_pins[endpoint] = new Pin {
						Endpoint = endpoint, Fingerprint = fingerprint, Subject = cert.Subject, FirstSeen = DateTime.Now
					};
					_mismatches.Remove(endpoint);
					SaveLocked();
					log.WriteInfo(string.Format("TLS: pinned certificate for {0} ({1})", endpoint, fingerprint));
					return true;
				}
				if (pin.Fingerprint == fingerprint)
					return true;

				Mismatch existing;
				if (!_mismatches.TryGetValue(endpoint, out existing) || existing.PresentedFingerprint != fingerprint) {
					raised = new Mismatch {
						Endpoint = endpoint,
						PinnedFingerprint = pin.Fingerprint,
						PresentedFingerprint = fingerprint,
						PresentedSubject = cert.Subject,
						DetectedAt = DateTime.Now
					};
					_mismatches[endpoint] = raised;
				}
			}
			log.WriteError(string.Format("TLS: refused changed certificate for {0} ({1})", endpoint, fingerprint));
			if (raised != null) {
				var h = CertificateChanged;
				if (h != null) {
					try {
						h(this, new CertificateChangedEventArgs { Mismatch = raised });
					} catch (Exception err) {
						dbg.Error(err);
					}
				}
			}
			return false;
		}

		/// <summary>The refused certificate of an endpoint, or null if there is none.</summary>
		public Mismatch GetMismatch(string host, int port) {
			lock (_sync) {
				Mismatch m;
				return _mismatches.TryGetValue(EndpointKey(host, port), out m) ? m : null;
			}
		}

		public IList<Pin> GetPins() {
			lock (_sync) {
				return _pins.Values.OrderBy(p => p.Endpoint).ToList();
			}
		}

		public IList<Mismatch> GetMismatches() {
			lock (_sync) {
				return _mismatches.Values.ToList();
			}
		}

		/// <summary>Trust the refused certificate in place of the pinned certificate. Accept only
		/// the certificate that the user examined (by its fingerprint).</summary>
		public void AcceptChange(string endpoint, string presentedFingerprint) {
			lock (_sync) {
				Mismatch m;
				if (!_mismatches.TryGetValue(endpoint, out m) || m.PresentedFingerprint != presentedFingerprint)
					return;
				_pins[endpoint] = new Pin {
					Endpoint = endpoint, Fingerprint = m.PresentedFingerprint, Subject = m.PresentedSubject, FirstSeen = DateTime.Now
				};
				_mismatches.Remove(endpoint);
				SaveLocked();
			}
		}

		/// <summary>Forget an endpoint. Its next certificate is trusted on first use.</summary>
		public void Remove(string endpoint) {
			lock (_sync) {
				_pins.Remove(endpoint);
				_mismatches.Remove(endpoint);
				SaveLocked();
			}
		}

		void SaveLocked() {
			if (_path == null)
				return;
			try {
				var sb = new StringBuilder();
				foreach (var p in _pins.Values.OrderBy(p => p.Endpoint)) {
					// Tab-separated values. Remove tabs and new lines from the subject text.
					var subject = (p.Subject ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
					sb.Append(p.Endpoint).Append('\t').Append(p.Fingerprint).Append('\t')
						.Append(subject).Append('\t')
						.Append(p.FirstSeen.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
						.Append("\r\n");
				}
				string tempPath = _path + ".tmp";
				File.WriteAllText(tempPath, sb.ToString(), Encoding.UTF8);
				if (File.Exists(_path))
					File.Replace(tempPath, _path, null);
				else
					File.Move(tempPath, _path);
			} catch (Exception err) {
				dbg.Error(err);
			}
		}
	}
}
