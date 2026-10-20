using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TextFlowPlus.Services.Voice;

/// <summary>
/// HTTP ASR 客户端：把 VAD 切出的语音段(wav 字节)POST 到远程识别服务，返回识别文本。
/// 协议：POST /api/asr/transcribe (multipart/form-data, 字段 file)，响应优先按
/// { "code": 200, "msg": "success", "text": "..." } 解析；无 code 字段时回退到
/// 宽松解析（遍历 text/result/transcript/data/recognized/message 等常见字段）。
/// </summary>
public sealed class HttpAsrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    public HttpAsrClient(string endpoint)
    {
        _endpoint = endpoint;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>上传一段 wav(16kHz 单声道 16bit)识别，返回文本(可能为 null/空)。</summary>
    public async Task<string?> RecognizeAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var byteContent = new ByteArrayContent(wavBytes);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(byteContent, "file", $"seg_{DateTime.Now:HHmmss}.wav");

        using var resp = await _http.PostAsync(_endpoint, form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResponse(json);
    }

    /// <summary>宽松解析：先试约定协议 {code,msg,text}，再回退遍历常见文本字段。</summary>
    private static string? ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.String)
        {
            return root.GetString();
        }

        if (root.ValueKind != JsonValueKind.Object) return null;

        // 约定协议：code=200 时取 text 字段
        if (root.TryGetProperty("code", out var code))
        {
            if (code.ValueKind == JsonValueKind.Number && code.GetInt32() == 200)
            {
                return GetStringField(root, "text");
            }
            return null;   // 服务端明确返回了非成功 code
        }

        // 回退：遍历常见文本字段
        foreach (var field in new[] { "text", "result", "transcript", "data", "recognized", "message" })
        {
            var text = GetStringField(root, field);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string? GetStringField(JsonElement root, string field)
    {
        if (root.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
