using Avalonia.Controls;

namespace VideoSecurityPlayer.Views.SecretVideoPlayer;

/// <summary>
/// 批量解密视图；只拥有窗口级文件/目录选择能力，队列状态全部属于当前 Document。
/// </summary>
public partial class VideoDecryptorView : UserControl
{
    public VideoDecryptorView()
    {
        InitializeComponent();
    }
}
