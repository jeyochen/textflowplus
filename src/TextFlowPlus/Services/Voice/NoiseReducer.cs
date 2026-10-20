using System;
using RNNoise.NET;

namespace TextFlowPlus.Services.Voice;

/// <summary>
/// RNNoise 降噪 + 48k→16k 降采样（参考 MachineAgent 的实现）。
/// 采集 48kHz → RNNoise(48k, 480 采样/帧)逐帧降噪 → 线性插值降采样到 16kHz 输出。
/// 输入采样率必须为 48kHz（RNNoise 固定 48k）。
/// </summary>
public sealed class NoiseReducer : IDisposable
{
    private const int InSampleRate = 48000;
    private const int OutSampleRate = 16000;

    private readonly Denoiser _denoiser = new();
    private readonly float[] _frame = new float[480];
    private int _frameFill;

    /// <summary>输入浮点流(48kHz) → 输出降噪后的 16kHz 音频；就地返回新数组。</summary>
    public float[] Process(float[] in48k)
    {
        // 1) 逐帧喂 RNNoise (480 采样)
        var outCollected = new System.Collections.Generic.List<float>(in48k.Length / 3);
        var pos = 0;
        while (pos < in48k.Length)
        {
            var toCopy = Math.Min(480 - _frameFill, in48k.Length - pos);
            Array.Copy(in48k, pos, _frame, _frameFill, toCopy);
            _frameFill += toCopy;
            pos += toCopy;

            if (_frameFill >= 480)
            {
                // 降噪一帧 (就地)
                _denoiser.Denoise(new Span<float>(_frame), finish: false);
                // 3:1 降采样到 16k: 取每第 3 个(相位 0)
                for (var i = 0; i < 480; i += 3)
                {
                    outCollected.Add(_frame[i]);
                }
                _frameFill = 0;
            }
        }
        return outCollected.ToArray();
    }

    /// <summary>结束时冲刷残留半帧（丢弃不足 480 的尾巴，避免伪影）。</summary>
    public void Flush()
    {
        _frameFill = 0;
    }

    public void Dispose()
    {
        _denoiser.Dispose();
    }
}
