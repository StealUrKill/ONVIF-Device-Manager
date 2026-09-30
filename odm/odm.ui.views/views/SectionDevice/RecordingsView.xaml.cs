using odm.ui.viewModels;

namespace odm.ui.controls {
	public partial class RecordingsView : BasePropertyControl {
		public RecordingsView(RecordingsViewModel viewModel) {
			InitializeComponent();
			this.DataContext = viewModel;
		}
	}
}
