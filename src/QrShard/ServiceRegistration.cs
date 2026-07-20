using Microsoft.Extensions.DependencyInjection;

namespace QrShard;

/// <summary>Composition root: the one place that wires interfaces to implementations.</summary>
internal static class ServiceRegistration
{
    public static ServiceProvider BuildProvider(AppSettings settings) =>
        new ServiceCollection()
            .AddSingleton(settings)
            // Codec and raster primitives (registered as concrete sealed types: their calls sit
            // in hot loops, and non-virtual calls on concrete receivers inline like statics did)
            .AddSingleton<Gf256>()
            .AddSingleton<ReedSolomon>()
            .AddSingleton<Fec>()
            .AddSingleton<CrossShardFec>()
            .AddSingleton<FountainFec>()
            .AddSingleton<Crc>()
            .AddSingleton<BitStream>()
            .AddSingleton<Palette>()
            .AddSingleton<FastPng>()
            .AddSingleton<FastPngReader>()
            .AddSingleton<ShardImageFormat>()
            .AddSingleton<CameraMath>()
            .AddSingleton<PayloadCipher>()
            .AddSingleton<Interleaver2>()
            // Decode pipeline
            .AddSingleton<IInnerRectScanner, InnerRectScanner>()
            .AddSingleton<IStripReader, StripReader>()
            .AddSingleton<IFrameLocator, FrameLocator>()
            .AddSingleton<IGridSampler, GridSampler>()
            .AddSingleton<IParityReassembler, ParityReassembler>()
            .AddSingleton<IShardAssembler, ShardAssembler>()
            .AddSingleton<IPhotoFusion, PhotoFusion>()
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
            .AddSingleton<IPayloadPreparer, PayloadPreparer>()
            .AddSingleton<IStripePlanner, StripePlanner>()
            .AddSingleton<IShardRenderer, ShardRenderer>()
            .AddSingleton<IShardEncoder, ShardEncoder>()
            .AddSingleton<ISlideshowWriter, SlideshowWriter>()
            .AddSingleton<ISelfTest, SelfTest>()
            .AddSingleton<ISessionStore, SessionStore>()
            .AddSingleton<ICalibration, CalibrationRunner>()
            .AddSingleton<HeatmapRenderer>()
            .BuildServiceProvider();
}
