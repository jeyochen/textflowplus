using System;
using System.Collections.Generic;
using System.Linq;

namespace TextFlowPlus.Services.Voice;

/// <summary>
/// 腾讯 Ten VAD 动态库后端：P/Invoke 直调 ten_vad.dll（x64），无需 onnx 模型。
/// 逐帧（hop_size=256，16ms@16kHz）喂入返回说话概率/标志；内部累积语音段：
/// 帧为说话 → 打开/继续段，静音连续 ≥ minSilenceDuration → 闭段入队。
/// 每段起点向前回退 preRollMilliseconds，覆盖 VAD 起判延迟，避免开头截字。
/// 采样存于 float 全量缓冲，段弹出返回 float[]（16kHz 单声道）。
/// </summary>
public sealed class TenVadVad : IDisposable
{
    private readonly object _lock = new();
    private readonly IntPtr _vad;   // ten_vad_handle_t
    private readonly TenVadNative _native;
    private bool _disposed;

    public int SampleRate { get; }
    public int HopSamples { get; }
    public int MinSpeechSamples { get; }

    private readonly int _minSilenceSamples;
    private readonly int _minSpeechSamplesSound;
    private readonly int _preRollSamples;
    private readonly int _maxSpeechSamples;

    // 全量 float 采样缓冲（16kHz 单声道），VAD 帧边界推进
    private readonly List<float> _buf = new();

    // 段状态
    private bool _inSpeech;
    private int _dispatch;            // _buf 中已消费（已弹出）的位置
    private int _segStart;            // 当前段起点（含 pre-roll 回退）
    private int _silenceLen;          // 说话中累计静音采样数
    private double _segSumSq;         // 段内采样平方和
    private int _segSumCount;
    private double _segPeak;          // 段内采样绝对峰值
    private long _segWinIdx;          // 段内窗口序号
    private double _segWinSumSq;      // 当前窗口平方和
    private double[] _segWinRms = Array.Empty<double>();   // 各窗口 RMS

    /// <summary>段 RMS 低于该阈值时丢弃该段（绝对能量底线，0 禁用）。</summary>
    public float MinRms { get; }

    /// <summary>段峰值因子 (peak/rms) 低于该值且窗口能量平稳时丢弃（识别无人声噪声）。</summary>
    public float MinPeakRatio { get; }

    /// <summary>窗口能量变异系数低于该值视为平稳（无人声噪声特征）。</summary>
    public float MaxEnergyCv { get; }

    // 待弹出段
    private readonly Queue<float[]> _segments = new();

    /// <summary>ten_vad.dll 加载路径。默认 "ten_vad"（Windows 下按进程目录解析）。</summary>
    public string DllPath { get; }

    public TenVadVad(
        string? dllPath = null,
        int sampleRate = 16000,
        int hopSamples = 256,
        float threshold = 0.75f,
        float minSilenceDuration = 0.5f,
        float minSpeechDuration = 0.5f,
        float maxSpeechDuration = 30f,
        int preRollMilliseconds = 300,
        float minRms = 0.006f,
        float minPeakRatio = 3.0f,
        float maxEnergyCv = 0.2f)
    {
        SampleRate = sampleRate;
        HopSamples = hopSamples;
        _minSilenceSamples = (int)(minSilenceDuration * sampleRate);
        _minSpeechSamplesSound = (int)(minSpeechDuration * sampleRate);
        _preRollSamples = sampleRate * preRollMilliseconds / 1000;
        _maxSpeechSamples = (int)(maxSpeechDuration * sampleRate);
        MinSpeechSamples = _minSpeechSamplesSound;
        MinRms = minRms;
        MinPeakRatio = minPeakRatio;
        MaxEnergyCv = maxEnergyCv;
        DllPath = dllPath ?? "ten_vad";

        _native = new TenVadNative(DllPath);
        _vad = _native.Create(out var h, (UIntPtr)hopSamples, threshold) == 0 && h != IntPtr.Zero
            ? h
            : throw new InvalidOperationException("ten_vad_create 失败");
    }

    // ---------- 音频线程调用 ----------

    /// <summary>喂入一块 16kHz 单声道 float 音频（内部按 HopSamples 逐帧处理）。</summary>
    public void VadAccept(float[] window)
    {
        lock (_lock)
        {
            AppendToBuf(window);
            // 逐帧处理（每帧 HopSamples），不足一帧残留到下次
            while (_buf.Count - _dispatch >= HopSamples)
            {
                var frameStart = _dispatch;
                var frame = new short[HopSamples];
                for (var i = 0; i < HopSamples; i++)
                {
                    var v = _buf[frameStart + i];
                    frame[i] = (short)Math.Max(-32768, Math.Min(32767, (int)(v * 32767f)));
                }

                var prob = 0f;
                var flag = 0;
                var rc = _native.Process(_vad, frame, (UIntPtr)HopSamples, ref prob, ref flag);
                if (rc != 0)
                {
                    System.Diagnostics.Trace.WriteLine("ten_vad_process rc=" + rc);
                    _dispatch += HopSamples;
                    continue;
                }

                OnFrame(flag != 0);
                _dispatch += HopSamples;
            }
        }
    }

    public bool VadIsEmpty()
    {
        lock (_lock)
        {
            return _segments.Count == 0;
        }
    }

    /// <summary>弹出一段语音样本（float 16kHz 单声道）。</summary>
    public float[] VadPopSegment()
    {
        lock (_lock)
        {
            return _segments.Count == 0 ? Array.Empty<float>() : _segments.Dequeue();
        }
    }

    /// <summary>停止时冲刷剩余语音，把尾部未闭段也产出。</summary>
    public void VadFlush()
    {
        lock (_lock)
        {
            if (_inSpeech)
            {
                PushSegmentIfEnough();
                _inSpeech = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_vad != IntPtr.Zero)
            {
                IntPtr h = _vad;
                _native.Destroy(ref h);
            }
            _native.Free();
        }
    }

    // ---------- internal ----------

    private void OnFrame(bool frameSpeech)
    {
        // 段开启时累积能量、峰值、窗口能量（无人声噪声过滤用）
        if (_inSpeech)
        {
            var s = _dispatch;
            var e = Math.Min(_dispatch + HopSamples, _buf.Count);
            for (var i = s; i < e; i++)
            {
                var v = _buf[i];
                _segSumSq += v * v;
                var a = Math.Abs(v);
                if (a > _segPeak) _segPeak = a;
                _segWinSumSq += v * v;
            }
            _segSumCount += Math.Max(0, e - s);
            // 每 200ms(3200 采样)结算一个窗口
            if (_segSumCount - _segWinIdx * SegWindowSamples >= SegWindowSamples)
            {
                _segWinIdx++;
                if (_segWinIdx >= _segWinRms.Length && _segWinRms.Length < 512)
                {
                    Array.Resize(ref _segWinRms, Math.Max(16, _segWinRms.Length * 2));
                }
                if (_segWinIdx - 1 < _segWinRms.Length)
                {
                    _segWinRms[_segWinIdx - 1] = Math.Sqrt(_segWinSumSq / SegWindowSamples);
                }
                _segWinSumSq = 0;
            }
        }

        if (frameSpeech)
        {
            if (!_inSpeech)
            {
                _inSpeech = true;
                _silenceLen = 0;
                _segSumSq = 0;
                _segSumCount = 0;
                _segPeak = 0;
                _segWinIdx = 0;
                _segWinSumSq = 0;
                _segWinRms = new double[16];
                // 段起点：当前帧起点向前回退 pre-roll（可含 VAD 起判延迟）
                var frameStart = _dispatch;
                _segStart = Math.Max(0, frameStart - _preRollSamples);
            }
            else
            {
                _silenceLen = 0;
            }
        }
        else
        {
            if (_inSpeech)
            {
                _silenceLen += HopSamples;
                if (_silenceLen >= _minSilenceSamples)
                {
                    PushSegmentIfEnough();
                    _inSpeech = false;
                }
            }
        }

        // 段超长强制闭段
        if (_inSpeech && _dispatch - _segStart >= _maxSpeechSamples)
        {
            PushSegmentIfEnough();
            _inSpeech = false;
        }
    }

    /// <summary>窗口长度: 200ms@16kHz。</summary>
    private const int SegWindowSamples = 3200;

    /// <summary>是否判定为无人声噪声段：峰值因子低且窗口能量平稳。</summary>
    private bool IsNoiseLikeSegment(double segRms)
    {
        if (MinRms > 0 && segRms < MinRms) return true;   // 绝对能量底线

        if (MinPeakRatio > 0 && _segWinIdx >= 2)
        {
            var peakRatio = segRms > 0 ? _segPeak / segRms : 0;
            // 窗口能量变异系数
            var mean = _segWinRms.Take((int)_segWinIdx).Average();
            if (mean > 0)
            {
                var variance = _segWinRms.Take((int)_segWinIdx).Sum(v => (v - mean) * (v - mean)) / _segWinIdx;
                var cv = Math.Sqrt(variance) / mean;
                if (peakRatio < MinPeakRatio && cv < MaxEnergyCv)
                {
                    return true;   // 平稳低峰 → 无人声噪声
                }
            }
        }
        return false;
    }

    /// <summary>把当前段推入队列（无人声噪声则丢弃）并截断缓冲。</summary>
    private void PushSegmentIfEnough()
    {
        var len = _dispatch - _segStart;
        if (len < _minSpeechSamplesSound) return;

        if (_segSumCount > 0)
        {
            var rms = Math.Sqrt(_segSumSq / _segSumCount);
            if (IsNoiseLikeSegment(rms))
            {
                _buf.RemoveRange(0, _dispatch);
                _dispatch = 0;
                _segSumSq = 0;
                _segSumCount = 0;
                _segPeak = 0;
                return;
            }
        }

        var seg = new float[len];
        _buf.CopyTo(_segStart, seg, 0, len);
        _segments.Enqueue(seg);

        // 消费到段尾：丢弃已处理数据（保留未处理残留起点）
        _buf.RemoveRange(0, _segStart + len);
        _dispatch = 0;
        _segSumSq = 0;
        _segSumCount = 0;
        _segPeak = 0;
    }

    private void AppendToBuf(float[] window)
    {
        for (var i = 0; i < window.Length; i++) _buf.Add(window[i]);
    }
}
