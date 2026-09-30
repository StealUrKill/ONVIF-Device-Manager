using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using odm.player;

namespace odm.ui.core {
	/// <summary>The video part of an RTSP session description (SDP).</summary>
	public class SdpVideo {
		/// <summary>"H264" or "H265".</summary>
		public string Codec;
		public int PayloadType = -1;
		/// <summary>The control URL of the video track, as the SDP gives it (can be relative).</summary>
		public string Control;
		/// <summary>The control URL of the session, or null.</summary>
		public string SessionControl;
		/// <summary>The parameter sets from the SDP (SPS, PPS and VPS) as NAL units without start codes.</summary>
		public List<byte[]> ParameterSets = new List<byte[]>();

		public static SdpVideo Parse(string sdp) {
			var result = new SdpVideo();
			var sections = Regex.Split(sdp ?? "", @"\r?\nm=");
			var session = Regex.Match(sections[0], @"a=control:(\S+)");
			if (session.Success && session.Groups[1].Value != "*")
				result.SessionControl = session.Groups[1].Value;
			var video = sections.Skip(1).FirstOrDefault(s => s.StartsWith("video"));
			if (video == null)
				return result;
			var control = Regex.Match(video, @"a=control:(\S+)");
			if (control.Success) result.Control = control.Groups[1].Value;
			foreach (Match m in Regex.Matches(video, @"a=rtpmap:(\d+)\s+([A-Za-z0-9\-]+)/")) {
				var name = m.Groups[2].Value.ToUpperInvariant();
				if (name == "H264" || name == "H265" || name == "HEVC") {
					result.PayloadType = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
					result.Codec = name == "H264" ? "H264" : "H265";
					break;
				}
			}
			foreach (var key in new[] { "sprop-vps", "sprop-sps", "sprop-pps", "sprop-parameter-sets" }) {
				var sprop = Regex.Match(video, key + @"=([^;\s]+)");
				if (!sprop.Success) continue;
				foreach (var part in sprop.Groups[1].Value.Split(',')) {
					try {
						if (part.Length > 0) result.ParameterSets.Add(Convert.FromBase64String(part));
					} catch (FormatException) { }
				}
			}
			return result;
		}

		/// <summary>Joins a control URL to the base URL. A query in the base is not part of the path.</summary>
		public static string Resolve(string baseUrl, string control) {
			if (String.IsNullOrEmpty(control) || control == "*") return baseUrl;
			if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return control;
			var b = baseUrl.Split('?')[0];
			return b.EndsWith("/") ? b + control : b + "/" + control;
		}
	}

	/// <summary>Makes access units (Annex-B, with start codes) from RTP payloads (RFC 6184 and RFC 7798).</summary>
	public class RtpDepacketizer {
		static readonly byte[] StartCode = { 0, 0, 0, 1 };
		readonly bool h265;
		readonly List<byte[]> nals = new List<byte[]>();
		MemoryStream fragment;

		public RtpDepacketizer(bool h265) { this.h265 = h265; }

		/// <summary>Adds one RTP payload. Returns true when the payload has NAL units.</summary>
		public void Add(byte[] payload, int offset, int length) {
			if (length < 2) return;
			if (!h265) {
				int type = payload[offset] & 0x1F;
				if (type >= 1 && type <= 23) {
					nals.Add(Copy(payload, offset, length));
				} else if (type == 24) { // STAP-A: 16-bit size, then the NAL unit
					int i = offset + 1, end = offset + length;
					while (i + 2 <= end) {
						int size = (payload[i] << 8) | payload[i + 1];
						i += 2;
						if (size == 0 || i + size > end) break;
						nals.Add(Copy(payload, i, size));
						i += size;
					}
				} else if (type == 28 && length > 2) { // FU-A
					byte indicator = payload[offset], header = payload[offset + 1];
					if ((header & 0x80) != 0) {
						fragment = new MemoryStream();
						fragment.WriteByte((byte)((indicator & 0xE0) | (header & 0x1F)));
					}
					if (fragment != null) {
						fragment.Write(payload, offset + 2, length - 2);
						if ((header & 0x40) != 0) { nals.Add(fragment.ToArray()); fragment = null; }
					}
				}
			} else {
				int type = (payload[offset] >> 1) & 0x3F;
				if (type < 48) {
					nals.Add(Copy(payload, offset, length));
				} else if (type == 48) { // aggregation packet
					int i = offset + 2, end = offset + length;
					while (i + 2 <= end) {
						int size = (payload[i] << 8) | payload[i + 1];
						i += 2;
						if (size == 0 || i + size > end) break;
						nals.Add(Copy(payload, i, size));
						i += size;
					}
				} else if (type == 49 && length > 3) { // fragmentation unit
					byte header = payload[offset + 2];
					if ((header & 0x80) != 0) {
						fragment = new MemoryStream();
						int fuType = header & 0x3F;
						fragment.WriteByte((byte)((payload[offset] & 0x81) | (fuType << 1)));
						fragment.WriteByte(payload[offset + 1]);
					}
					if (fragment != null) {
						fragment.Write(payload, offset + 3, length - 3);
						if ((header & 0x40) != 0) { nals.Add(fragment.ToArray()); fragment = null; }
					}
				}
			}
		}

		public bool HasData { get { return nals.Count > 0; } }

		/// <summary>True if the NAL unit starts a key frame (IDR for H.264, IRAP for H.265).</summary>
		public bool IsKey(byte[] nal) {
			if (!h265) return (nal[0] & 0x1F) == 5;
			int type = (nal[0] >> 1) & 0x3F;
			return type >= 16 && type <= 21;
		}

		/// <summary>True for SPS, PPS and (H.265) VPS.</summary>
		public bool IsParameterSet(byte[] nal) {
			if (!h265) { int t = nal[0] & 0x1F; return t == 7 || t == 8; }
			int type = (nal[0] >> 1) & 0x3F;
			return type >= 32 && type <= 34;
		}

		/// <summary>Takes the NAL units of the current access unit.</summary>
		public List<byte[]> TakeNals() {
			var result = new List<byte[]>(nals);
			nals.Clear();
			return result;
		}

		public static byte[] AnnexB(IEnumerable<byte[]> units) {
			var ms = new MemoryStream();
			foreach (var nal in units) {
				ms.Write(StartCode, 0, StartCode.Length);
				ms.Write(nal, 0, nal.Length);
			}
			return ms.ToArray();
		}

		static byte[] Copy(byte[] src, int offset, int length) {
			var b = new byte[length];
			Buffer.BlockCopy(src, offset, b, 0, length);
			return b;
		}
	}

	/// <summary>Saves a part of an ONVIF recording to an MP4 file. It uses RTSP over TCP (ONVIF replay).</summary>
	public class ReplayDownloader {
		readonly string url;
		readonly NetworkCredential credential;
		TcpClient tcp;
		Stream stream;
		int cseq;
		string session;
		string realm, nonce;
		bool basicAuth;

		public ReplayDownloader(string url, NetworkCredential credential) {
			this.url = url;
			this.credential = credential;
		}

		/// <summary>Called with the saved part (0 to 1) and the number of bytes.</summary>
		public Action<double, long> Progress;

		/// <summary>Saves the video from start to end (UTC) to the MP4 file. Returns the number of frames.</summary>
		public int Download(DateTime startUtc, DateTime endUtc, string path, CancellationToken cancel) {
			var uri = new Uri(url);
			tcp = new TcpClient();
			tcp.ReceiveTimeout = 15000;
			tcp.SendTimeout = 15000;
			tcp.Connect(uri.Host, uri.Port > 0 ? uri.Port : 554);
			stream = tcp.GetStream();
			var frames = 0;
			try {
				var describe = Request("DESCRIBE", url, "Accept: application/sdp\r\n");
				var sdp = SdpVideo.Parse(describe.Body);
				if (sdp.Codec == null)
					throw new NotSupportedException("the recording has no H.264 or H.265 video track");
				var contentBase = describe.Header("Content-Base") ?? url;
				var sessionUrl = sdp.SessionControl != null ? SdpVideo.Resolve(contentBase, sdp.SessionControl) : url;
				var trackUrl = SdpVideo.Resolve(contentBase, sdp.Control);

				var setup = Request("SETUP", trackUrl, "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n");
				var sessionHeader = setup.Header("Session");
				if (sessionHeader != null)
					session = sessionHeader.Split(';')[0].Trim();

				var range = String.Format("Range: clock={0}-{1}\r\n", ClockTime(startUtc), ClockTime(endUtc));
				// "Rate-Control: no" asks the camera to send the data as fast as it can.
				Request("PLAY", sessionUrl, range + "Rate-Control: no\r\n");

				using (var writer = new Mp4Writer(path, sdp.Codec == "H265")) {
					frames = Receive(writer, sdp, endUtc - startUtc, cancel);
					if (!writer.HasVideo)
						throw new InvalidDataException("the camera sent no key frame for this time");
				}
				try { Request("TEARDOWN", sessionUrl, "", false); } catch (Exception) { }
			} finally {
				tcp.Close();
			}
			return frames;
		}

		int Receive(Mp4Writer writer, SdpVideo sdp, TimeSpan duration, CancellationToken cancel) {
			var depacketizer = new RtpDepacketizer(sdp.Codec == "H265");
			var parameterSets = new Dictionary<int, byte[]>();
			foreach (var ps in sdp.ParameterSets) parameterSets[NalKey(ps, sdp.Codec)] = ps;
			long firstTs = -1, lastTs = -1, unwrapped = 0;
			uint prevTs = 0;
			long bytes = 0;
			int frames = 0;
			var header = new byte[4];
			var buffer = new byte[65536];
			while (!cancel.IsCancellationRequested) {
				int first;
				try {
					first = stream.ReadByte();
				} catch (IOException) {
					break; // no data for the timeout: the recording ended
				}
				if (first < 0) break;
				if (first != '$') {
					SkipRtspMessage(first);
					continue;
				}
				ReadExact(header, 0, 3);
				int channel = header[0], length = (header[1] << 8) | header[2];
				if (length > buffer.Length) buffer = new byte[length];
				ReadExact(buffer, 0, length);
				bytes += length + 4;
				if (channel != 0 || length < 12) continue;

				// RTP header: CSRC list, extension and padding.
				int cc = buffer[0] & 0x0F, offset = 12 + cc * 4;
				bool marker = (buffer[1] & 0x80) != 0;
				int payloadType = buffer[1] & 0x7F;
				uint ts = (uint)((buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7]);
				if ((buffer[0] & 0x10) != 0 && offset + 4 <= length)
					offset += 4 + ((buffer[offset + 2] << 8) | buffer[offset + 3]) * 4;
				int end = length;
				if ((buffer[0] & 0x20) != 0) end -= buffer[length - 1];
				if (offset >= end || (sdp.PayloadType >= 0 && payloadType != sdp.PayloadType)) continue;

				if (firstTs < 0) { firstTs = ts; prevTs = ts; unwrapped = ts; }
				unwrapped += (int)(ts - prevTs); // the difference is signed, so a wrap of the 32-bit time is correct
				prevTs = ts;
				depacketizer.Add(buffer, offset, end - offset);
				if (!marker || !depacketizer.HasData) continue;

				var nals = depacketizer.TakeNals();
				foreach (var nal in nals.Where(depacketizer.IsParameterSet))
					parameterSets[NalKey(nal, sdp.Codec)] = nal;
				bool key = nals.Any(depacketizer.IsKey);
				if (key && parameterSets.Count > 0) {
					// A key frame starts with the parameter sets, so the file can start with it.
					var body = nals.Where(n => !depacketizer.IsParameterSet(n));
					nals = parameterSets.OrderBy(ps => ps.Key).Select(ps => ps.Value).Concat(body).ToList();
				}
				writer.WriteFrame(RtpDepacketizer.AnnexB(nals), unwrapped, key);
				if (writer.HasVideo) frames++;
				lastTs = unwrapped;

				var elapsed = TimeSpan.FromSeconds((lastTs - firstTs) / 90000.0);
				var progress = Progress;
				if (progress != null) progress(duration.TotalSeconds > 0 ? Math.Min(1, elapsed.TotalSeconds / duration.TotalSeconds) : 0, bytes);
				// Some cameras send data after the end time. Stop at the end of the requested part.
				if (elapsed >= duration + TimeSpan.FromSeconds(1)) break;
				// Near the end, other cameras stop sending. Then a short wait is enough.
				if (elapsed.TotalSeconds >= duration.TotalSeconds * 0.9) tcp.ReceiveTimeout = 3000;
			}
			return frames;
		}

		// The order of the parameter sets in a key frame: VPS, SPS, PPS.
		static int NalKey(byte[] nal, string codec) {
			if (codec == "H265") return (nal[0] >> 1) & 0x3F;
			int t = nal[0] & 0x1F;
			return t == 7 ? 0 : t == 8 ? 1 : t;
		}

		static string ClockTime(DateTime utc) {
			return utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
		}

		void ReadExact(byte[] b, int offset, int count) {
			while (count > 0) {
				int n = stream.Read(b, offset, count);
				if (n <= 0) throw new EndOfStreamException();
				offset += n; count -= n;
			}
		}

		// An RTSP message inside the interleaved data (for example the reply to TEARDOWN). Read and ignore it.
		void SkipRtspMessage(int first) {
			var head = ReadHead(first);
			var len = Regex.Match(head, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
			if (len.Success) {
				var skip = new byte[int.Parse(len.Groups[1].Value, CultureInfo.InvariantCulture)];
				ReadExact(skip, 0, skip.Length);
			}
		}

		string ReadHead(int first) {
			var sb = new StringBuilder();
			if (first >= 0) sb.Append((char)first);
			while (!sb.ToString().EndsWith("\r\n\r\n")) {
				int c = stream.ReadByte();
				if (c < 0) throw new EndOfStreamException();
				sb.Append((char)c);
				if (sb.Length > 65536) throw new InvalidDataException("the RTSP reply is too long");
			}
			return sb.ToString();
		}

		class Response {
			public int Status;
			public string Head;
			public string Body;
			public string Header(string name) {
				var m = Regex.Match(Head, "^" + Regex.Escape(name) + @":\s*(.+?)\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
				return m.Success ? m.Groups[1].Value.Trim() : null;
			}
		}

		Response Request(string method, string target, string extra, bool check = true) {
			for (int attempt = 0; attempt < 2; attempt++) {
				var sb = new StringBuilder();
				sb.AppendFormat("{0} {1} RTSP/1.0\r\nCSeq: {2}\r\nUser-Agent: ODM\r\nRequire: onvif-replay\r\n", method, target, ++cseq);
				var auth = Authorization(method, target);
				if (auth != null) sb.Append(auth);
				if (session != null) sb.AppendFormat("Session: {0}\r\n", session);
				sb.Append(extra).Append("\r\n");
				var bytes = Encoding.ASCII.GetBytes(sb.ToString());
				stream.Write(bytes, 0, bytes.Length);
				if (!check) return null;

				// Skip interleaved data before the reply.
				int first = stream.ReadByte();
				while (first == '$') {
					var h = new byte[3];
					ReadExact(h, 0, 3);
					var skip = new byte[(h[1] << 8) | h[2]];
					ReadExact(skip, 0, skip.Length);
					first = stream.ReadByte();
				}
				var head = ReadHead(first);
				var response = new Response { Head = head };
				var status = Regex.Match(head, @"^RTSP/1\.\d\s+(\d+)");
				response.Status = status.Success ? int.Parse(status.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
				var len = response.Header("Content-Length");
				if (len != null) {
					var body = new byte[int.Parse(len, CultureInfo.InvariantCulture)];
					ReadExact(body, 0, body.Length);
					response.Body = Encoding.UTF8.GetString(body);
				}
				if (response.Status == 401 && attempt == 0 && credential != null) {
					var challenge = response.Header("WWW-Authenticate") ?? "";
					basicAuth = challenge.StartsWith("Basic", StringComparison.OrdinalIgnoreCase);
					realm = Regex.Match(challenge, "realm=\"([^\"]*)\"").Groups[1].Value;
					nonce = Regex.Match(challenge, "nonce=\"([^\"]*)\"").Groups[1].Value;
					continue;
				}
				if (response.Status != 200)
					throw new WebException(String.Format("{0} failed: {1}", method, head.Split('\r')[0]));
				return response;
			}
			throw new WebException(method + " failed: authentication");
		}

		string Authorization(string method, string target) {
			if (credential == null || (realm == null && !basicAuth)) return null;
			if (basicAuth)
				return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName + ":" + credential.Password)) + "\r\n";
			return String.Format("Authorization: Digest username=\"{0}\", realm=\"{1}\", nonce=\"{2}\", uri=\"{3}\", response=\"{4}\"\r\n",
				credential.UserName, realm, nonce, target, DigestResponse(credential.UserName, credential.Password, realm, nonce, method, target));
		}

		/// <summary>The RFC 2617 digest response without qop (RTSP servers use this form).</summary>
		public static string DigestResponse(string user, string password, string realm, string nonce, string method, string uri) {
			Func<string, string> md5 = s => {
				using (var h = MD5.Create())
					return String.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
			};
			return md5(md5(user + ":" + realm + ":" + password) + ":" + nonce + ":" + md5(method + ":" + uri));
		}
	}
}
