using Microsoft.Extensions.DependencyInjection;

namespace QrShard;

/// <summary>
/// Composition root: the one place that wires interfaces to implementations. DI lives at this
/// level only — the numeric primitives (Gf256, Fec, Crc, BitStream, Homography, CameraMath,
/// FastPng) remain static pure functions, so testing seams cost nothing in the hot loops.
/// </summary>
internal static class ServiceRegistration
{
    public static ServiceProvider BuildProvider(AppSettings settings) =>
        new ServiceCollection()
            .AddSingleton(settings)
            // Decode pipeline
            .AddSingleton<IInnerRectScanner, InnerRectScanner>()
            .AddSingleton<IStripReader, StripReader>()
            .AddSingleton<IFrameLocator, FrameLocator>()
            .AddSingleton<IGridSampler, GridSampler>()
            .AddSingleton<IParityReassembler, ParityReassembler>()
            .AddSingleton<IShardAssembler, ShardAssembler>()
            .AddSingleton<IShardDecoder, ShardDecoder>()
            // Camera rectification
            .AddSingleton<IAdaptiveBinarizer, AdaptiveBinarizer>()
            .AddSingleton<IFinderDetector, FinderDetector>()
            .AddSingleton<IQuadSelector, QuadSelector>()
            .AddSingleton<ICoarseFrameScanner, CoarseFrameScanner>()
            .AddSingleton<IFrameEdgeTracer, FrameEdgeTracer>()
            .AddSingleton<ICameraRectifier, CameraRectifier>()
            // Video mode
            .AddSingleton<IFrameSource, RecordingFrameSource>()
            .AddSingleton<IVideoDecoder, VideoDecoder>()
            // Encode + tooling
            .AddSingleton<IShardEncoder, ShardEncoder>()
            .AddSingleton<ISlideshowWriter, SlideshowWriter>()
            .AddSingleton<ISelfTest, SelfTest>()
            .BuildServiceProvider();
}
