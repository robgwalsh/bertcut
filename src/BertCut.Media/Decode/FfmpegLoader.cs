using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using FFmpeg.AutoGen.Abstractions;

namespace BertCut.Media.Decode;

/// <summary>
/// Points the FFmpeg bindings at the shared libraries BertCut ships.
/// </summary>
/// <remarks>
/// The dynamically-loaded binding flavour is used deliberately: it resolves the libraries
/// from an explicit directory rather than the OS search path, so a stale system-wide
/// FFmpeg can never be picked up. That matters because the bindings are generated against
/// a specific ABI (avcodec 62 for the 8.1 series) and loading a mismatched build produces
/// access violations rather than a clean error.
/// </remarks>
public static class FfmpegLoader
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>Loads the libraries from the runtime's directory. Idempotent.</summary>
    public static void EnsureInitialized(FfmpegRuntime runtime)
    {
        lock (Gate)
        {
            if (_initialized) return;

            DynamicallyLoadedBindings.LibrariesPath = runtime.Directory;
            DynamicallyLoadedBindings.Initialize();

            // Decoding errors are reported through return codes; the default log level
            // also writes to stderr, which is noise in a GUI process.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            _initialized = true;
        }
    }

    /// <summary>Turns a negative FFmpeg return code into its message.</summary>
    public static unsafe string DescribeError(int code)
    {
        const int BufferSize = 1024;
        var buffer = stackalloc byte[BufferSize];
        ffmpeg.av_strerror(code, buffer, BufferSize);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"error {code}";
    }

    /// <summary>Throws when <paramref name="code"/> indicates failure.</summary>
    public static int Check(int code, string what) =>
        code < 0 ? throw new FfmpegDecodeException($"{what}: {DescribeError(code)}") : code;
}

public sealed class FfmpegDecodeException(string message) : Exception(message);
