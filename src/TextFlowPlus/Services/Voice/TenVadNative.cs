using System;
using System.Runtime.InteropServices;

namespace TextFlowPlus.Services.Voice;

/// <summary>ten_vad.dll 动态加载（P/Invoke 需 DLL 在进程目录，这里支持任意绝对路径）。</summary>
internal sealed class TenVadNative
{
    private readonly IntPtr _lib;

    internal TenVadNative(string dllPath)
    {
        _lib = NativeLibrary.Load(dllPath);
        GetProcs();
    }

    private delegate int DCreate(out IntPtr handle, UIntPtr hop_size, float threshold);
    private delegate int DProcess(IntPtr handle, [In] short[] audio_data, UIntPtr len, ref float out_probability, ref int out_flag);
    private delegate int DDestroy(ref IntPtr handle);
    private delegate IntPtr DGetVersion();

    private DCreate _create = null!;
    private DProcess _process = null!;
    private DDestroy _destroy = null!;
    private DGetVersion _getVersion = null!;

    private T GetProc<T>(string name) where T : Delegate
    {
        var p = NativeLibrary.GetExport(_lib, name);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"缺少导出 {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private void GetProcs()
    {
        _create = GetProc<DCreate>("ten_vad_create");
        _process = GetProc<DProcess>("ten_vad_process");
        _destroy = GetProc<DDestroy>("ten_vad_destroy");
        _getVersion = GetProc<DGetVersion>("ten_vad_get_version");
    }

    internal int Create(out IntPtr handle, UIntPtr hop, float threshold) => _create(out handle, hop, threshold);
    internal int Process(IntPtr h, short[] data, UIntPtr len, ref float prob, ref int flag) => _process(h, data, len, ref prob, ref flag);
    internal int Destroy(ref IntPtr h) => _destroy(ref h);
    internal string Version { get { var p = _getVersion(); return p == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(p) ?? "?"; } }

    internal void Free()
    {
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }
}
