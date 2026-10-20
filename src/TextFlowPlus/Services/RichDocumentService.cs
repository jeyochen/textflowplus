using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Documents.Serialization;
using Avalonia.Controls.Documents.Serialization.Docx;
using Avalonia.Controls.Documents.Serialization.Rtf;

namespace TextFlowPlus.Services;

/// <summary>
/// 富文本文档读写服务：加载/保存 Word(.docx/.doc) 与 RTF 文档为 FlowDocument，
/// 构建图片查看文档，并提供 .doc 老格式与 .docx 之间的自动转换（LibreOffice / Word COM）。
/// </summary>
public static class RichDocumentService
{
    private const string RootCacheDirName = "TextFlowPlus";

    /// <summary>docx（Word Open XML）</summary>
    public static readonly string[] WordOpenXmlExts = { ".docx", ".docm", ".dotx" };

    /// <summary>老式二进制 Word</summary>
    public static readonly string[] WordLegacyExts = { ".doc", ".dot" };

    /// <summary>RTF 富文本</summary>
    public static readonly string[] RtfExts = { ".rtf" };

    /// <summary>图片只读查看</summary>
    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    /// <summary>所有走富文本模式的扩展名</summary>
    public static readonly string[] RichTextExts = WordOpenXmlExts.Concat(WordLegacyExts).Concat(RtfExts).ToArray();

    /// <summary>是否为富文本文档扩展名（docx/docm/dotx/doc/dot/rtf）</summary>
    public static bool IsRichDocument(string extension) =>
        WordOpenXmlExts.Contains(extension) || WordLegacyExts.Contains(extension) || RtfExts.Contains(extension);

    /// <summary>是否为图片扩展名</summary>
    public static bool IsImage(string extension) => ImageExts.Contains(extension);

    /// <summary>缺少 Avalonia Pro 许可证 key 引发的异常（序列化器静态构造函数硬性检查）。</summary>
    public sealed class AvaloniaLicenseRequiredException : Exception
    {
        public AvaloniaLicenseRequiredException() : base(LicenseErrorMessage) { }
    }

    /// <summary>许可证缺失时的用户指引文案。</summary>
    public const string LicenseErrorMessage =
        "此功能需要 Avalonia Pro 许可证（.docx/.rtf 序列化器来自 Avalonia Pro 控件集）。\n\n" +
        "个人/非商业用途可在 Avalonia Portal 免费申请 Community License Key：\n" +
        "https://avaloniaui.net/portal\n\n" +
        "申请后在 TextFlowPlus.csproj 中添加（替换为你的 key）：\n" +
        "<AvaloniaUILicenseKey Include=\"AAL-XXXX-XXXX-XXXX\" />\n\n" +
        "配置完成后重新构建即可打开/保存 Word 与 RTF 文档。";

    /// <summary>判断异常链中是否包含 AvaloniaUI 许可证缺失错误。</summary>
    public static bool IsLicenseError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("AvaloniaUI license key", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 加载文档为 FlowDocument（docx 用 DocxSerializer、rtf 用 RtfSerializer）。
    /// 由调用方在后台线程执行；返回的文档可直接赋给 RichTextEditor.Document。
    /// 缺少 Avalonia Pro 许可证 key 时抛出 <see cref="AvaloniaLicenseRequiredException"/>。
    /// </summary>
    public static async Task<FlowDocument> LoadDocumentAsync(string path, CancellationToken ct = default)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            IDocumentSerializer serializer = ext switch
            {
                ".rtf" => new RtfSerializer(),
                _ => new DocxSerializer(),
            };

            await using var stream = File.OpenRead(path);
            return await FlowDocument.LoadAsync(stream, serializer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLicenseError(ex))
        {
            throw new AvaloniaLicenseRequiredException();
        }
    }

    /// <summary>
    /// 用对应的格式保存 FlowDocument。docx → DocxSerializer（图片以内嵌字节往返），rtf → RtfSerializer。
    /// 缺少 Avalonia Pro 许可证 key 时抛出 <see cref="AvaloniaLicenseRequiredException"/>。
    /// </summary>
    public static async Task SaveDocumentAsync(FlowDocument doc, string path, CancellationToken ct = default)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            IDocumentSerializer serializer = ext switch
            {
                ".rtf" => new RtfSerializer(),
                _ => new DocxSerializer(),
            };

            await using var stream = File.Create(path);
            await doc.SaveAsync(stream, serializer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLicenseError(ex))
        {
            throw new AvaloniaLicenseRequiredException();
        }
    }

    /// <summary>取图片文件的 MIME 类型（保存/内嵌用）。</summary>
    public static string GetImageMimeType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/x-icon",
    };

    /// <summary>
    /// 构建单图查看文档：图片居中显示，保留长宽比（最大宽度 640dip，超高时按比例缩放）。
    /// </summary>
    public static FlowDocument BuildImageViewerDocument(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string mime = GetImageMimeType(ext);

        var image = new RichImage(bytes, mime)
        {
            AltText = Path.GetFileName(path),
        };

        // 读取原始像素尺寸，等比缩放到可读大小（避免超大图无限撑开）
        if (image.Source is RichImageSource source && source.TryGetIntrinsicPixelSize(out int w, out int h) && w > 0 && h > 0)
        {
            const double maxWidth = 640;
            if (w > maxWidth)
            {
                image.Width = maxWidth;
                image.Height = h * maxWidth / w;
            }
        }

        var paragraph = new Paragraph
        {
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        };
        paragraph.Inlines.Add(image);

        var doc = new FlowDocument();
        doc.Blocks.Add(paragraph);
        return doc;
    }

    /// <summary>.doc 转换缓存目录（%TEMP%\TextFlowPlus\doc-cache）</summary>
    public static string DocCacheDirectory
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), RootCacheDirName, "doc-cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>为 .doc/.dot 源文件生成唯一缓存 docx 路径。</summary>
    public static string GetDocCachePath(string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(DocCacheDirectory, $"{name}_{DateTime.Now.Ticks}.docx");
    }

    /// <summary>
    /// 把 Word 文档（当前 .doc/.dot）转换为 docx。优先 LibreOffice（跨平台），Windows 可用时回退 Word COM。
    /// </summary>
    public static bool TryConvertToDocx(string sourcePath, string targetDocxPath)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext == ".docx") return CopyCore(sourcePath, targetDocxPath);

        var tool = FindConverterTool();
        return tool switch
        {
            ConverterTool.LibreOffice => ConvertWithLibreOffice(sourcePath, targetDocxPath, "docx"),
            ConverterTool.WordCom => ConvertWithWordCom(sourcePath, targetDocxPath, dxFileFormat: WordComFileFormats.DocxXml),
            _ => false,
        };
    }

    /// <summary>
    /// 把 docx 缓存转回 .doc/.dot 原格式（保存时回写）。失败返回 false（内容仍保存在 docx 缓存中）。
    /// </summary>
    public static bool TryConvertToLegacyDoc(string sourceDocxPath, string targetDocPath)
    {
        var tool = FindConverterTool();
        return tool switch
        {
            ConverterTool.LibreOffice => ConvertWithLibreOffice(sourceDocxPath, targetDocPath, "doc:\"MS Word 97\""),
            ConverterTool.WordCom => ConvertWithWordCom(sourceDocxPath, targetDocPath, dxFileFormat: WordComFileFormats.Doc),
            _ => false,
        };
    }

    /// <summary>是否有可用的转换工具（决定 .doc 标签页能否编辑保存）。</summary>
    public static bool HasConverterAvailable() => FindConverterTool() != ConverterTool.None;

    private enum ConverterTool { None, LibreOffice, WordCom }

    private static bool CopyCore(string source, string target)
    {
        try { File.Copy(source, target, overwrite: true); return true; }
        catch { return false; }
    }

    /// <summary>检测可用转换工具：LibreOffice（soffice）优先，其次 Windows Word COM。</summary>
    private static ConverterTool FindConverterTool()
    {
        if (FindLibreOfficePath() != null) return ConverterTool.LibreOffice;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Type.GetTypeFromProgID("Word.Application") != null)
            return ConverterTool.WordCom;
        return ConverterTool.None;
    }

    // Word COM 文件格式常量（Microsoft.Office.Interop.Word.WdSaveFormat）：文档(doc)=0，XML(docx)=12，RTF=6
    internal static class WordComFileFormats
    {
        public const int Doc = 0;
        public const int Rtf = 6;
        public const int DocxXml = 12;
    }

    /// <summary>查找 LibreOffice 可执行文件（PATH + 常见安装路径）。</summary>
    public static string? FindLibreOfficePath()
    {
        var candidates = new List<string>();

        // PATH 中的 soffice / soffice.exe
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                AddIfExists(candidates, Path.Combine(dir, "soffice.exe"));
                AddIfExists(candidates, Path.Combine(dir, "soffice"));
            }
        }

        // 常见安装位置
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AddIfExists(candidates, @"C:\Program Files\LibreOffice\program\soffice.exe");
            AddIfExists(candidates, @"C:\Program Files (x86)\LibreOffice\program\soffice.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            AddIfExists(candidates, "/Applications/LibreOffice.app/Contents/MacOS/soffice");
        }
        else
        {
            AddIfExists(candidates, "/usr/bin/soffice");
            AddIfExists(candidates, "/usr/local/bin/soffice");
        }

        return candidates.FirstOrDefault();
    }

    private static void AddIfExists(List<string> list, string path)
    {
        try { if (File.Exists(path) && !list.Contains(path)) list.Add(path); } catch { }
    }

    /// <summary>
    /// LibreOffice headless 转换：soffice --headless --convert-to 目标格式(过滤器) --outdir 输出目录 源文件。
    /// 输出文件名 = 源文件名（扩展名替换为目标扩展名）。转换结束前进程可能已退出但文件尚在写，
    /// 轮询等待目标文件出现（最长 30 秒）。
    /// </summary>
    private static bool ConvertWithLibreOffice(string sourcePath, string targetPath, string convertArg)
    {
        string? soffice = FindLibreOfficePath();
        if (soffice == null) return false;

        try
        {
            string outDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(outDir);

            string expectedName = Path.GetFileNameWithoutExtension(sourcePath) + Path.GetExtension(targetPath);
            string expected = Path.Combine(outDir, expectedName);

            // 先删残留，避免把旧文件误判为转换成功
            if (File.Exists(expected)) File.Delete(expected);
            if (File.Exists(targetPath)) File.Delete(targetPath);

            var psi = new ProcessStartInfo(soffice, $"--headless --convert-to {convertArg} --outdir \"{outDir}\" \"{sourcePath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            // 轮询等待产物出现（LibreOffice 首次启动较慢）
            for (int i = 0; i < 60; i++)
            {
                if (proc != null && proc.HasExited && File.Exists(expected)) break;
                if (File.Exists(expected)) break;
                Thread.Sleep(500);
            }

            if (!File.Exists(expected)) return false;
            if (expected != targetPath) File.Copy(expected, targetPath, overwrite: true);
            return File.Exists(targetPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Windows Word COM 转换：Word.Application → Documents.Open(源) → SaveAs2(目标, 格式)。
    /// 通过 ProgID 动态绑定，无需 Interop 程序集。失败时确保释放 COM 对象。
    /// </summary>
    private static bool ConvertWithWordCom(string sourcePath, string targetPath, int dxFileFormat)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        object? word = null, docs = null, document = null;
        try
        {
            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null) return false;

            word = Activator.CreateInstance(wordType);
            if (word == null) return false;

            wordType.InvokeMember("Visible", System.Reflection.BindingFlags.SetProperty, null, word, new object[] { false });
            docs = wordType.InvokeMember("Documents", System.Reflection.BindingFlags.GetProperty, null, word, null);
            Type? docsType = docs?.GetType();

            // Open 可能弹“转换/修复”对话框：DisplayAlerts=false 抑制
            wordType.InvokeMember("DisplayAlerts", System.Reflection.BindingFlags.SetProperty, null, word, new object[] { 0 });

            var docArgs = new object[] { sourcePath, false, true, false };
            document = docsType?.InvokeMember("Open", System.Reflection.BindingFlags.InvokeMethod, null, docs, docArgs);
            Type? docType = document?.GetType();

            var saveArgs = new object[] { targetPath, dxFileFormat };
            docType?.InvokeMember("SaveAs2", System.Reflection.BindingFlags.InvokeMethod, null, document, saveArgs);
            return File.Exists(targetPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { document?.GetType()?.InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, document, new object[] { false, Type.Missing, Type.Missing }); } catch { }
            try { word?.GetType()?.InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, word, null); } catch { }
            if (document != null) Marshal.FinalReleaseComObject(document);
            if (docs != null) Marshal.FinalReleaseComObject(docs);
            if (word != null) Marshal.FinalReleaseComObject(word);
        }
    }
}
