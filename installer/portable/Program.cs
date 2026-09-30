using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace odm.portable {
	static class Program {
		const string Title = "ONVIF Device Manager (portable)";
		const string CompleteMarker = ".complete";
		const int ErrorCancelled = 1223;

		[STAThread]
		static int Main(string[] args) {
			try {
				string app = Extract();
				var start = new ProcessStartInfo(Path.Combine(app, "odm.exe")) {
					UseShellExecute = true,
					WorkingDirectory = app,
					Arguments = BuildArguments(DataDir(), args),
				};
				// UseShellExecute shows the UAC prompt, because odm.exe needs administrator rights.
				Process.Start(start);
				return 0;
			} catch (Win32Exception err) when (err.NativeErrorCode == ErrorCancelled) {
				return 1;
			} catch (Exception err) {
				MessageBox.Show("Cannot start ONVIF Device Manager.\n\n" + err.Message, Title,
					MessageBoxButtons.OK, MessageBoxIcon.Error);
				return 1;
			}
		}

		// Config and data go into the folder of this exe.
		static string DataDir() {
			string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location).TrimEnd('\\');
			return dir.EndsWith(":") ? dir + @"\." : dir;
		}

		static string BuildArguments(string dataDir, string[] args) {
			var sb = new StringBuilder("--data-dir " + Quote(dataDir));
			foreach (var arg in args)
				sb.Append(' ').Append(Quote(arg));
			return sb.ToString();
		}

		static string Quote(string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
				return arg;
			var sb = new StringBuilder("\"");
			int slashes = 0;
			foreach (char c in arg) {
				if (c == '\\') { slashes++; continue; }
				sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
				slashes = 0;
			}
			return sb.Append('\\', slashes * 2).Append('"').ToString();
		}

		// The folder name is the version, so the firewall rule for odm.exe stays valid.
		// The marker holds the build ID. A different build replaces the files.
		static string Extract() {
			var asm = Assembly.GetExecutingAssembly();
			string root = Path.Combine(Path.GetTempPath(), "ONVIF Device Manager", "portable");
			string build = asm.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
			using (var zipStream = asm.GetManifestResourceStream("odm.zip"))
			using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read)) {
				var files = zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName).ToList();
				string name = Version(asm);
				string dir = Path.Combine(root, name);
				if (IsReady(dir, build, files))
					return dir;
				if (IsRunning(Path.Combine(dir, "odm.exe"))) {
					// An older build runs from that folder. Use a folder for this build.
					name += "-" + build;
					dir = Path.Combine(root, name);
					if (IsReady(dir, build, files))
						return dir;
				}

				Directory.CreateDirectory(root);
				string tmp = Path.Combine(root, name + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
				zip.ExtractToDirectory(tmp);
				File.WriteAllText(Path.Combine(tmp, CompleteMarker), build);
				try {
					if (Directory.Exists(dir))
						Directory.Delete(dir, true);
					Directory.Move(tmp, dir);
				} catch (IOException) {
					// Another launcher can finish first. Use its folder if it is complete.
					if (!IsReady(dir, build, files))
						throw;
					TryDelete(tmp);
				}
				RemoveOldCaches(root, name);
				return dir;
			}
		}

		// Disk cleanup can remove files from the temp folder. Then the folder is not ready.
		static bool IsReady(string dir, string build, List<string> files) {
			string marker = Path.Combine(dir, CompleteMarker);
			try {
				return File.Exists(marker) && File.ReadAllText(marker).Trim() == build
					&& files.All(f => File.Exists(Path.Combine(dir, f)));
			} catch (IOException) {
				return false;
			}
		}

		static string Version(Assembly asm) {
			var info = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
				.OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
			return info != null ? info.InformationalVersion.Split('+')[0] : asm.GetName().Version.ToString();
		}

		static void RemoveOldCaches(string root, string keep) {
			foreach (var old in Directory.GetDirectories(root)) {
				if (String.Equals(Path.GetFileName(old), keep, StringComparison.OrdinalIgnoreCase))
					continue;
				// Do not touch a folder that another launcher extracts now.
				if (Directory.GetCreationTimeUtc(old) > DateTime.UtcNow.AddHours(-1) && !File.Exists(Path.Combine(old, CompleteMarker)))
					continue;
				if (IsRunning(Path.Combine(old, "odm.exe")))
					continue;
				// Remove the marker first. Then a folder that is only partly removed is not used again.
				try { File.Delete(Path.Combine(old, CompleteMarker)); } catch (Exception) { continue; }
				TryDelete(old);
			}
		}

		// Windows locks the exe file of a running process.
		static bool IsRunning(string exe) {
			if (!File.Exists(exe))
				return false;
			try {
				using (new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
				return false;
			} catch (IOException) {
				return true;
			} catch (UnauthorizedAccessException) {
				return true;
			}
		}

		static void TryDelete(string dir) {
			try { Directory.Delete(dir, true); } catch (Exception) { }
		}
	}
}
