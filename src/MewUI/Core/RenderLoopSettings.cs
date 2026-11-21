namespace Aprillz.MewUI;

/// <summary>
/// Controls the application's render loop scheduling.
/// </summary>
/// <remarks>
/// Continuous rendering has exactly two inputs: the user <see cref="Continuous"/> flag, and active animations
/// (driven internally by the animation system). The platform host reads <see cref="IsContinuous"/> (the OR of
/// those, plus VSync off) each frame; nothing sets the loop mode directly.
/// </remarks>
/// <summary>
/// Cumulative pacing counters of the continuous loop: how many frames it completed, and where the
/// frame time went (all in <see cref="System.Diagnostics.Stopwatch"/> ticks).
/// </summary>
/// <remarks>
/// Cumulative rather than windowed on purpose: the consumer already windows its own log line, and a
/// second window here would only make the two disagree. Subtract two snapshots to get a span.
/// <para>
/// <see cref="Frames"/> is the loop's own count of completed passes, i.e. the number of presentations
/// it made. It exists because a host-side frame callback can fire on a different cadence than the
/// loop; this is the number to compare against an external overlay (RTSS and friends count presents).
/// </para>
/// </remarks>
public sealed class RenderLoopStats
{
    /// <summary>Completed frame passes.</summary>
    public long Frames { get; internal set; }

    /// <summary>Time spent in <c>ProcessMessages</c>.</summary>
    public long MessagesTicks { get; internal set; }

    /// <summary>Time spent rendering windows (layout + draw + present of every window).</summary>
    public long RenderTicks { get; internal set; }

    /// <summary>Time spent waiting for the next frame budget (or yielding, when uncapped).</summary>
    public long WaitTicks { get; internal set; }

    /// <summary>Time spent inside <c>EndFrame</c> (flush / submit / composition wait), written by the context.</summary>
    public long EndFrameTicks { get; internal set; }

    /// <summary>Time spent inside <c>IDXGISwapChain::Present</c>, written by the backend.</summary>
    public long PresentTicks { get; internal set; }

    /// <summary>Presents counted by <see cref="PresentTicks"/>.</summary>
    public long Presents { get; internal set; }
}

public sealed class RenderLoopSettings
{
    private readonly RenderLoopStats _loopStats = new();
    private int _continuous;
    private int _targetFps;
    private int _vsyncEnabled = 1;

    internal RenderLoopSettings()
    {
    }

    /// <summary>
    /// Gets or sets whether the render loop is forced to run continuously.
    /// </summary>
    /// <remarks>
    /// This is the only direct way to request continuous rendering; the animation system requests it on its own
    /// (see <see cref="IsContinuous"/>) while clocks run.
    /// </remarks>
    public bool Continuous
    {
        get => Volatile.Read(ref _continuous) != 0;
        set => Interlocked.Exchange(ref _continuous, value ? 1 : 0);
    }

    /// <summary>
    /// Gets or sets the target FPS. A value of 0 indicates no cap.
    /// </summary>
    public int TargetFps
    {
        get => Volatile.Read(ref _targetFps);
        set => Interlocked.Exchange(ref _targetFps, value);
    }

    /// <summary>
    /// Gets or sets whether VSync is enabled (when supported by the backend/platform).
    /// </summary>
    public bool VSyncEnabled
    {
        get => Volatile.Read(ref _vsyncEnabled) != 0;
        set => Interlocked.Exchange(ref _vsyncEnabled, value ? 1 : 0);
    }

    /// <summary>
    /// Cumulative pacing counters of the continuous loop (frames completed, and the split between
    /// messages / render / wait). Diagnostics only; the platform host writes it every pass.
    /// </summary>
    public RenderLoopStats LoopStats => _loopStats;

    /// <summary>
    /// Driven by the animation system: true while one or more animation clocks are active. Not a user knob - set the
    /// <see cref="Continuous"/> flag to force continuous rendering yourself.
    /// </summary>
    // Pulled from the animation manager rather than pushed into here, so a clock started before this run
    // existed still turns the loop continuous once the loop begins reading.
    internal bool AnimationActive => Animation.AnimationManager.Instance.HasUnpausedClocks;

    /// <summary>
    /// Gets whether the render loop should run continuously: VSync is off, <see cref="Continuous"/> is set, or
    /// animations are active.
    /// </summary>
    /// <remarks>
    /// The single value the platform host reads each frame.
    /// </remarks>
    public bool IsContinuous => !VSyncEnabled || Continuous || AnimationActive;

    /// <summary>Sets <see cref="Continuous"/>.</summary>
    public void SetContinuous(bool enabled) => Continuous = enabled;

    /// <summary>
    /// Frame rate the loop should hold itself to, or 0 to leave pacing to the backend's presentation.
    /// <see cref="TargetFps"/> wins when set; otherwise a backend that cannot pace on the display refresh
    /// holds itself to <paramref name="displayRefreshHz"/> so an animating window neither renders as fast
    /// as the CPU allows nor gives up the frames a faster screen can show.
    /// </summary>
    /// <param name="displayRefreshHz">The screen's refresh rate, or 0 when the platform cannot report it.</param>
    internal int EffectiveFrameCap(int displayRefreshHz = 0)
    {
        int target = TargetFps;
        if (target > 0)
        {
            return target;
        }

        // Turning VSync off is the request for an unlimited loop, and the only case that leaves the loop
        // without a rhythm of its own. A backend that blocks in its present reaches that block before the
        // cap below, so the cap costs it nothing while covering the frames it does not present.
        if (!VSyncEnabled)
        {
            return 0;
        }

        return displayRefreshHz > 0 ? displayRefreshHz : VSyncFallbackFps;
    }

    // Used when the platform cannot report a refresh rate; 60 is the rate a backend without presentation
    // pacing is expected to hold.
    internal const int VSyncFallbackFps = 60;
}
