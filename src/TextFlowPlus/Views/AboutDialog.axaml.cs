using Avalonia.Controls;
using TextFlowPlus.Services.Localization;

namespace TextFlowPlus.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        OkButton.Click += (s, e) => Close();
        // 界面语言：打开时按当前语言刷新静态文本
        Title = LangService.T("关于 TextFlowPlus");
        AppNameText.Text = "TextFlowPlus";
        DescText.Text = LangService.T("跨平台文本/代码编辑器");
        AuthorText.Text = LangService.T("作者：红尘墨客");
        FeaturesTitle.Text = LangService.T("功能特性：");
        Feature1Text.Text = LangService.T("• 多标签页编辑 • 多种语言语法高亮 • 编码自动检测");
        Feature2Text.Text = LangService.T("• 查找/替换 • JSON/YAML/XML 格式化 • HTTP 调试");
        Feature3Text.Text = LangService.T("• 文件/文件夹对比 • 二进制文件支持 • 大文件支持");
        OkButton.Content = LangService.T("确定");
    }
}
