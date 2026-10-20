using System.ComponentModel;
using Avalonia.Media;

namespace TextFlowPlus.Models;

public class FileItem : INotifyPropertyChanged
{
    private string _fileName = "";
    private string _fileSize = "";
    private string _modifiedTime = "";
    private string _fullPath = "";
    private Color _highlightColor = Colors.Transparent;
    private bool _isFolder;

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(nameof(FileName)); }
    }

    public string FileSize
    {
        get => _fileSize;
        set { _fileSize = value; OnPropertyChanged(nameof(FileSize)); }
    }

    public string ModifiedTime
    {
        get => _modifiedTime;
        set { _modifiedTime = value; OnPropertyChanged(nameof(ModifiedTime)); }
    }

    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(nameof(FullPath)); }
    }

    public long Length { get; set; }

    public Color HighlightColor
    {
        get => _highlightColor;
        set { _highlightColor = value; OnPropertyChanged(nameof(HighlightColor)); }
    }

    public bool IsFolder
    {
        get => _isFolder;
        set { _isFolder = value; OnPropertyChanged(nameof(IsFolder)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
