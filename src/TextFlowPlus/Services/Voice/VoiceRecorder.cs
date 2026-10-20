using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace TextFlowPlus.Services.Voice;

/// <summary>
/// 语音录制类（参考 MachineAgent 的实现）。使用 NAudio 采集 48kHz 麦克风音频，
/// RNNoise 降噪并降采样到 16kHz，ten_vad.dll 做 VAD 自动切分语句（含噪声段过滤），
/// 语音段通过 HTTP 发送到远程 ASR 服务识别，识别出的文本通过 UtteranceRecognized 事件回调。
/// </summary>
public class VoiceRecorder : IDisposable
{
    // ========== 采集参数 ==========
    private const int InSampleRate = 48000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    // ========== HTTP ASR ==========
    /// <summary>远程 ASR 识别服务地址（POST multipart 字段 file，返回 {code,msg,text}）。</summary>
    public string AsrUrl { get; set; } = "http://asrapi.app2s.cn/api/asr/transcribe";

    // ========== 状态 ==========
    private WaveInEvent? _waveIn;
    private NoiseReducer? _noiseReducer;
    private TenVadVad? _vad;
    private HttpAsrClient? _asr;
    private Channel<float[]>? _queue;
    private CancellationTokenSource? _cts;
    private Task? _uploadTask;
    private int _nextUploadSeq;   // 上传序号，用于丢弃过期状态提示
    private bool _isRecording;
    private bool _isDisposed;

    // ========== 事件 ==========
    /// <summary>每识别出一句文本时触发</summary>
    public event Action<string>? UtteranceRecognized;
    /// <summary>状态变更（用于 UI 显示）</summary>
    public event Action<string>? StatusChanged;
    /// <summary>错误信息</summary>
    public event Action<string>? ErrorOccurred;

    public bool IsRecording => _isRecording;
    /// <summary>是否正在使用本地 ASR（HTTP 模式始终返回 false）</summary>
    public bool UseLocalAsr => false;

    public VoiceRecorder() { }

    /// <summary>开始录音：48kHz 采集 → RNNoise 降噪降采样 → ten_vad VAD 切分 → HTTP ASR。</summary>
    public bool Start()
    {
        if (_isRecording) return false;

        try
        {
            // ten_vad.dll 通过 P/Invoke 动态加载，须在程序目录（或输出目录）存在
            var dllPath = Path.Combine(AppContext.BaseDirectory, "ten_vad.dll");
            if (!File.Exists(dllPath))
            {
                ErrorOccurred?.Invoke($"❌ 缺少 ten_vad.dll（预期位于程序目录：{dllPath}），无法启动语音识别。");
                return false;
            }

            _noiseReducer = new NoiseReducer();
            _vad = new TenVadVad(dllPath);
            _asr = new HttpAsrClient(AsrUrl);

            ResetState();
            _queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _cts = new CancellationTokenSource();
            _uploadTask = Task.Run(() => UploadLoopAsync(_cts.Token));

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(InSampleRate, BitsPerSample, Channels)
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();

            _isRecording = true;
            StatusChanged?.Invoke("🎤 语音识别中… 说完一句话自动识别并插入文档");
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"❌ 录音启动失败：{ex.Message}\n请检查麦克风权限及 ten_vad.dll 是否就位。");
            Cleanup();
            return false;
        }
    }

    /// <summary>停止录音（冲刷尾部语音段后再停止上传任务）</summary>
    public void Stop()
    {
        if (_waveIn == null) return;

        try
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }
        catch { }

        FinishPipeline();
        _isRecording = false;
    }

    // ========== 音频采集 → 降噪 → VAD 切分 ==========

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            var vad = _vad;
            var reducer = _noiseReducer;
            if (vad == null || reducer == null) return;

            var samples48k = BytesToFloats(args.Buffer, args.BytesRecorded);
            if (samples48k.Length == 0) return;

            // RNNoise 降噪 + 3:1 降采样到 16kHz
            var samples16k = reducer.Process(samples48k);
            if (samples16k.Length == 0) return;

            vad.VadAccept(samples16k);

            var queue = _queue;
            while (queue != null && !vad.VadIsEmpty())
            {
                var segment = vad.VadPopSegment();
                if (segment.Length < vad.MinSpeechSamples) continue;   // 过短噪声
                queue.Writer.TryWrite(segment);
            }
        }
        catch { }
    }

    /// <summary>录音停止回调：冲刷 VAD 尾部段后统一释放</summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        FinishPipeline();
        _isRecording = false;
    }

    /// <summary>结束处理链：冲刷 VAD 剩余段入队，停止上传任务，释放资源。</summary>
    private void FinishPipeline()
    {
        try { _vad?.VadFlush(); } catch { }

        // 把 Flush 产出的尾部段也入队
        var queue = _queue;
        var vad = _vad;
        if (queue != null && vad != null)
        {
            while (!vad.VadIsEmpty())
            {
                var segment = vad.VadPopSegment();
                if (segment.Length >= vad.MinSpeechSamples)
                {
                    queue.Writer.TryWrite(segment);
                }
            }
        }

        _cts?.Cancel();
        try { _uploadTask?.Wait(3000); } catch { /* 退出竞态忽略 */ }
        _cts?.Dispose();
        _cts = null;
        _queue = null;

        _vad?.Dispose();
        _vad = null;
        _noiseReducer?.Dispose();
        _noiseReducer = null;
        _asr?.Dispose();
        _asr = null;
    }

    // ========== 后台串行上传识别 ==========

    private async Task UploadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            float[]? segment = null;
            try
            {
                var queue = _queue;
                if (queue == null) break;
                segment = await queue.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }

            if (segment == null || segment.Length == 0) continue;

            await SendSegmentAsync(segment, ct).ConfigureAwait(false);
        }
    }

    /// <summary>将一段语音（float 16kHz）转 WAV 上传识别，回调事件。</summary>
    private async Task SendSegmentAsync(float[] samples, CancellationToken ct)
    {
        var seq = ++_nextUploadSeq;
        var asr = _asr;
        if (asr == null) return;

        StatusChanged?.Invoke("🔄 正在识别…");

        try
        {
            var wav = BuildWav(samples);
            var text = await asr.RecognizeAsync(wav, ct).ConfigureAwait(false);

            var trimmed = text?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length >= 2 &&
                !trimmed.Contains("unk>", StringComparison.OrdinalIgnoreCase) &&
                IsValidText(trimmed))
            {
                // 已被新的录音会话取代时丢弃过期结果
                if (seq != _nextUploadSeq) return;
                UtteranceRecognized?.Invoke(trimmed);
                StatusChanged?.Invoke("🎤 语音识别中…");
            }
            else
            {
                // 未识别出有效文本 → 恢复初始状态
                if (seq == _nextUploadSeq)
                {
                    StatusChanged?.Invoke("🎤 语音识别中…");
                }
            }
        }
        catch (OperationCanceledException) { }   // 停止录音取消，静默
        catch (System.Net.Http.HttpRequestException ex)
        {
            ErrorOccurred?.Invoke($"❌ ASR 请求失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"❌ ASR 识别异常: {ex.Message}");
        }
    }

    /// <summary>验证识别文本是否为正常的中文或英文，丢弃乱码结果</summary>
    private static bool IsValidText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int validChars = 0;
        foreach (var c in text)
        {
            // 允许中文字符（含标点）
            if (c >= 0x4E00 && c <= 0x9FFF) validChars++;
            else if (c >= 0x3000 && c <= 0x303F) validChars++; // 中文标点
            else if (c >= 0xFF00 && c <= 0xFFEF) validChars++; // 全角字符
            else if (c >= 'a' && c <= 'z') validChars++;
            else if (c >= 'A' && c <= 'Z') validChars++;
            else if (c >= '0' && c <= '9') validChars++;
            else if (c == ' ' || c == '.' || c == ',' || c == '!' || c == '?' || c == '-' || c == '_') validChars++;
        }
        // 有效字符占比 >= 60% 才认为是正常文本
        return (double)validChars / text.Length >= 0.6;
    }

    /// <summary>float 采样(16kHz) → 16bit PCM WAV 字节</summary>
    private static byte[] BuildWav(float[] samples)
    {
        const int sampleRate = 16000;
        var dataLen = samples.Length * 2;
        var buf = new byte[44 + dataLen];

        WriteAscii(buf, 0, "RIFF");
        WriteInt32(buf, 4, 36 + dataLen);
        WriteAscii(buf, 8, "WAVE");
        WriteAscii(buf, 12, "fmt ");
        WriteInt32(buf, 16, 16);
        WriteInt16(buf, 20, 1);          // PCM
        WriteInt16(buf, 22, 1);          // 单声道
        WriteInt32(buf, 24, sampleRate);
        WriteInt32(buf, 28, sampleRate * 2);
        WriteInt16(buf, 32, 2);          // block align
        WriteInt16(buf, 34, 16);         // 位深
        WriteAscii(buf, 36, "data");
        WriteInt32(buf, 40, dataLen);

        for (var i = 0; i < samples.Length; i++)
        {
            var s = (short)Math.Max(-32768, Math.Min(32767, (int)(samples[i] * 32767.0f)));
            WriteInt16(buf, 44 + i * 2, s);
        }
        return buf;
    }

    private static void WriteAscii(byte[] buf, int offset, string s)
    {
        for (var i = 0; i < s.Length; i++) buf[offset + i] = (byte)s[i];
    }

    private static void WriteInt16(byte[] buf, int offset, short v)
    {
        buf[offset] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)(v >> 8);
    }

    private static void WriteInt32(byte[] buf, int offset, int v)
    {
        buf[offset] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)((v >> 8) & 0xFF);
        buf[offset + 2] = (byte)((v >> 16) & 0xFF);
        buf[offset + 3] = (byte)((v >> 24) & 0xFF);
    }

    // ========== 工具方法 ==========

    private static float[] BytesToFloats(byte[] buffer, int bytesRecorded)
    {
        var count = bytesRecorded / 2;
        var shorts = new short[count];
        Buffer.BlockCopy(buffer, 0, shorts, 0, bytesRecorded & ~1);
        var floats = new float[count];
        for (var i = 0; i < count; i++)
        {
            floats[i] = shorts[i] / 32768f;
        }
        return floats;
    }

    private void ResetState()
    {
        _nextUploadSeq = 0;
    }

    private void Cleanup()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.Dispose();
            _waveIn = null;
        }
        FinishPipeline();
        _isRecording = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { Stop(); } catch { }
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
