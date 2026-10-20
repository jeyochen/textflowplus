using System;

namespace TextFlowPlus.Views;

/// <summary>
/// 工具标签页视图的“请求关闭”事件。
/// 视图内的取消/关闭按钮触发该事件，由主窗口关闭对应的标签页。
/// </summary>
public interface ICloseRequested
{
    event Action? CloseRequested;
}
