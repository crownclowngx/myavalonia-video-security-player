using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace VideoSecurityPlayer.Standalone;

/// <summary>仅开发 smoke 使用的隔离装载适配器；产品插件没有音乐项目引用。</summary>
/// <remarks>
/// 从显式目录加载音乐插件及其私有 LibVLCSharp，实际调用音乐的 Runtime 与 AudioOutput。
/// 反射限制在这一测试边界，避免为测试把音乐内部类型改成公开 API 或污染产品依赖。
/// 不装载音乐账号、网络服务和用户配置；全部配置与媒体均位于本轮隔离数据目录。
/// </remarks>
internal sealed class S1MusicCoexistence : IDisposable
{
    private readonly AssemblyLoadContext _context;
    private readonly object _runtime;
    private readonly object _output;
    private readonly string _wave;
    public string ActiveDirectory => Read(_runtime, "ActiveDirectory")?.ToString() ?? "";
    public string ActiveVersion => Read(_runtime, "ActiveVersion")?.ToString() ?? "";
    public long Position => Player is { } player ? (long)(Read(player, "Time") ?? 0L) : 0;
    private object? Player => _output.GetType().GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_output);

    public S1MusicCoexistence(string pluginDirectory, string runtimeDirectory, string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "playback-settings.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, customDirectory = runtimeDirectory }));
        _context = new PrivateMusicContext(pluginDirectory);
        var assembly = _context.LoadFromAssemblyPath(Path.Combine(pluginDirectory, "MusicNetEasePlugin.Plugin.dll"));
        object Create(string name, params object?[] args) => Activator.CreateInstance(assembly.GetType(name, true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;
        var store = Create("MusicNetEasePlugin.Infrastructure.Persistence.LibVlcSettingsStore", dataRoot);
        var probe = Create("MusicNetEasePlugin.Infrastructure.Audio.LibVlcDirectoryProbe");
        var resolver = Create("MusicNetEasePlugin.Infrastructure.Audio.LibVlcRuntimeResolver", store, probe, Path.Combine(dataRoot, "no-bundled-runtime"));
        _runtime = Create("MusicNetEasePlugin.Infrastructure.Audio.LibVlcRuntime", resolver);
        _output = Create("MusicNetEasePlugin.Infrastructure.Audio.LibVlcAudioOutput", _runtime, null);
        _wave = Path.Combine(dataRoot, "coexistence-tone.wav");
        WriteWave(_wave);
    }

    public async Task StartAsync()
    {
        await (Task)_output.GetType().GetMethod("OpenAsync")!.Invoke(_output, [_wave, 1L, CancellationToken.None, 0L])!;
        var bridge = Player!.GetType().Assembly;
        if (ReferenceEquals(bridge, typeof(LibVLCSharp.Shared.MediaPlayer).Assembly) ||
            AssemblyLoadContext.GetLoadContext(bridge) != _context)
            throw new InvalidOperationException("音乐未使用独立的 LibVLCSharp 加载上下文。");
    }

    public void Dispose()
    {
        ((IDisposable)_output).Dispose();
        ((IDisposable)_runtime).Dispose();
        // 原生解析器按程序集存活；测试进程退出才卸载，不能主动卸载还有 native 回调的上下文。
        using var file = File.Open(_wave, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static object? Read(object instance, string property) => instance.GetType().GetProperty(property)!.GetValue(instance);
    private sealed class PrivateMusicContext(string directory) : AssemblyLoadContext("S1.Music.Private", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
            if (name.Name == "LibVLCSharp") throw new FileNotFoundException("音乐私有目录缺少 LibVLCSharp，禁止静默共享视频程序集。", path);
            return null;
        }
    }

    private static void WriteWave(string path)
    {
        // 二十秒低音量合成 PCM，足够覆盖两个加载顺序与同时播放，无外部媒体或账号依赖。
        const int samples = 44100 * 20;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100);
        writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(256 * Math.Sin(2 * Math.PI * 220 * i / 44100)));
    }
}
