using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TextFlowPlus.Views;

public partial class ScreenshotSelectionWindow : Window
{
    private bool _isDragging;
    private Point _startPoint;
    private byte[]? _selectedBitmapData;

    private Image _backgroundImage;
    private Canvas _selectionCanvas;
    private Border _selectionBorder;
    private Rectangle _topOverlay, _bottomOverlay, _leftOverlay, _rightOverlay;

    public byte[]? SelectedBitmapData => _selectedBitmapData;

    public ScreenshotSelectionWindow()
    {
        Title = "截屏 OCR";
        WindowState = WindowState.Maximized;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = new Cursor(StandardCursorType.Cross);

        BuildUI();
        CaptureScreen();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid();

        _backgroundImage = new Image { Stretch = Stretch.None };
        mainGrid.Children.Add(_backgroundImage);

        _selectionCanvas = new Canvas { Background = Brushes.Transparent };
        mainGrid.Children.Add(_selectionCanvas);

        var overlayGrid = new Grid();
        overlayGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        overlayGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        overlayGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        overlayGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        overlayGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        overlayGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));

        _topOverlay = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0, 0, 0), 0.5) };
        Grid.SetColumnSpan(_topOverlay, 3);
        overlayGrid.Children.Add(_topOverlay);

        _bottomOverlay = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0, 0, 0), 0.5) };
        Grid.SetRow(_bottomOverlay, 2);
        Grid.SetColumnSpan(_bottomOverlay, 3);
        overlayGrid.Children.Add(_bottomOverlay);

        _leftOverlay = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0, 0, 0), 0.5) };
        Grid.SetRow(_leftOverlay, 1);
        overlayGrid.Children.Add(_leftOverlay);

        _rightOverlay = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0, 0, 0), 0.5) };
        Grid.SetRow(_rightOverlay, 1);
        Grid.SetColumn(_rightOverlay, 2);
        overlayGrid.Children.Add(_rightOverlay);

        _selectionBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 0)),
            BorderThickness = new Thickness(2),
            Cursor = new Cursor(StandardCursorType.Cross)
        };
        Grid.SetRow(_selectionBorder, 1);
        Grid.SetColumn(_selectionBorder, 1);
        overlayGrid.Children.Add(_selectionBorder);

        _selectionCanvas.Children.Add(overlayGrid);

        var hintText = new TextBlock
        {
            Text = "拖动鼠标选择要识别的区域，按 ESC 取消，双击截取全屏",
            FontSize = 16,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 0, 0)
        };
        _selectionCanvas.Children.Add(hintText);

        Content = mainGrid;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
    }

    private void CaptureScreen()
    {
        try
        {
#if WINDOWS
            // Use WinForms for screen capture on Windows
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            using var bitmap = new System.Drawing.Bitmap(screen.Bounds.Width, screen.Bounds.Height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, screen.Bounds.Size);

            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            _backgroundImage.Source = new Bitmap(ms);
#else
            _backgroundImage.Source = null;
#endif
        }
        catch
        {
            // Failed to capture, will still allow cancellation
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_selectionCanvas).Properties;
        if (props.IsRightButtonPressed)
        {
            Close();
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        _isDragging = true;
        _startPoint = e.GetPosition(_selectionCanvas);
        _startPoint = new Point(Math.Max(0, _startPoint.X), Math.Max(0, _startPoint.Y));

        _selectionBorder.Width = 0;
        _selectionBorder.Height = 0;
        Canvas.SetLeft(_selectionBorder, _startPoint.X);
        Canvas.SetTop(_selectionBorder, _startPoint.Y);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;

        var currentPoint = e.GetPosition(_selectionCanvas);
        double left = Math.Min(_startPoint.X, currentPoint.X);
        double top = Math.Min(_startPoint.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _startPoint.X);
        double height = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(_selectionBorder, left);
        Canvas.SetTop(_selectionBorder, top);
        _selectionBorder.Width = width;
        _selectionBorder.Height = height;

        UpdateOverlay(left, top, width, height);
    }

    private void UpdateOverlay(double left, double top, double width, double height)
    {
        _topOverlay.Height = top;
        _leftOverlay.Width = left;
        Canvas.SetTop(_bottomOverlay, top + height);
        Canvas.SetLeft(_rightOverlay, left + width);
        _bottomOverlay.Height = Math.Max(0, _selectionCanvas.Bounds.Height - top - height);
        _rightOverlay.Width = Math.Max(0, _selectionCanvas.Bounds.Width - left - width);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        var endPoint = e.GetPosition(_selectionCanvas);
        double left = Math.Min(_startPoint.X, endPoint.X);
        double top = Math.Min(_startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - _startPoint.X);
        double height = Math.Abs(endPoint.Y - _startPoint.Y);

        if (width < 10 || height < 10)
        {
            _selectionBorder.Width = 0;
            _selectionBorder.Height = 0;
            return;
        }

        try
        {
            CropSelection(left, top, width, height);
            Close();
        }
        catch
        {
            Close();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void CropSelection(double left, double top, double width, double height)
    {
#if WINDOWS
        if (_backgroundImage.Source is Bitmap source)
        {
            var scaleX = source.Size.Width / _backgroundImage.Bounds.Width;
            var scaleY = source.Size.Height / _backgroundImage.Bounds.Height;

            int pixelLeft = (int)(left * scaleX);
            int pixelTop = (int)(top * scaleY);
            int pixelWidth = (int)(width * scaleX);
            int pixelHeight = (int)(height * scaleY);

            pixelLeft = Math.Max(0, Math.Min(pixelLeft, (int)source.Size.Width - 1));
            pixelTop = Math.Max(0, Math.Min(pixelTop, (int)source.Size.Height - 1));
            pixelWidth = Math.Min(pixelWidth, (int)source.Size.Width - pixelLeft);
            pixelHeight = Math.Min(pixelHeight, (int)source.Size.Height - pixelTop);

            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                int absLeft = screen.Bounds.Left + pixelLeft;
                int absTop = screen.Bounds.Top + pixelTop;

                using var bmp = new System.Drawing.Bitmap(pixelWidth, pixelHeight);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(absLeft, absTop, 0, 0, new System.Drawing.Size(pixelWidth, pixelHeight));

                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                _selectedBitmapData = ms.ToArray();
            }
        }
#endif
    }
}
