using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoSecurityPlayer.Playback.IntegrationHarness;

/// <summary>
/// 仅在验收进程内按内核对象类型统计句柄，用于定位总数闸门失败；不会关闭或复制任何句柄。
/// </summary>
internal static class WindowsHandleDiagnostics
{
    private const int ProcessHandleInformation = 51;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    public static IReadOnlyDictionary<string, int> CaptureCurrentProcessByType()
    {
        using var process = Process.GetCurrentProcess();
        var bufferLength = 64 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                var status = NtQueryInformationProcess(
                    process.Handle,
                    ProcessHandleInformation,
                    buffer,
                    (uint)bufferLength,
                    out var requiredLength);
                if (status == StatusInfoLengthMismatch)
                {
                    bufferLength = checked((int)Math.Max(
                        requiredLength,
                        (uint)(bufferLength * 2)));
                    continue;
                }

                if (status < 0)
                {
                    return new Dictionary<string, int>
                    {
                        [$"ntstatus-0x{status:X8}"] = process.HandleCount
                    };
                }

                return ReadHandleTypes(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new Dictionary<string, int>
        {
            ["snapshot-buffer-exhausted"] = process.HandleCount
        };
    }

    public static IReadOnlyDictionary<string, int> CreateDelta(
        IReadOnlyDictionary<string, int> start,
        IReadOnlyDictionary<string, int> final)
    {
        return start.Keys.Concat(final.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(key => new
            {
                Key = key,
                Delta = final.GetValueOrDefault(key) - start.GetValueOrDefault(key)
            })
            .Where(item => item.Delta != 0)
            .OrderByDescending(item => Math.Abs(item.Delta))
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Delta, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, int> ReadHandleTypes(IntPtr buffer)
    {
        var count = checked((int)(nuint)Marshal.ReadIntPtr(buffer));
        var entrySize = Marshal.SizeOf<ProcessHandleTableEntryInfo>();
        var entryAddress = buffer + (IntPtr.Size * 2);
        var typeNames = new Dictionary<uint, string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < count; index++)
        {
            var entry = Marshal.PtrToStructure<ProcessHandleTableEntryInfo>(
                entryAddress + (index * entrySize));
            if (!typeNames.TryGetValue(entry.ObjectTypeIndex, out var typeName))
            {
                typeName = ReadObjectTypeName(entry.HandleValue) ??
                           $"type-index-{entry.ObjectTypeIndex}";
                typeNames.Add(entry.ObjectTypeIndex, typeName);
            }

            counts[typeName] = counts.GetValueOrDefault(typeName) + 1;
        }

        return counts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string? ReadObjectTypeName(IntPtr handle)
    {
        _ = NtQueryObject(
            handle,
            ObjectTypeInformation,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            var status = NtQueryObject(
                handle,
                ObjectTypeInformation,
                buffer,
                requiredLength,
                out _);
            if (status < 0)
            {
                return null;
            }

            var name = Marshal.PtrToStructure<NativeUnicodeString>(buffer);
            return name.Length == 0 || name.Buffer == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(name.Buffer, name.Length / sizeof(char));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        uint processInformationLength,
        out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        IntPtr handle,
        int objectInformationClass,
        IntPtr objectInformation,
        uint objectInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessHandleTableEntryInfo
    {
        public readonly IntPtr HandleValue;
        public readonly nuint HandleCount;
        public readonly nuint PointerCount;
        public readonly uint GrantedAccess;
        public readonly uint ObjectTypeIndex;
        public readonly uint HandleAttributes;
        public readonly uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeUnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }
}
