using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Practices.Prism.Commands;
using odm.ui.core;
using odm.ui.viewModels;
using utils;
using System.Windows.Media;

namespace odm.ui.controls {

	public partial class DeviceListView : UserControl {
		public static readonly RoutedCommand HideCommand = new RoutedCommand("Hide", typeof(DeviceListView));
        public LocalDeviceList Strings { get { return LocalDeviceList.instance;} }
		DeviceListViewModel viewModel;
		public DeviceListView(DeviceListViewModel viewModel) {
			this.viewModel = viewModel;
			this.DataContext = viewModel;
			InitializeComponent();
			//hideButton.Command = new DelegateCommand(
			//   () => this.Visibility = Visibility.Collapsed,
			//   () => true
			//);


			btnresetFilter.Click+=new RoutedEventHandler((o,e)=>{
				valueFilter.Text = "";
			});

			// The menu is made each time it opens, because the list of saved accounts can change.
			deviceList.ContextMenu = new ContextMenu();
			deviceList.ContextMenuOpening += OnDeviceMenuOpening;
			
			Loaded += (s,a)=>{
                deviceList.CreateBinding(DeviceListControl.FirmwareCaptionProperty, Strings, x => x.firmware);
                deviceList.CreateBinding(DeviceListControl.LocationCaptionProperty, Strings, x => x.location);
                deviceList.CreateBinding(DeviceListControl.AddressCaptionProperty, Strings, x => x.address);
				viewModel.LoadDevices();
			};
		}

		void OnDeviceMenuOpening(object sender, ContextMenuEventArgs e) {
			var source = e.OriginalSource as FrameworkElement;
			var dev = source == null ? null : source.DataContext as DeviceDescriptionHolder;
			if (dev == null) {
				// Not on a device.
				e.Handled = true;
				return;
			}
			var menu = deviceList.ContextMenu;
			menu.Items.Clear();

			var add = new MenuItem { Header = Strings.menuAdd, IsEnabled = !dev.IsManual };
			add.Click += (s, a) => viewModel.AddToList(dev);
			menu.Items.Add(add);

			var credentials = new MenuItem { Header = Strings.menuCredentials, ToolTip = Strings.menuCredentialsHint };
			var selected = viewModel.AccountFor(dev);
			var all = new MenuItem { Header = Strings.menuAllCredentials, IsCheckable = true, IsChecked = selected == null };
			all.Click += (s, a) => viewModel.SetAccountFor(dev, null);
			credentials.Items.Add(all);
			credentials.Items.Add(new Separator());
			var accounts = CredentialStore.Instance.GetAll();
			if (accounts.Count == 0)
				credentials.Items.Add(new MenuItem { Header = Strings.menuNoCredentials, IsEnabled = false });
			for (int i = 0; i < accounts.Count; i++) {
				var account = accounts[i];
				// The number and the notes tell apart accounts with the same name.
				var header = (i + 1) + ". " + account.Name + (account.Notes.Length > 0 ? "  (" + account.Notes + ")" : "");
				// A TextBlock header shows "_" in names. A string header uses it as an access key.
				var item = new MenuItem {
					Header = new TextBlock { Text = header },
					IsCheckable = true,
					IsChecked = selected != null && selected.Value.Id == account.Id
				};
				item.Click += (s, a) => viewModel.SetAccountFor(dev, account.Id);
				credentials.Items.Add(item);
			}
			menu.Items.Add(credentials);
		}
	}
}
