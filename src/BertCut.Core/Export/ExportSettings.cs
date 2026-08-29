namespace BertCut.Core.Export;

/// <summary>Video encoders BertCut knows how to configure, best first.</summary>
public enum VideoEncoder
{
    /// <summary>NVIDIA NVENC. Present on this project's target machine.</summary>
    H264Nvenc,

    /// <summary>Intel Quick Sync.</summary>
    H264Qsv,

    /// <summary>AMD Advanced Media Framework.</summary>
    H264Amf,

    /// <summary>CPU fallback. Only in GPL builds — see <see cref="EncoderCapabilities"/>.</summary>
    Libx264,

    /// <summary>CPU fallback available in LGPL builds.</summary>
    Libopenh264,
}

/// <summary>
/// What the installed ffmpeg can actually do, probed at startup.
/// </summary>
/// <remarks>
/// Passed into <see cref="FfmpegArgs"/> rather than discovered inside it, which keeps the
/// argument builder pure and lets tests pin a capability set. Hard-coding NVENC would
/// work on the development machine and fail on any other.
/// </remarks>
public sealed record EncoderCapabilities(
    IReadOnlySet<string> Encoders,
    IReadOnlySet<string> Filters,
    bool HasCudaHwaccel)
{
    public static readonly EncoderCapabilities NvencOnly = new(
        new HashSet<string> { "h264_nvenc", "hevc_nvenc", "aac" },
        new HashSet<string> { "crop", "scale", "overlay", "trim", "atrim", "concat", "aresample" },
        HasCudaHwaccel: true);

    public bool Has(string encoder) => Encoders.Contains(encoder);

    /// <summary>Picks the best available encoder, preferring hardware.</summary>
    public VideoEncoder SelectVideoEncoder()
    {
        if (Has("h264_nvenc")) return VideoEncoder.H264Nvenc;
        if (Has("h264_qsv")) return VideoEncoder.H264Qsv;
        if (Has("h264_amf")) return VideoEncoder.H264Amf;
        if (Has("libx264")) return VideoEncoder.Libx264;
        if (Has("libopenh264")) return VideoEncoder.Libopenh264;

        throw new InvalidOperationException(
            "No usable H.264 encoder found in this ffmpeg build. Run tools/fetch-ffmpeg.ps1.");
    }

    public static string NameOf(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.H264Nvenc => "h264_nvenc",
        VideoEncoder.H264Qsv => "h264_qsv",
        VideoEncoder.H264Amf => "h264_amf",
        VideoEncoder.Libx264 => "libx264",
        VideoEncoder.Libopenh264 => "libopenh264",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder)),
    };
}

/// <summary>User-facing export options.</summary>
public sealed record ExportSettings(
    string OutputPath,
    Quality Quality = Quality.Balanced,
    bool AllowLosslessFastPath = true)
{
    /// <summary>How long a GIF may be before the export refuses.</summary>
    public const int DefaultGifMaxSeconds = 30;

    /// <summary>Audio bitrate for the re-encoded track.</summary>
    public int AudioBitrateKbps { get; init; } = 192;

    /// <summary>
    /// Frames a second in a GIF.
    /// </summary>
    /// <remarks>
    /// Twelve rather than the timeline's rate. A GIF has no inter-frame compression, so every
    /// frame costs its full palettised size and halving the rate halves the file; twelve is where
    /// screen-recorded motion still reads as motion.
    /// </remarks>
    public int GifFrameRate { get; init; } = 12;

    /// <summary>
    /// The widest a GIF is written, scaled down to fit and never up.
    /// </summary>
    /// <remarks>
    /// Never up, because enlarging a frame in a 256-colour palette adds bytes and no detail.
    /// </remarks>
    public int GifMaxWidth { get; init; } = 640;

    /// <summary>Duration past which a GIF is refused rather than written.</summary>
    public int GifMaxSeconds { get; init; } = DefaultGifMaxSeconds;

    /// <summary>Whether the named output asks for a GIF.</summary>
    public bool WantsGif =>
        System.IO.Path.GetExtension(OutputPath).Equals(".gif", StringComparison.OrdinalIgnoreCase);
}

public enum Quality
{
    /// <summary>Fastest encode, larger file.</summary>
    Fast,

    /// <summary>The default: p6/cq21, the practical quality knee for NVENC.</summary>
    Balanced,

    /// <summary>Slowest preset, smallest file at equal quality.</summary>
    High,
}
