using odm.ui.viewModels;

namespace odm.ui.controls {
	public partial class IPAddressFilterView : BasePropertyControl {
		public IPAddressFilterView(IPAddressFilterViewModel viewModel) {
			InitializeComponent();
			this.DataContext = viewModel;
		}
	}
}
