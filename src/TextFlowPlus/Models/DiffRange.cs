namespace TextFlowPlus.Models;

public enum DiffType
{
    Insert,
    Delete,
    Modified
}

public class DiffRange
{
    public int LeftStart { get; set; }
    public int LeftEnd { get; set; }
    public int RightStart { get; set; }
    public int RightEnd { get; set; }
    public DiffType DiffType { get; set; }
    public int Index { get; set; }
}

public class FolderDiffRange
{
    public string FileName { get; set; } = "";
    public DiffType DiffType { get; set; }
    public string? LeftPath { get; set; }
    public string? RightPath { get; set; }
    public long LeftLength { get; set; }
    public long RightLength { get; set; }
}
