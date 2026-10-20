using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TextFlowPlus.Models;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

/// <summary>
/// HTTP 调试工具（UserControl，可嵌入主窗口标签页）。
/// 由 HttpDebuggerDialog 改造而来：不再以独立窗口弹出，而是作为标签页内容承载。
/// </summary>
public partial class HttpDebuggerView : UserControl, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ObservableCollection<HeaderItem> _headers;
    private readonly ObservableCollection<HeaderItem> _responseHeaders;
    private string _lastResponseContent = "";

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;

    /// <summary>JSON 格式化选项：缩进 + 不转义中文（UnsafeRelaxedJsonEscaping 保留非 ASCII 字符原样显示）。</summary>
    private static readonly JsonSerializerOptions JsonFormatOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static HttpDebuggerView()
    {
        // 注册代码页编码（GBK/GB2312 等），用于解码非 UTF-8 的 HTTP 响应
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public HttpDebuggerView()
    {
        InitializeComponent();

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _headers = new ObservableCollection<HeaderItem>();
        _responseHeaders = new ObservableCollection<HeaderItem>();

        _headers.Add(new HeaderItem { Key = "Content-Type", Value = "application/json" });
        _headers.Add(new HeaderItem { Key = "User-Agent", Value = "TextFlowPlus/1.0" });

        HeadersDataGrid.ItemsSource = _headers;
        ResponseHeadersDataGrid.ItemsSource = _responseHeaders;
        MethodComboBox.SelectedIndex = 0;

        // Wire up events
        SendButton.Click += async (s, e) => await SendRequestAsync();
        UrlTextBox.KeyDown += async (s, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SendRequestAsync(); };
        AddCommonHeadersButton.Click += AddCommonHeaders_Click;
        ClearHeadersButton.Click += ClearHeaders_Click;
        FormatJsonButton.Click += FormatJson_Click;
        CopyResponseButton.Click += async (s, e) => await CopyResponseAsync();
        SaveResponseButton.Click += (s, e) => _dialogTask = SaveResponseAsync();

        // Set DataContext for DataGrid bindings
        HeadersDataGrid.DataContext = _headers;
        ResponseHeadersDataGrid.DataContext = _responseHeaders;
        LangService.LanguageChanged += ApplyTexts;
        ApplyTexts();
    }

    /// <summary>标签页关闭时释放 HttpClient。</summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>按当前界面语言刷新本面板文本。</summary>
    public void ApplyTexts()
    {
        SendButton.Content = LangService.T("发送");
        HeadersTitleText.Text = LangService.T("请求头 (Headers)");
        AddCommonHeadersButton.Content = LangService.T("添加常用头");
        ClearHeadersButton.Content = LangService.T("清除");
        BodyTitleText.Text = LangService.T("请求体 (Body) — 仅 POST/PUT/PATCH");
        FormatJsonButton.Content = LangService.T("格式化 JSON");
        StatusLabelText.Text = LangService.T("状态：");
        TimeLabelText.Text = LangService.T("耗时：");
        CopyResponseButton.Content = LangService.T("复制响应");
        SaveResponseButton.Content = LangService.T("保存响应");
        ResponseTitleText.Text = LangService.T("响应");
    }
    private void ClearHeaders_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _headers.Clear();
    }

    private async Task SendRequestAsync()
    {
        try
        {
            string method = ((MethodComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()) ?? "GET";
            string url = UrlTextBox.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(url))
            {
                StatusTextBlock.Text = LangService.T("请输入 URL");
                StatusTextBlock.Foreground = Brushes.Red;
                return;
            }

            SetSendingState(true);
            var stopwatch = Stopwatch.StartNew();

            using var request = new HttpRequestMessage(GetHttpMethod(method), url);

            foreach (var header in _headers)
            {
                if (!string.IsNullOrEmpty(header.Key))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (method is "POST" or "PUT" or "PATCH")
            {
                string body = BodyTextBox.Text ?? "";
                if (!string.IsNullOrEmpty(body))
                {
                    string contentType = GetContentType();
                    request.Content = new StringContent(body, Encoding.UTF8, contentType);
                }
            }

            var response = await _httpClient.SendAsync(request);
            stopwatch.Stop();

            await DisplayResponseAsync(response, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"错误：{ex.Message}";
            StatusTextBlock.Foreground = Brushes.Red;
            ResponseTextBox.Text = ex.Message;
            TimeTextBlock.Text = "";
        }
        finally
        {
            SetSendingState(false);
        }
    }

    private void SetSendingState(bool isSending)
    {
        SendButton.IsEnabled = !isSending;
        SendButton.Content = isSending ? LangService.T("发送中...") : "发送";
    }

    private static HttpMethod GetHttpMethod(string method)
    {
        return method.ToUpper() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            _ => HttpMethod.Get
        };
    }

    private string GetContentType()
    {
        if (JsonRadioButton.IsChecked == true) return "application/json";
        if (TextRadioButton.IsChecked == true) return "text/plain";
        if (XmlRadioButton.IsChecked == true) return "application/xml";
        if (FormRadioButton.IsChecked == true) return "application/x-www-form-urlencoded";
        return "application/json";
    }

    private async Task DisplayResponseAsync(HttpResponseMessage response, long elapsedMs)
    {
        string statusText = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        StatusTextBlock.Text = statusText;
        StatusTextBlock.Foreground = response.IsSuccessStatusCode ? Brushes.Green : Brushes.Red;

        TimeTextBlock.Text = $"{elapsedMs} ms";

        _responseHeaders.Clear();
        foreach (var header in response.Headers)
        {
            _responseHeaders.Add(new HeaderItem
            {
                Key = header.Key,
                Value = string.Join(", ", header.Value)
            });
        }
        if (response.Content.Headers.ContentType != null)
        {
            _responseHeaders.Add(new HeaderItem
            {
                Key = "Content-Type",
                Value = response.Content.Headers.ContentType.ToString()
            });
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        string content = DecodeResponseBody(bytes, response.Content.Headers.ContentType?.ToString());
        _lastResponseContent = content;

        if (content.Trim().StartsWith("{") || content.Trim().StartsWith("["))
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(content);
                content = JsonSerializer.Serialize(
                    JsonSerializer.Deserialize<JsonElement>(content),
                    JsonFormatOptions
                );
            }
            catch { }
        }

        ResponseTextBox.Text = content;
    }

    /// <summary>
    /// 智能解码响应字节：优先按 Content-Type 的 charset；其次 UTF-8 BOM；
    /// 再严格检测合法 UTF-8；否则回退 GBK（国内服务器常见）；最后 Latin-1 兜底不丢字节。
    /// </summary>
    private static string DecodeResponseBody(byte[] bytes, string? contentType)
    {
        // 1) Content-Type 明确声明的 charset
        if (!string.IsNullOrEmpty(contentType))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                contentType, @"charset\s*=\s*[""']?([A-Za-z0-9_\-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                try { return Encoding.GetEncoding(m.Groups[1].Value).GetString(bytes); }
                catch { /* 未知编码名称，继续后续检测 */ }
            }
        }

        // 2) UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        // 3) 严格 UTF-8：全部字节为合法 UTF-8 序列才按 UTF-8 解码
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 含非法 UTF-8 字节 → 大概率 GBK/GB2312
        }

        // 4) GBK（GB2312 超集）
        try { return Encoding.GetEncoding(936).GetString(bytes); }
        catch { /* 理论上已注册，失败则继续 */ }

        // 5) Latin-1 兜底（单字节无损映射，避免抛出异常）
        return Encoding.Latin1.GetString(bytes);
    }

    private void AddCommonHeaders_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var commonHeaders = new[]
        {
            new HeaderItem { Key = "Accept", Value = "application/json" },
            new HeaderItem { Key = "Authorization", Value = "Bearer " },
            new HeaderItem { Key = "Accept-Language", Value = "zh-CN,zh;q=0.9,en;q=0.8" },
        };

        foreach (var header in commonHeaders)
        {
            if (!_headers.Any(h => h.Key == header.Key))
                _headers.Add(header);
        }
    }

    private void FormatJson_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            string json = BodyTextBox.Text ?? "";
            using var jsonDoc = JsonDocument.Parse(json);
            string formatted = JsonSerializer.Serialize(
                JsonSerializer.Deserialize<JsonElement>(json),
                JsonFormatOptions
            );
            BodyTextBox.Text = formatted;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = LangService.TFormat("JSON 格式错误：{0}", ex.Message);
            StatusTextBlock.Foreground = Brushes.Red;
        }
    }

    private async Task CopyResponseAsync()
    {
        if (string.IsNullOrEmpty(_lastResponseContent)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.Create(Avalonia.Input.DataFormat.Text, _lastResponseContent));
            await topLevel.Clipboard.SetDataAsync(data);
            StatusTextBlock.Text = LangService.T("响应已复制到剪贴板");
            StatusTextBlock.Foreground = Brushes.Green;
        }
    }

    private async Task SaveResponseAsync()
    {
        if (string.IsNullOrEmpty(_lastResponseContent))
        {
            StatusTextBlock.Text = LangService.T("没有可保存的响应内容");
            StatusTextBlock.Foreground = Brushes.Red;
            return;
        }

        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存响应",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(LangService.T("JSON 文件")) { Patterns = new[] { "*.json" } },
                new FilePickerFileType(LangService.T("文本文件")) { Patterns = new[] { "*.txt" } },
                new FilePickerFileType(LangService.T("所有文件")) { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
        {
            try
            {
                await using var stream = await file.OpenWriteAsync();
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteAsync(_lastResponseContent);
                StatusTextBlock.Text = LangService.T("响应已保存");
                StatusTextBlock.Foreground = Brushes.Green;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = LangService.TFormat("保存失败：{0}", ex.Message);
                StatusTextBlock.Foreground = Brushes.Red;
            }
        }
    }
}
