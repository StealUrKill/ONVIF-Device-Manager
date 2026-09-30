using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using odm.ui.viewModels;

namespace odm.ui.controls {
	public partial class PrivacyMasksView : BasePropertyControl {
		readonly PrivacyMasksViewModel viewModel;
		Point? dragStart;
		Rectangle dragShape;

		public PrivacyMasksView(PrivacyMasksViewModel viewModel) {
			InitializeComponent();
			this.viewModel = viewModel;
			this.DataContext = viewModel;
			viewModel.MasksChanged += () => Dispatcher.BeginInvoke(new Action(Redraw));
			maskCanvas.MouseLeftButtonDown += OnMouseDown;
			maskCanvas.MouseMove += OnMouseMove;
			maskCanvas.MouseLeftButtonUp += OnMouseUp;
		}

		// ONVIF coordinates are -1 to 1 with y up. Canvas coordinates are pixels with y down.
		Point ToCanvas(Point p) {
			return new Point((p.X + 1) / 2 * maskArea.Width, (1 - p.Y) / 2 * maskArea.Height);
		}
		Point ToOnvif(Point p) {
			return new Point(
				Math.Max(-1, Math.Min(1, p.X / maskArea.Width * 2 - 1)),
				Math.Max(-1, Math.Min(1, 1 - p.Y / maskArea.Height * 2)));
		}

		void Redraw() {
			// Keep the aspect ratio of the snapshot.
			var bitmap = viewModel.Snapshot as BitmapSource;
			if (bitmap != null && bitmap.PixelWidth > 0)
				maskArea.Height = Math.Round(maskArea.Width * bitmap.PixelHeight / bitmap.PixelWidth);

			maskCanvas.Children.Clear();
			foreach (var mask in viewModel.Items) {
				var isSelected = mask == viewModel.Selected;
				var shape = new Polygon {
					Points = new PointCollection(mask.Item.Points.Select(ToCanvas)),
					Stroke = isSelected ? Brushes.Yellow : Brushes.OrangeRed,
					StrokeThickness = isSelected ? 2 : 1,
					Fill = new SolidColorBrush(Color.FromArgb(mask.Item.Enabled ? (byte)110 : (byte)40, 0, 0, 0)),
					Cursor = Cursors.Hand
				};
				var target = mask;
				shape.MouseLeftButtonDown += (s, e) => { viewModel.Selected = target; e.Handled = true; };
				maskCanvas.Children.Add(shape);
			}
		}

		void OnMouseDown(object sender, MouseButtonEventArgs e) {
			dragStart = e.GetPosition(maskCanvas);
			dragShape = new Rectangle { Stroke = Brushes.Yellow, StrokeDashArray = new DoubleCollection { 3, 2 }, StrokeThickness = 1 };
			Canvas.SetLeft(dragShape, dragStart.Value.X);
			Canvas.SetTop(dragShape, dragStart.Value.Y);
			maskCanvas.Children.Add(dragShape);
			maskCanvas.CaptureMouse();
		}

		void OnMouseMove(object sender, MouseEventArgs e) {
			if (!dragStart.HasValue || dragShape == null)
				return;
			var p = e.GetPosition(maskCanvas);
			Canvas.SetLeft(dragShape, Math.Min(p.X, dragStart.Value.X));
			Canvas.SetTop(dragShape, Math.Min(p.Y, dragStart.Value.Y));
			dragShape.Width = Math.Abs(p.X - dragStart.Value.X);
			dragShape.Height = Math.Abs(p.Y - dragStart.Value.Y);
		}

		void OnMouseUp(object sender, MouseButtonEventArgs e) {
			if (!dragStart.HasValue)
				return;
			maskCanvas.ReleaseMouseCapture();
			var start = dragStart.Value;
			var end = e.GetPosition(maskCanvas);
			dragStart = null;
			maskCanvas.Children.Remove(dragShape);
			dragShape = null;
			// A click without a drag does not add a mask.
			if (Math.Abs(end.X - start.X) < 4 || Math.Abs(end.Y - start.Y) < 4)
				return;
			var a = ToOnvif(new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)));
			var b = ToOnvif(new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));
			viewModel.AddRectangle(a.X, a.Y, b.X, b.Y);
		}
	}
}
