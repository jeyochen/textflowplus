using System.Collections.Generic;

namespace TextFlowPlus.Services.Ssh;

/// <summary>
/// 命令历史记录服务 - 支持上下键调取历史命令
/// </summary>
public class CommandHistoryService
{
    private readonly List<string> _history = new();
    private int _currentIndex = -1;
    private readonly int _maxHistory;

    public CommandHistoryService(int maxHistory = 100)
    {
        _maxHistory = maxHistory;
    }

    /// <summary>
    /// 添加命令到历史记录
    /// </summary>
    public void AddCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        // 避免重复添加相同的连续命令
        if (_history.Count > 0 && _history[_history.Count - 1] == command)
            return;

        _history.Add(command);

        // 限制历史记录数量
        while (_history.Count > _maxHistory)
        {
            _history.RemoveAt(0);
        }

        _currentIndex = -1;
    }

    /// <summary>
    /// 获取上一条历史命令
    /// </summary>
    public string? GetPrevious()
    {
        if (_history.Count == 0) return null;

        if (_currentIndex < 0)
        {
            _currentIndex = _history.Count - 1;
        }
        else
        {
            _currentIndex--;
            if (_currentIndex < 0) _currentIndex = 0;
        }

        return _history[_currentIndex];
    }

    /// <summary>
    /// 获取下一条历史命令
    /// </summary>
    public string? GetNext()
    {
        if (_history.Count == 0 || _currentIndex < 0) return null;

        _currentIndex++;
        if (_currentIndex >= _history.Count)
        {
            _currentIndex = -1;
            return ""; // 返回空字符串表示回到输入新命令的状态
        }

        return _history[_currentIndex];
    }

    /// <summary>
    /// 清空历史记录
    /// </summary>
    public void Clear()
    {
        _history.Clear();
        _currentIndex = -1;
    }

    /// <summary>
    /// 获取所有历史记录
    /// </summary>
    public IReadOnlyList<string> GetAll()
    {
        return _history.AsReadOnly();
    }
}
