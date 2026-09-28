using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>把本插件私有 LibVLCSharp 的原生导入固定到已检查的目录，不改变进程 PATH。</summary>
/// <remarks>
/// 不共享播放器、密码或设置。绑定句柄与托管程序集同寿命，关闭某个文档不能 Free；
/// 原生解析器无法注销，因此从首次尝试开始冻结目录，失败后也必须重启。
/// </remarks>
internal static class LibVlcNativeBinding
{
    private static readonly object Sync = new();
    private static string? _directory;
    private static nint _core;
    private static nint _vlc;
    private static bool _ready;
    public static string Version { get; private set; } = "";

    public static void Initialize(string directory)
    {
        lock (Sync)
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (_directory is not null)
            {
                if (_ready && string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException("此程序集已尝试绑定原生库，请重启应用。");
            }
            _directory = directory;
            var registered = false;
            try
            {
                _core = NativeLibrary.Load(Path.Combine(directory, "libvlccore.dll"));
                VerifySource(_core, Path.Combine(directory, "libvlccore.dll"));
                _vlc = NativeLibrary.Load(Path.Combine(directory, "libvlc.dll"));
                VerifySource(_vlc, Path.Combine(directory, "libvlc.dll"));
                NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, Resolve);
                registered = true;
                Core.Initialize(directory);
                var getVersion = Marshal.GetDelegateForFunctionPointer<GetVersion>(NativeLibrary.GetExport(_vlc, "libvlc_get_version"));
                Version = Marshal.PtrToStringUTF8(getVersion()) ?? "未知";
                _ready = true;
            }
            catch
            {
                // 解析器登记后句柄已被托管导入引用，不能释放；登记前只释放本次取得的引用。
                if (!registered)
                {
                    if (_vlc != 0) NativeLibrary.Free(_vlc);
                    if (_core != 0) NativeLibrary.Free(_core);
                    _vlc = _core = 0;
                }
                throw;
            }
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? flags) => name switch
    { "libvlc" or "libvlc.dll" => _vlc, "libvlccore" or "libvlccore.dll" => _core, _ => 0 };

    private static void VerifySource(nint handle, string expected)
    {
        var actual = new StringBuilder(32768);
        if (GetModuleFileName(handle, actual, actual.Capacity) == 0 ||
            !string.Equals(Path.GetFullPath(actual.ToString()), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原生库的实际加载来源与所选目录不一致。");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetVersion();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(nint module, StringBuilder path, int size);
}
