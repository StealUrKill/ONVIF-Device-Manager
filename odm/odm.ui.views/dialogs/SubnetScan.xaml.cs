using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using odm.ui.controls;
using odm.ui.core;

namespace odm.ui.views {
	/// <summary>Asks for the subnet to scan: an IP address and a subnet mask.</summary>
	public partial class SubnetScan : DialogWindow {
		/// <summary>One entry of the subnet mask list. Bits is the prefix length; -1 means that the address box gives the range.</summary>
		public class MaskItem {
			public int Bits { get; set; }
			public string Text { get; set; }
		}

		static readonly int[] MaskBits = { 24, 23, 22, 21, 20, 16, 32 };
		readonly List<MaskItem> masks;
		readonly MaskItem fromTextItem;
		MaskItem selectedMask;

		public SubnetScan(string ranges, bool scanOnRefresh) {
			InitializeComponent();
			masks = MaskBits.Select(b => new MaskItem { Bits = b, Text = MaskText(b) }).ToList();
			fromTextItem = new MaskItem { Bits = -1, Text = Strings.scanMaskFromText };
			maskCombo.ItemsSource = masks;
			selectedMask = masks[0];

			// Show a saved "address/bits" as the address and the mask. Show other saved text as it is.
			ranges = (ranges ?? "").Trim();
			var m = Regex.Match(ranges, @"^(\d+\.\d+\.\d+\.\d+)(?:/(\d+))?$");
			if (m.Success) {
				var bits = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 32;
				var saved = masks.FirstOrDefault(x => x.Bits == bits);
				if (saved != null) {
					selectedMask = saved;
					ranges = m.Groups[1].Value;
				}
			}
			rangesText.Text = ranges;
			onRefreshCheck.IsChecked = scanOnRefresh;
			Loaded += (s, e) => {
				rangesText.Focus();
				rangesText.SelectAll();
			};
			Validate();
		}

		static string MaskText(int bits) {
			uint mask = bits == 0 ? 0 : 0xFFFFFFFF << (32 - bits);
			var dotted = string.Join(".", new[] { mask >> 24, (mask >> 16) & 255, (mask >> 8) & 255, mask & 255 }.Select(x => x.ToString()).ToArray());
			if (bits == 32)
				return dotted + "   (" + LocalDeviceList.instance.scanOneAddress + ")";
			// The network and broadcast addresses are not devices.
			var count = (1L << (32 - bits)) - 2;
			return dotted + "   (" + string.Format(LocalDeviceList.instance.scanMaskCount, count) + ")";
		}

		public LocalDeviceList Strings { get { return LocalDeviceList.instance; } }
		public LocalTitles Titles { get { return LocalTitles.instance; } }
		public LocalButtons Buttons { get { return LocalButtons.instance; } }

		/// <summary>The text to save and to scan, for example "10.10.10.1/24".</summary>
		public string Ranges { get; private set; }
		public bool ScanOnRefresh { get { return onRefreshCheck.IsChecked == true; } }
		public List<IPAddress> Addresses { get; private set; }

		// A subnet, a range or a list in the address box gives the addresses itself. Then the mask is not used.
		bool IsListText(string text) {
			return text.IndexOfAny(new[] { '/', '-', ',', ';', ' ' }) >= 0;
		}

		bool updating;
		void Validate() {
			if (updating)
				return;
			updating = true;
			try {
				var text = rangesText.Text.Trim();
				var isList = IsListText(text);
				if (isList) {
					maskCombo.ItemsSource = new[] { fromTextItem };
					maskCombo.SelectedItem = fromTextItem;
					maskCombo.IsEnabled = false;
					Ranges = text;
				} else {
					if (maskCombo.ItemsSource != masks)
						maskCombo.ItemsSource = masks;
					maskCombo.SelectedItem = selectedMask;
					maskCombo.IsEnabled = true;
					Ranges = selectedMask.Bits == 32 ? text : text + "/" + selectedMask.Bits;
				}
				List<IPAddress> addresses = null;
				bool tooMany = false;
				var ok = text.Length > 0 && SubnetScanner.TryParse(Ranges, out addresses, out tooMany);
				Addresses = ok ? addresses : null;
				btnScan.IsEnabled = ok;
				// Do not show an error while the box is empty.
				errorText.Text = tooMany ? Strings.scanTooMany : Strings.scanInvalid;
				errorText.Visibility = !ok && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
			} finally {
				updating = false;
			}
		}

		void OnTextChanged(object sender, TextChangedEventArgs e) {
			Validate();
		}

		void OnMaskChanged(object sender, SelectionChangedEventArgs e) {
			var item = maskCombo.SelectedItem as MaskItem;
			if (updating || item == null || item.Bits < 0)
				return;
			selectedMask = item;
			Validate();
		}

		void OnScan(object sender, RoutedEventArgs e) {
			if (Addresses == null)
				return;
			DialogResult = true;
			Close();
		}

		void OnCancel(object sender, RoutedEventArgs e) {
			DialogResult = false;
			Close();
		}
	}
}
