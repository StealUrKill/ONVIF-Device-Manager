using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using utils;

namespace odm.ui.core {
	/// <summary>A device that answered the scan.</summary>
	public class SubnetScanResult {
		public IPAddress Address { get; set; }
		public Uri[] Uris { get; set; }
		public string[] Scopes { get; set; }
	}

	/// <summary>
	/// Finds ONVIF devices in other subnets. Multicast discovery does not go through routers,
	/// thus the scanner sends a unicast WS-Discovery probe to each address.
	/// </summary>
	public static class SubnetScanner {
		public const int MaxAddresses = 65536;
		// Above this count, do not try HTTP: the connection attempts take too long.
		public const int MaxHttpAddresses = 4096;
		const int DiscoveryPort = 3702;
		static readonly TimeSpan ReplyTime = TimeSpan.FromSeconds(3);
		static readonly TimeSpan ConnectTime = TimeSpan.FromSeconds(1);
		static readonly TimeSpan HttpTime = TimeSpan.FromSeconds(4);

		/// <summary>
		/// Reads a list of addresses, subnets and ranges, for example "10.10.10.0/24, 10.10.20.1-50, 10.10.30.5".
		/// </summary>
		public static bool TryParse(string text, out List<IPAddress> addresses, out bool tooMany) {
			addresses = new List<IPAddress>();
			tooMany = false;
			var seen = new HashSet<uint>();
			var tokens = (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length == 0)
				return false;
			foreach (var token in tokens) {
				uint first, last;
				if (!TryParseToken(token, out first, out last))
					return false;
				for (ulong a = first; a <= last; a++) {
					if (seen.Add((uint)a))
						addresses.Add(ToAddress((uint)a));
					if (seen.Count > MaxAddresses) {
						tooMany = true;
						return false;
					}
				}
			}
			return true;
		}

		static bool TryParseToken(string token, out uint first, out uint last) {
			first = last = 0;
			var slash = token.IndexOf('/');
			var dash = token.IndexOf('-');
			if (slash > 0) {
				uint ip;
				int bits;
				if (!TryParseIp(token.Substring(0, slash), out ip) || !int.TryParse(token.Substring(slash + 1), out bits) || bits < 8 || bits > 32)
					return false;
				uint mask = bits == 32 ? 0xFFFFFFFF : ~(0xFFFFFFFF >> bits);
				first = ip & mask;
				last = first | ~mask;
				// Do not send to the network and broadcast addresses.
				if (bits <= 30) {
					first++;
					last--;
				}
				return true;
			}
			if (dash > 0) {
				if (!TryParseIp(token.Substring(0, dash), out first))
					return false;
				var end = token.Substring(dash + 1);
				int lastOctet;
				if (int.TryParse(end, out lastOctet)) {
					// The short form 10.10.10.1-50 gives the last number only.
					if (lastOctet < 0 || lastOctet > 255)
						return false;
					last = (first & 0xFFFFFF00) | (uint)lastOctet;
				} else if (!TryParseIp(end, out last)) {
					return false;
				}
				return last >= first;
			}
			if (!TryParseIp(token, out first))
				return false;
			last = first;
			return true;
		}

		static bool TryParseIp(string text, out uint value) {
			value = 0;
			var parts = text.Split('.');
			if (parts.Length != 4)
				return false;
			foreach (var part in parts) {
				byte b;
				if (!byte.TryParse(part, out b))
					return false;
				value = (value << 8) | b;
			}
			return true;
		}

		static IPAddress ToAddress(uint value) {
			return new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
		}

		/// <summary>
		/// Scans the addresses on a background thread. It calls onFound for each device and onCompleted at the end.
		/// Dispose the result to stop the scan.
		/// </summary>
		public static IDisposable Scan(IList<IPAddress> addresses, Action<SubnetScanResult> onFound, Action onCompleted) {
			var cancel = new BooleanDisposable();
			var thread = new Thread(() => {
				try {
					var answered = ProbeAll(addresses, onFound, cancel);
					// Some devices do not answer WS-Discovery. Try the ONVIF device service of the other addresses.
					if (!cancel.IsDisposed && addresses.Count <= MaxHttpAddresses)
						HttpProbeAll(addresses.Where(a => !answered.Contains(a)).ToList(), onFound, cancel);
				} catch (Exception err) {
					dbg.Error(err);
				}
				if (!cancel.IsDisposed)
					onCompleted();
			});
			thread.IsBackground = true;
			thread.Name = "subnet scan";
			thread.Start();
			return cancel;
		}

		static HashSet<IPAddress> ProbeAll(IList<IPAddress> addresses, Action<SubnetScanResult> onFound, BooleanDisposable cancel) {
			var answered = new HashSet<IPAddress>();
			using (var udp = new UdpClient(0)) {
				udp.Client.ReceiveTimeout = 200;
				try {
					// Stop the ICMP errors of closed ports from giving errors on the receive calls.
					const int SIO_UDP_CONNRESET = -1744830452;
					udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
				} catch (Exception) { }
				// Some devices answer only one of the two types. The discovery of the device list sends both too.
				var probes = new[] {
					ProbeMessage("dn:NetworkVideoTransmitter", "http://www.onvif.org/ver10/network/wsdl"),
					ProbeMessage("dn:Device", "http://www.onvif.org/ver10/device/wsdl")
				};
				int sent = 0;
				foreach (var address in addresses) {
					if (cancel.IsDisposed)
						return answered;
					foreach (var probe in probes) {
						try {
							udp.Send(probe, probe.Length, new IPEndPoint(address, DiscoveryPort));
						} catch (SocketException err) {
							dbg.Error(err);
						}
					}
					// A short pause after each group of packets, so that the network does not drop them.
					if (++sent % 64 == 0) {
						Thread.Sleep(5);
						Receive(udp, answered, onFound, 0);
					}
				}
				var end = DateTime.UtcNow + ReplyTime;
				while (DateTime.UtcNow < end && !cancel.IsDisposed)
					Receive(udp, answered, onFound, 50000);
			}
			return answered;
		}

		static void Receive(UdpClient udp, HashSet<IPAddress> answered, Action<SubnetScanResult> onFound, int waitMicroseconds) {
			while (true) {
				byte[] data;
				var remote = new IPEndPoint(IPAddress.Any, 0);
				try {
					if (udp.Available == 0 && !udp.Client.Poll(waitMicroseconds, SelectMode.SelectRead))
						return;
					data = udp.Receive(ref remote);
				} catch (SocketException) {
					// A closed port gives an ICMP error on Windows. Ignore it.
					continue;
				}
				// Each device answers each probe. Use the first answer only.
				if (answered.Contains(remote.Address))
					continue;
				var result = ParseProbeMatch(data, remote.Address);
				if (result == null)
					continue;
				answered.Add(remote.Address);
				onFound(result);
			}
		}

		static byte[] ProbeMessage(string type, string typeNamespace) {
			var xml =
				"<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
				"<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\">" +
				"<s:Header>" +
				"<a:Action s:mustUnderstand=\"1\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>" +
				"<a:MessageID>uuid:" + Guid.NewGuid() + "</a:MessageID>" +
				"<a:ReplyTo><a:Address>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo>" +
				"<a:To s:mustUnderstand=\"1\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>" +
				"</s:Header>" +
				"<s:Body><d:Probe xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\">" +
				"<d:Types xmlns:dn=\"" + typeNamespace + "\">" + type + "</d:Types>" +
				"</d:Probe></s:Body></s:Envelope>";
			return Encoding.UTF8.GetBytes(xml);
		}

		static SubnetScanResult ParseProbeMatch(byte[] data, IPAddress remote) {
			try {
				XDocument doc;
				using (var ms = new MemoryStream(data))
					doc = XDocument.Load(ms);
				var match = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProbeMatch");
				if (match == null)
					return null;
				var xaddrs = Values(match, "XAddrs");
				var uris = new List<Uri>();
				foreach (var x in xaddrs) {
					Uri uri;
					if (Uri.TryCreate(x, UriKind.Absolute, out uri))
						uris.Add(uri);
				}
				// Put the address that answered first. Other addresses can be link-local.
				uris = uris.OrderBy(u => u.Host == remote.ToString() ? 0 : 1).ToList();
				if (uris.Count == 0)
					uris.Add(DefaultUri(remote));
				return new SubnetScanResult { Address = remote, Uris = uris.ToArray(), Scopes = Values(match, "Scopes") };
			} catch (Exception err) {
				dbg.Error(err);
				return null;
			}
		}

		static string[] Values(XElement parent, string localName) {
			var e = parent.Elements().FirstOrDefault(x => x.Name.LocalName == localName);
			if (e == null)
				return new string[0];
			return e.Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		}

		static Uri DefaultUri(IPAddress address) {
			return new Uri("http://" + address + "/onvif/device_service");
		}

		static void HttpProbeAll(IList<IPAddress> addresses, Action<SubnetScanResult> onFound, BooleanDisposable cancel) {
			// First find the addresses with an open HTTP port. A connection attempt to a missing host takes long, thus do many at the same time.
			var open = new List<IPAddress>();
			for (int i = 0; i < addresses.Count && !cancel.IsDisposed; i += 256) {
				var batch = addresses.Skip(i).Take(256).ToList();
				var sockets = batch.Select(a => {
					var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
					try {
						s.BeginConnect(a, 80, ar => { try { s.EndConnect(ar); } catch (Exception) { } }, null);
					} catch (Exception) { }
					return s;
				}).ToList();
				Thread.Sleep(ConnectTime);
				for (int k = 0; k < batch.Count; k++) {
					if (sockets[k].Connected)
						open.Add(batch[k]);
					sockets[k].Close();
				}
			}
			var done = new CountdownEvent(1);
			foreach (var address in open) {
				if (cancel.IsDisposed)
					break;
				done.AddCount();
				var a = address;
				ThreadPool.QueueUserWorkItem(_ => {
					try {
						if (!cancel.IsDisposed && IsOnvifDeviceService(DefaultUri(a)))
							onFound(new SubnetScanResult { Address = a, Uris = new[] { DefaultUri(a) }, Scopes = new string[0] });
					} finally {
						done.Signal();
					}
				});
			}
			done.Signal();
			done.Wait();
		}

		// GetSystemDateAndTime does not need a login. Some ONVIF devices ask for a login all the same.
		static bool IsOnvifDeviceService(Uri uri) {
			var status = Post(uri);
			if (status != HttpStatusCode.Unauthorized)
				return status == HttpStatusCode.OK;
			// Some web servers ask for a login on each path. Then the login request does not show an ONVIF service.
			return Post(new Uri(uri, "/odm-scan-" + Guid.NewGuid().ToString("N"))) == HttpStatusCode.NotFound;
		}

		// Returns OK for a SOAP answer, Unauthorized for a login request, NotFound for other answers and no answer.
		static HttpStatusCode Post(Uri uri) {
			const string body =
				"<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
				"<GetSystemDateAndTime xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>" +
				"</s:Body></s:Envelope>";
			try {
				var request = (HttpWebRequest)WebRequest.Create(uri);
				request.Method = "POST";
				request.ContentType = "application/soap+xml; charset=utf-8";
				request.Timeout = (int)HttpTime.TotalMilliseconds;
				request.ReadWriteTimeout = (int)HttpTime.TotalMilliseconds;
				request.Proxy = null;
				var bytes = Encoding.UTF8.GetBytes(body);
				using (var stream = request.GetRequestStream())
					stream.Write(bytes, 0, bytes.Length);
				using (var response = (HttpWebResponse)request.GetResponse())
					return ReadsAsSoap(response) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
			} catch (WebException err) {
				var response = err.Response as HttpWebResponse;
				if (response == null)
					return HttpStatusCode.NotFound;
				using (response) {
					if (response.StatusCode == HttpStatusCode.Unauthorized)
						return HttpStatusCode.Unauthorized;
					return ReadsAsSoap(response) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
				}
			} catch (Exception err) {
				dbg.Error(err);
				return HttpStatusCode.NotFound;
			}
		}

		static bool ReadsAsSoap(HttpWebResponse response) {
			using (var reader = new StreamReader(response.GetResponseStream())) {
				var text = reader.ReadToEnd();
				return text.IndexOf("Envelope", StringComparison.Ordinal) >= 0;
			}
		}
	}
}
