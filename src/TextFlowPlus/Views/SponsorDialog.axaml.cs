using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

/// <summary>
/// 赞助窗口：并排展示微信支付与支付宝收款二维码。
/// 二维码图片以 Avalonia 资源方式嵌入程序集（见 csproj 中的 AvaloniaResource 项），
/// 运行时通过 avares://TextFlowPlus/Assets/xxx.jpg 加载；资源缺失时仅显示文字提示，不抛异常。
/// </summary>
public partial class SponsorDialog : Window
{
    private readonly bool _wechatLoaded;
    private readonly bool _alipayLoaded;

    public SponsorDialog()
    {
        InitializeComponent();

        CloseButton.Click += (s, e) => Close();

        WeChatImage.Source = LoadQrCode("Assets/wechat.jpg");
        _wechatLoaded = WeChatImage.Source != null;
        AlipayImage.Source = LoadQrCode("Assets/alipay.jpg");
        _alipayLoaded = AlipayImage.Source != null;

        ApplyLanguage();
        LangService.LanguageChanged += ApplyLanguage;
        Closed += (s, e) => LangService.LanguageChanged -= ApplyLanguage;
    }

    /// <summary>加载嵌入的二维码图片；失败返回 null（界面给出文字提示，不影响窗口打开）。</summary>
    private static Bitmap? LoadQrCode(string relativePath)
    {
        try
        {
            var uri = new Uri($"avares://TextFlowPlus/{relativePath}");
            if (!AssetLoader.Exists(uri)) return null;
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按当前界面语言刷新文本（中文 / English / 日本語）。</summary>
    private void ApplyLanguage()
    {
        Title = LangService.T("赞助 TextFlowPlus");
        TitleText.Text = LangService.T("赞助 TextFlowPlus");
        DescText.Text = LangService.T("如果 TextFlowPlus 对你有帮助，欢迎扫码请作者喝杯咖啡");
        WeChatTitle.Text = LangService.T("微信支付");
        AlipayTitle.Text = LangService.T("支付宝");
        WeChatHint.Text = LangService.T(_wechatLoaded ? "打开微信扫一扫" : "二维码加载失败");
        AlipayHint.Text = LangService.T(_alipayLoaded ? "打开支付宝扫一扫" : "二维码加载失败");
        CloseButton.Content = LangService.T("关闭");
    }
}
