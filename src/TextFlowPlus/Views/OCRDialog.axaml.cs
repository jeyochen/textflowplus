using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
#if WINDOWS
using PaddleOCRSharp;
#endif

namespace TextFlowPlus.Views;

public partial class OCRDialog : Window
{
    private string? _currentImagePath;
    private string? _tempPastedImagePath;
#if WINDOWS
    private OCRResult? _ocrResult;
#endif
    private DispatcherTimer? _spinnerTimer;

    public OCRDialog()
    {
        InitializeComponent();
        InitializeSpinnerAnimation();
        WireUpEvents();
    }

    private void InitializeSpinnerAnimation()
    {
        _spinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _spinnerTimer.Tick += (s, e) =>
        {
            if (LoadingSpinner != null)
            {
                var rotateTransform = LoadingSpinner.RenderTransform as RotateTransform;
                if (rotateTransform == null)
                {
                    rotateTransform = new RotateTransform(0);
                    LoadingSpinner.RenderTransform = rotateTransform;
                }
                rotateTransform.Angle = (rotateTransform.Angle + 30) % 360;
            }
        };
    }

    private void WireUpEvents()
    {
        BrowseImageButton.Click += async (s, e) => await BrowseImageAsync();
        PasteImageButton.Click += async (s, e) => await PasteImageAsync();
        CopyTextButton.Click += async (s, e) => await CopyTextAsync();
        ReOCRButton.Click += async (s, e) => await PerformOCRAsync();
        CloseButton.Click += (s, e) => { CleanupTempFile(); Close(); };

        // Keyboard: Ctrl+V paste
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = PasteImageAsync();
                e.Handled = true;
            }
        };
    }

    private async Task BrowseImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片文件") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });
        if (files.Count > 0)
        {
            _currentImagePath = files[0].Path.LocalPath;
            ImagePathTextBox.Text = _currentImagePath;
            LoadImage(_currentImagePath);
            await PerformOCRAsync();
        }
    }

    private async Task<bool> IsClipboardImageAvailableAsync()
    {
#if WINDOWS
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null) return false;
        try
        {
            var formats = await topLevel.Clipboard.GetFormatsAsync();
            return formats.Contains("image/png") || formats.Contains("image/bmp") || formats.Contains("image/jpeg");
        }
        catch { return false; }
#else
        return false;
#endif
    }

    private async Task PasteImageAsync()
    {
#if WINDOWS
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null) return;
        try
        {
            // 尝试获取图像数据 - Avalonia 12.0 使用 DataTransfer API
            var data = await topLevel.Clipbox.GetDataAsync("image/png");
            if (data == null)
                data = await topLevel.Clipboard.GetDataAsync("image/bmp");
            if (data == null)
                data = await topLevel.Clipboard.GetDataAsync("image/jpeg");

            if (data == null)
            {
                StatusText.Text = "剪贴板中没有图片";
                return;
            }

            CleanupTempFile();
            _tempPastedImagePath = Path.Combine(Path.GetTempPath(), $"ocr_clipboard_{Guid.NewGuid():N}.png");

            // 保存图像到临时文件
            if (data is Stream stream)
            {
                using var fileStream = File.Create(_tempPastedImagePath);
                await stream.CopyToAsync(fileStream);
            }
            else if (data is byte[] bytes)
            {
                await File.WriteAllBytesAsync(_tempPastedImagePath, bytes);
            }
            else
            {
                StatusText.Text = "无法从剪贴板获取图片数据";
                return;
            }

            _currentImagePath = _tempPastedImagePath;
            ImagePathTextBox.Text = "(剪贴板图片)";
            StatusText.Text = "已粘贴剪贴板图片";
            LoadImage(_currentImagePath);
            await PerformOCRAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"粘贴图片失败：{ex.Message}";
        }
#else
        StatusText.Text = "粘贴图片功能仅 Windows 可用";
#endif
    }

    private void LoadImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            PreviewImage.Source = new Bitmap(path);
            StatusText.Text = $"图片已加载：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载图片失败：{ex.Message}";
        }
    }

    private void ShowLoading(bool show)
    {
        if (LoadingPanel != null && ResultTextBox != null)
        {
            LoadingPanel.IsVisible = show;
            ResultTextBox.IsVisible = !show;
            if (show)
                _spinnerTimer?.Start();
            else
                _spinnerTimer?.Stop();
        }
    }

    private async Task PerformOCRAsync()
    {
        if (string.IsNullOrEmpty(_currentImagePath) || !File.Exists(_currentImagePath))
        {
            StatusText.Text = "请先选择图片文件";
            return;
        }

#if WINDOWS
        try
        {
            ShowLoading(true);
            StatusText.Text = "正在识别文字...";

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllPath = Path.Combine(baseDir, "PaddleOCR.dll");

            // 设置 PaddleOCR 原生 DLL 路径
            if (File.Exists(dllPath))
            {
                PaddleOCREngine.PaddleOCRdllPath = baseDir;
            }
            else
            {
                var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, "PaddleOCR.dll")))
                {
                    PaddleOCREngine.PaddleOCRdllPath = exeDir;
                }
            }

            await Task.Run(() =>
            {
                try
                {
                    var modelConfig = new OCRModelConfig
                    {
                        det_infer = Path.Combine(baseDir, "inference", "yt_PP-OCRv5_mobile_det_infer"),
                        cls_infer = Path.Combine(baseDir, "inference", "yt_PP-OCRv5_mobile_cls_infer"),
                        rec_infer = Path.Combine(baseDir, "inference", "yt_PP-OCRv5_mobile_rec_infer")
                    };

                    using var ocr = new PaddleOCREngine(modelConfig);
                    _ocrResult = ocr.DetectText(_currentImagePath);
                }
                catch (Exception ocrEx)
                {
                    StatusText.Text = $"OCR 引擎错误：{ocrEx.Message}";
                }
            });

            ShowLoading(false);

            if (_ocrResult != null && _ocrResult.TextBlocks != null && _ocrResult.TextBlocks.Count > 0)
            {
                var text = string.Join("\n", _ocrResult.TextBlocks.ConvertAll(t => t.Text));
                ResultTextBox.Text = text;
                StatusText.Text = $"识别完成，共识别出 {_ocrResult.TextBlocks.Count} 个文本块";
            }
            else
            {
                ResultTextBox.Text = "(未识别到文字)";
                StatusText.Text = "未识别到文字";
            }
        }
        catch (Exception ex)
        {
            ShowLoading(false);
            StatusText.Text = $"识别失败：{ex.Message}";
        }
#else
        StatusText.Text = "OCR 识别功能仅 Windows 可用";
#endif
    }

    private async Task CopyTextAsync()
    {
        if (string.IsNullOrEmpty(ResultTextBox.Text))
        {
            StatusText.Text = "没有可复制的文本";
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, ResultTextBox.Text));
            await topLevel.Clipboard.SetDataAsync(data);
            StatusText.Text = "文本已复制到剪贴板";
        }
    }

    private void CleanupTempFile()
    {
        if (_tempPastedImagePath != null)
        {
            try { if (File.Exists(_tempPastedImagePath)) File.Delete(_tempPastedImagePath); }
            catch { }
            _tempPastedImagePath = null;
        }
    }

    public void LoadImageFromPath(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _currentImagePath = path;
            ImagePathTextBox.Text = path;
            LoadImage(path);
            _ = PerformOCRAsync();
        }
    }
}
