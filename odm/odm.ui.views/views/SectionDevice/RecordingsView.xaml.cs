using odm.ui.viewModels;

namespace odm.ui.controls {
	public partial class RecordingsView : BasePropertyControl {
		public RecordingsView(RecordingsViewModel viewModel) {
			InitializeComponent();
			this.DataContext = viewModel;
			// A double click on a clip plays it from its start.
			clipsList.MouseDoubleClick += (s, e) => viewModel.PlayClip(clipsList.SelectedItem as ClipViewModel);
		}
	}
}
