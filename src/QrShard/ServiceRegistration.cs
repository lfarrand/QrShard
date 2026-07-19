using Microsoft.Extensions.DependencyInjection;

namespace QrShard;

/// <summary>
/// Composition root: the one place that wires interfaces to implementations. DI lives at this
/// level only — the per-pixel/per-cell hot paths remain static pure functions, so testing
/// seams cost nothing at render/decode time.
/// </summary>
internal static class ServiceRegistration
{
    public static ServiceProvider BuildProvider(AppSettings settings) =>
        new ServiceCollection()
            .AddSingleton(settings)
            .AddSingleton<ICameraRectifier, CameraRectifier>()
            .AddSingleton<IShardDecoder, ShardDecoder>()
            .AddSingleton<IFrameSource, RecordingFrameSource>()
            .AddSingleton<IVideoDecoder, VideoDecoder>()
            .AddSingleton<IShardEncoder, ShardEncoder>()
            .AddSingleton<ISlideshowWriter, SlideshowWriter>()
            .BuildServiceProvider();
}
