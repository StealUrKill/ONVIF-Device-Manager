using odm.ui.viewModels;

namespace odm.ui.controls {
	public partial class OsdView : BasePropertyControl {
		public OsdView(OsdViewModel viewModel) {
			InitializeComponent();
			this.DataContext = viewModel;
		}
	}
}
