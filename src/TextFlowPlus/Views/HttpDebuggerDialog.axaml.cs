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

namespace TextFlowPlus.Views;

public partial class HttpDebuggerDialog : Window
{
    private readonly HttpClient _httpClient;
    private readonly ObservableCollection<HeaderItem> _headers;
    private readonly ObservableCollection<HeaderItem> _responseHeaders;
    private string _lastResponseContent = "";

    // 防止 macOS 原生文件对话框回调时 GC 导致 SIGSEGV
    private Task? _dialogTask;

    public HttpDebuggerDialog()
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

        Closed += (s, e) => _httpClient.Dispose();
    }

    /// <summary>弹出后把输入光标放到 URL 输入框：构造期间控件还未接入可视树，那时 Focus() 不生效。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UrlTextBox.Focus();
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
                StatusTextBlock.Text = "请输入 URL";
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
        SendButton.Content = isSending ? "发送中..." : "发送";
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

        string content = await response.Content.ReadAsStringAsync();
        _lastResponseContent = content;

        if (content.Trim().StartsWith("{") || content.Trim().StartsWith("["))
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(content);
                content = JsonSerializer.Serialize(
                    JsonSerializer.Deserialize<JsonElement>(content),
                    new JsonSerializerOptions { WriteIndented = true }
                );
            }
            catch { }
        }

        ResponseTextBox.Text = content;
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
                new JsonSerializerOptions { WriteIndented = true }
            );
            BodyTextBox.Text = formatted;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"JSON 格式错误：{ex.Message}";
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
            StatusTextBlock.Text = "响应已复制到剪贴板";
            StatusTextBlock.Foreground = Brushes.Green;
        }
    }

    private async Task SaveResponseAsync()
    {
        if (string.IsNullOrEmpty(_lastResponseContent))
        {
            StatusTextBlock.Text = "没有可保存的响应内容";
            StatusTextBlock.Foreground = Brushes.Red;
            return;
        }

        var storageProvider = StorageProvider;
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存响应",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } },
                new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
        {
            try
            {
                await using var stream = await file.OpenWriteAsync();
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteAsync(_lastResponseContent);
                StatusTextBlock.Text = "响应已保存";
                StatusTextBlock.Foreground = Brushes.Green;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"保存失败：{ex.Message}";
                StatusTextBlock.Foreground = Brushes.Red;
            }
        }
    }
}
