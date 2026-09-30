using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using utils;

namespace odm.ui {
	/// <summary>Switches between the light and the dark theme of ODM.</summary>
	public static class ThemeManager {
		public const string Light = "Light";
		public const string Dark = "Dark";
		/// <summary>Use the app theme of Windows ("Choose your app mode").</summary>
		public const string System = "System";

		static readonly Uri DarkUri = new Uri("pack://application:,,,/odm.ui.views;component/themes/DarkBrushes.xaml", UriKind.Absolute);

		/// <summary>True if the setting gives the dark theme on this computer.</summary>
		public static bool IsDark(string theme) {
			if (theme == Dark) return true;
			// No value is the default: follow the Windows setting.
			if (String.IsNullOrEmpty(theme) || theme == System) return WindowsUsesDarkApps();
			return false;
		}

		/// <summary>Applies the theme. The brushes of the dark dictionary replace the light brushes at once.</summary>
		public static void Apply(string theme) {
			var app = Application.Current;
			if (app == null) return;
			try {
				var merged = app.Resources.MergedDictionaries;
				var current = merged.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.EndsWith("DarkBrushes.xaml", StringComparison.OrdinalIgnoreCase));
				var dark = IsDark(theme);
				if (dark && current == null) {
					// Last in the list, so it has priority over the light brushes.
					merged.Add(new ResourceDictionary { Source = DarkUri });
				} else if (!dark && current != null) {
					merged.Remove(current);
				}
				isDark = dark;
				// The title bar belongs to Windows. Set it for the open windows and for each new window.
				if (!windowHookAdded) {
					windowHookAdded = true;
					EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => SetTitleBar(s as Window)));
				}
				foreach (Window window in app.Windows)
					SetTitleBar(window);
			} catch (Exception err) {
				dbg.Error(err);
			}
		}

		static bool isDark;
		static bool windowHookAdded;

		[DllImport("dwmapi.dll")]
		static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
		[DllImport("user32.dll")]
		static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

		/// <summary>Asks Windows for a dark or light title bar (Windows 10 1809 and later). Older Windows ignores it.</summary>
		static void SetTitleBar(Window window) {
			if (window == null) return;
			try {
				var hwnd = new WindowInteropHelper(window).Handle;
				if (hwnd == IntPtr.Zero) return;
				int value = isDark ? 1 : 0;
				// Attribute 20 is DWMWA_USE_IMMERSIVE_DARK_MODE. Windows 10 before build 18985 uses 19.
				if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
					DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
				// Draw the frame again, so the change shows at once (SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED).
				SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
			} catch (Exception err) {
				dbg.Error(err);
			}
		}

		static bool WindowsUsesDarkApps() {
			try {
				using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
					var value = key == null ? null : key.GetValue("AppsUseLightTheme");
					return value is int && (int)value == 0;
				}
			} catch (Exception err) {
				dbg.Error(err);
				return false;
			}
		}
	}
}
