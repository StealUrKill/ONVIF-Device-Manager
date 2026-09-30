using odm.ui.viewModels;

namespace odm.ui.controls {
	public partial class VideoSourceView : BasePropertyControl {
		public VideoSourceView(VideoSourceViewModel viewModel) {
			InitializeComponent();
			this.DataContext = viewModel;
		}
	}
}
