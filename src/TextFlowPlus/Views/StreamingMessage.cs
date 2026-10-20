using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TextFlowPlus.Views;

/// <summary>
/// AI 助手会话中的一条消息（可流式增量更新）。
///
/// 设计要点：
/// - <see cref="Body"/> 为正文（模型最终回答），<see cref="Thinking"/> 为思考过程（推理链）。
///   两者分开保存：正文走 Markdown 渲染，思考过程走等宽/次要色渲染，互不干扰。
/// - 流式期间由后台线程调用 <see cref="AppendBody"/> / <see cref="AppendThinking"/>，
///   通过 <see cref="Sink"/> 回到 UI 线程更新对应控件，避免整块重建导致闪烁与滚动跳动。
/// - <see cref="Commit"/> 表示流结束：此时把是否显示思考区的状态定稿（只有一个字的思考不显示）。
/// </summary>
internal sealed class StreamingMessage
{
    /// <summary>最小刷新间隔：模型分片可能极密集（每 token 一次），
    /// 高频重建可视树会拖垮 UI 线程，因此按时间片节流合并渲染。</summary>
    private static readonly TimeSpan MinRepaintInterval = TimeSpan.FromMilliseconds(60);

    private readonly StringBuilder _body = new();
    private readonly StringBuilder _thinking = new();
    private readonly object _gate = new();

    private DateTime _lastBodyPaint = DateTime.MinValue;
    private DateTime _lastThinkingPaint = DateTime.MinValue;
    private bool _bodyPaintScheduled;
    private bool _thinkingPaintScheduled;

    /// <summary>消息角色：user / assistant / system。</summary>
    public string Role { get; init; } = "assistant";

    /// <summary>是否撑满面板宽度（欢迎语、AI 回答为 true）。</summary>
    public bool Stretch { get; init; }

    /// <summary>
    /// UI 落地回调：由 AgentPanel 注入，负责把最新文本写入对应控件。
    /// 参数为 (正文, 思考过程)。回调始终在 UI 线程被调用。
    /// </summary>
    public Action<StreamingMessage>? Sink { get; set; }

    /// <summary>正文文本（模型回答）。</summary>
    public string Body
    {
        get { lock (_gate) return _body.ToString(); }
    }

    /// <summary>思考过程文本（推理链）。</summary>
    public string Thinking
    {
        get { lock (_gate) return _thinking.ToString(); }
    }

    /// <summary>是否曾经收到过思考内容（模型支持并实际输出了推理链）。</summary>
    public bool HasThinking { get; private set; }

    /// <summary>流是否已结束（结束后不再接受增量，UI 定稿）。</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>追加正文增量。</summary>
    public void AppendBody(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        lock (_gate)
        {
            if (IsCommitted) return;
            _body.Append(delta);
        }
        SchedulePaint(thinking: false);
    }

    /// <summary>追加思考过程增量。</summary>
    public void AppendThinking(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        lock (_gate)
        {
            if (IsCommitted) return;
            _thinking.Append(delta);
            HasThinking = true;
        }
        SchedulePaint(thinking: true);
    }

    /// <summary>整体设置正文（非流式回填场景）。</summary>
    public void SetBody(string? text)
    {
        lock (_gate)
        {
            if (IsCommitted) return;
            _body.Clear();
            if (!string.IsNullOrEmpty(text)) _body.Append(text);
        }
        SchedulePaint(thinking: false);
    }

    /// <summary>结束流式：强制做一次最终渲染，并标记定稿。</summary>
    public void Commit()
    {
        lock (_gate) IsCommitted = true;
        PostToUi(() => Sink?.Invoke(this));
    }

    /// <summary>
    /// 按时间片调度重绘：距上次绘制不足 <see cref="MinRepaintInterval"/> 时，
    /// 延迟到时间片结束再绘制一次，把密集分片合并成约 16fps 的可视更新。
    /// </summary>
    private void SchedulePaint(bool thinking)
    {
        var now = DateTime.UtcNow;
        var last = thinking ? _lastThinkingPaint : _lastBodyPaint;
        var scheduled = thinking ? _thinkingPaintScheduled : _bodyPaintScheduled;

        if (now - last >= MinRepaintInterval)
        {
            if (thinking) _lastThinkingPaint = now; else _lastBodyPaint = now;
            PostToUi(() => Sink?.Invoke(this));
            return;
        }

        if (scheduled) return;   // 已有一个待执行的补帧，避免堆积
        if (thinking) _thinkingPaintScheduled = true; else _bodyPaintScheduled = true;

        var delay = MinRepaintInterval - (now - last);
        DispatcherTimer.RunOnce(() =>
        {
            if (thinking) { _thinkingPaintScheduled = false; _lastThinkingPaint = DateTime.UtcNow; }
            else { _bodyPaintScheduled = false; _lastBodyPaint = DateTime.UtcNow; }
            Sink?.Invoke(this);
        }, delay);
    }

    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}