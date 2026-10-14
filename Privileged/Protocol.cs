using System.Buffers.Binary;
using System.IO.Pipes;

namespace ClickClean.Security;

// 不传递路径、脚本、进程标识或任意序列化对象，协议只能表达固定整理命令。
public static class CleanupProtocol
{
    public const int Version = 1;
    public const string Prefix = "ClickClean.Privileged.";
    public const int Quit = 0;
    public static bool IsCommand(int value) => value is 2 or 3 or 4;
    public static bool TryArguments(string[] args, out string pipe, out int process)
    {
        pipe = ""; process = 0;
        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--client" ||
            !Guid.TryParseExact(args[1], "N", out var nonce) ||
            !int.TryParse(args[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out process) || process <= 0)
            return false;
        pipe = Prefix + nonce.ToString("N");
        return true;
    }
    public static int ReadInt(Stream stream, TimeSpan? timeout = null)
    {
        var bytes = new byte[sizeof(int)];
        // 超时只停止等待协议，不强行终止正在执行的系统调用。
        using var cancellation = timeout is null ? new CancellationTokenSource() : new CancellationTokenSource(timeout.Value);
        stream.ReadExactlyAsync(bytes, cancellation.Token).AsTask().GetAwaiter().GetResult();
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
    public static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes); stream.Flush();
    }
    public static int SelfTest()
    {
        if (!IsCommand(2) || !IsCommand(3) || !IsCommand(4) || IsCommand(100) || IsCommand(-1)) return 1;
        if (TryArguments(["--pipe", "../evil", "--client", "1"], out _, out _) ||
            TryArguments(["--pipe", new string('a', 32), "--client", "-1"], out _, out _) ||
            TryArguments(["--pipe", new string('a', 32), "--client", "1", "--command", "2"], out _, out _)) return 2;
        if (!TryArguments(["--pipe", new string('a', 32), "--client", "1"], out var pipe, out var process) ||
            pipe != Prefix + new string('a', 32) || process != 1) return 3;
        using var memory = new MemoryStream(); WriteInt(memory, unchecked((int)0xC0000022)); memory.Position = 0;
        return ReadInt(memory) == unchecked((int)0xC0000022) ? 0 : 4;
    }
}
