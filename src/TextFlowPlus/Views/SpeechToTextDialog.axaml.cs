using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
#if WINDOWS
using System.Speech.Recognition;
#endif

namespace TextFlowPlus.Views;

public partial class SpeechToTextDialog : Window
{
    private string? _originalAudioPath;
    private string? _currentAudioPath;
    private string? _tempWavPath;
#if WINDOWS
    private SpeechRecognitionEngine? _recognizer;
#endif

    private const int PcmSampleRate = 16000;
    private const short PcmBitsPerSample = 16;
    private const short PcmChannels = 1;

    private TextBox _filePathTextBox;
    private TextBox _resultTextBox;
    private TextBlock _statusText;

    public SpeechToTextDialog()
    {
        Title = "语音转文本";
        Width = 700; Height = 500;
        MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildUI();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid { Margin = new Thickness(10) };
        mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        mainGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Row 0: File selection
        var topPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _filePathTextBox = new TextBox { IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        var browseBtn = new Button { Content = "浏览音频...", Width = 100, Margin = new Thickness(5, 0, 0, 0) };
        browseBtn.Click += async (s, e) => await BrowseAudioAsync();
        topPanel.Children.Add(_filePathTextBox);
        topPanel.Children.Add(browseBtn);
        Grid.SetRow(topPanel, 0);
        mainGrid.Children.Add(topPanel);

        // Row 1: Action buttons
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        var recognizeBtn = new Button { Content = "开始识别", Width = 100 };
        recognizeBtn.Click += (s, e) => Recognize();
        var copyBtn = new Button { Content = "复制", Width = 80, Margin = new Thickness(10, 0, 0, 0) };
        copyBtn.Click += async (s, e) => await CopyTextAsync();
        _statusText = new TextBlock { Text = "就绪", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0), Foreground = Brushes.Gray, FontSize = 13 };
        btnPanel.Children.Add(recognizeBtn);
        btnPanel.Children.Add(copyBtn);
        btnPanel.Children.Add(_statusText);
        Grid.SetRow(btnPanel, 1);
        mainGrid.Children.Add(btnPanel);

        // Row 2: Result text editor
        _resultTextBox = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            FontSize = 14,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_resultTextBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Grid.SetRow(_resultTextBox, 2);
        mainGrid.Children.Add(_resultTextBox);

        // Row 3: Bottom buttons
        var closeBtn = new Button { Content = "关闭", Width = 80, Margin = new Thickness(0, 10, 0, 0) };
        closeBtn.Click += (s, e) => { Cleanup(); Close(); };
        Grid.SetRow(closeBtn, 3);
        mainGrid.Children.Add(closeBtn);

        Content = mainGrid;
    }

    private async Task BrowseAudioAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("音频文件") { Patterns = new[] { "*.wav", "*.mp3", "*.wma", "*.m4a", "*.aac", "*.ogg", "*.flac", "*.pcm" } },
                new FilePickerFileType("WAV 文件") { Patterns = new[] { "*.wav" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            CleanupTempFile();
            _originalAudioPath = files[0].Path.LocalPath;
            string ext = Path.GetExtension(_originalAudioPath).ToLower();

            if (ext == ".pcm")
            {
                _tempWavPath = ConvertPcmToWav(_originalAudioPath);
                _currentAudioPath = _tempWavPath;
                _filePathTextBox.Text = _originalAudioPath;
                _statusText.Text = $"已选择 PCM 文件（已转换为临时 WAV）：{Path.GetFileName(_originalAudioPath)}";
            }
            else
            {
                _currentAudioPath = _originalAudioPath;
                _filePathTextBox.Text = _originalAudioPath;
                _statusText.Text = $"已选择文件：{Path.GetFileName(_originalAudioPath)}";
            }
            _resultTextBox.Text = "";
        }
    }

    private static string ConvertPcmToWav(string pcmPath)
    {
        byte[] pcmData = File.ReadAllBytes(pcmPath);
        int dataSize = pcmData.Length;
        short blockAlign = (short)(PcmChannels * (PcmBitsPerSample / 8));
        int byteRate = PcmSampleRate * blockAlign;
        int fileSize = 36 + dataSize;

        using var ms = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(ms);

        writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        writer.Write(fileSize);
        writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        writer.Write(16);
        writer.Write((short)1);     // PCM
        writer.Write(PcmChannels);
        writer.Write(PcmSampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(PcmBitsPerSample);

        writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        writer.Write(dataSize);
        writer.Write(pcmData);
        writer.Flush();

        string tempPath = Path.Combine(Path.GetTempPath(), $"pcm_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tempPath, ms.ToArray());
        return tempPath;
    }

    private void Recognize()
    {
        if (string.IsNullOrEmpty(_currentAudioPath) || !File.Exists(_currentAudioPath))
        {
            _statusText.Text = "请先选择音频文件";
            return;
        }

#if WINDOWS
        string ext = Path.GetExtension(_currentAudioPath).ToLower();
        if (ext != ".wav")
        {
            _statusText.Text = "语音识别仅支持 WAV 格式，请转换后重试";
            return;
        }

        try
        {
            var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
            if (installedRecognizers == null || installedRecognizers.Count == 0)
            {
                _statusText.Text = "系统中未找到语音识别引擎，请在 Windows 设置中安装语音识别语言包";
                return;
            }

            _statusText.Text = "正在识别语音...";
            StartRecognition();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"语音识别初始化失败：{ex.Message}";
        }
#else
        _statusText.Text = "语音转文本功能仅 Windows 可用";
#endif
    }

#if WINDOWS
    private void StartRecognition()
    {
        try
        {
            var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.Name.StartsWith("zh-"))
                ?? SpeechRecognitionEngine.InstalledRecognizers().First();

            _recognizer = new SpeechRecognitionEngine(recognizerInfo);
            _recognizer.SetInputToWaveFile(_currentAudioPath!);
            _recognizer.LoadGrammar(new DictationGrammar());

            _recognizer.SpeechRecognized += (s, e) =>
            {
                if (e.Result != null && !string.IsNullOrEmpty(e.Result.Text))
                    _resultTextBox.Text += e.Result.Text;
            };

            _recognizer.RecognizeCompleted += (s, e) =>
            {
                if (e.Error != null)
                    _statusText.Text = $"识别出错：{e.Error.Message}";
                else if (string.IsNullOrEmpty(_resultTextBox.Text))
                {
                    _resultTextBox.Text = "(未识别到语音内容)";
                    _statusText.Text = "未能识别出语音内容";
                }
                else
                    _statusText.Text = "识别完成";
                CleanupRecognizer();
            };

            _statusText.Text = $"正在识别（{recognizerInfo.Culture.DisplayName}）...";
            _recognizer.RecognizeAsync(RecognizeMode.Single);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"语音识别失败：{ex.Message}";
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private void CleanupRecognizer()
    {
        if (_recognizer != null)
        {
            try { _recognizer.Dispose(); } catch { }
            _recognizer = null;
        }
    }
#endif

    private async Task CopyTextAsync()
    {
        if (string.IsNullOrEmpty(_resultTextBox.Text))
        {
            _statusText.Text = "没有可复制的文本";
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            var data = new Avalonia.Input.DataTransfer(); data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, _resultTextBox.Text)); await topLevel.Clipboard.SetDataAsync(data);
            _statusText.Text = "文本已复制到剪贴板";
        }
    }

    private void CleanupTempFile()
    {
        if (_tempWavPath != null)
        {
            try { if (File.Exists(_tempWavPath)) File.Delete(_tempWavPath); } catch { }
            _tempWavPath = null;
        }
    }

    private void Cleanup()
    {
#if WINDOWS
        CleanupRecognizer();
#endif
        CleanupTempFile();
    }
}
