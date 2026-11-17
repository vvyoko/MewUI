using Aprillz.MewUI.Rendering;

namespace Aprillz.MewUI.Text;

/// <summary>
/// The single <see cref="ITextRenderContext"/> implementation: lays paint-span colors,
/// backgrounds, decorations and overlays over a <see cref="ManagedTextLayout"/> and hands each
/// run to the backend through its private realization surface.
/// </summary>
internal sealed class ManagedTextRenderContext : ITextRenderContext, IDisposable
{
    // What the cache holds when frames draw little text, and the most it grows to for frames that draw a lot.
    private const int REALIZATION_CAPACITY = 128;
    private const int REALIZATION_CAPACITY_LIMIT = 4096;

    // Runs asked for since the last frame ended. A frame that draws more runs than the cache holds
    // realizes every one of them again, every frame, so the capacity follows what a frame draws.
    private int _runsThisFrame;
    private const string ELLIPSIS = "...";

    private readonly IGraphicsContext _context;
    private readonly ITextBackendRenderContext _backend;
    private readonly BoundedCache<RunRealizationKey, RealizedRun> _runs = new(
        REALIZATION_CAPACITY,
        static run => run.Dispose());

    public ManagedTextRenderContext(IGraphicsContext context)
    {
        _context = context;
        _backend = context as ITextBackendRenderContext
            ?? throw new InvalidOperationException("The graphics context does not provide text realization services.");
    }

    public IGraphicsContext Graphics => _context;

    internal int CachedLayoutCount => _runs.Count;
    internal IEnumerable<ITextBackendRun> CachedRuns
        => _runs.Values.Select(static run => run.Run);

    public void Draw(ITextLayout layout, Point origin, in TextDrawOptions options)
    {
        var managed = Validate(layout);
        if (CanDrawFastPath(managed, in options))
        {
            DrawFastPath(managed, origin, options.Foreground, EffectiveOwner(in options), options.Transient);
        }
        else
        {
            DrawBackgroundCore(managed, origin, in options);
            DrawForegroundCore(managed, origin, in options);
        }
    }

    public void DrawBackground(ITextLayout layout, Point origin, in TextDrawOptions options)
    {
        var managed = Validate(layout);
        if (!CanDrawFastPath(managed, in options))
        {
            DrawBackgroundCore(managed, origin, in options);
        }
    }

    public void DrawForeground(ITextLayout layout, Point origin, in TextDrawOptions options)
    {
        var managed = Validate(layout);
        if (CanDrawFastPath(managed, in options))
        {
            DrawFastPath(managed, origin, options.Foreground, EffectiveOwner(in options), options.Transient);
        }
        else
        {
            DrawForegroundCore(managed, origin, in options);
        }
    }

    // Transient text draws through the backend's per-frame scratch textures, which it selects by
    // this owner identity; the caller's owner would key a cache entry instead.
    private static object? EffectiveOwner(in TextDrawOptions options)
        => options.Transient ? TransientText.Owner : options.Owner;

    private static ManagedTextLayout Validate(ITextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout is not ManagedTextLayout managed)
        {
            throw new ArgumentException("The layout was created by a different text engine.", nameof(layout));
        }
        return managed;
    }

    internal static bool CanDrawFastPath(ManagedTextLayout layout, in TextDrawOptions options)
        => layout.IsFastPath &&
           !layout.HasMaterializedColumns &&
           options.PaintSpans.IsEmpty &&
           options.Overlays.IsEmpty &&
           options.Stroke is null &&
           options.Shadow is null;

    private void DrawBackgroundCore(ManagedTextLayout managed, Point origin, in TextDrawOptions options)
    {
        DrawBackgrounds(managed, origin, options.PaintSpans.Span);
        DrawOverlays(managed, origin, options.Overlays.Span);
    }

    private void DrawForegroundCore(ManagedTextLayout managed, Point origin, in TextDrawOptions options)
    {
        var lines = managed.ManagedLines;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var runs = managed.GetRuns(line);
            for (int index = 0; index < runs.Length; index++)
            {
                ref readonly var run = ref runs[index];
                Rect bounds = GetBaselineAlignedBounds(
                    line, origin, run.X, run.Width, run.Baseline, run.BaselineOffset, run.MeasuredHeight);
                if (run.Kind == ManagedTextRunKind.Inline)
                {
                    managed.GetInline(in run)?.Draw(this, bounds.Position);
                    continue;
                }
                if (run.Kind is ManagedTextRunKind.Tab or ManagedTextRunKind.NewLine)
                {
                    continue;
                }

                var realized = GetOrCreateRun(
                    managed, run.TextStart, run.TextLength, run.Font, run.Width, bounds.Height, options.Transient);
                if (realized is not null)
                {
                    try
                    {
                        DrawRunColorSegments(
                            managed, line.RunStart + index, origin, bounds, run.Baseline, realized, in options);
                    }
                    finally
                    {
                        if (options.Transient)
                        {
                            realized.Dispose();
                        }
                    }
                }
            }

            if (line.IsTrimmed)
            {
                DrawEllipsis(managed, line, runs, origin, options.Foreground, EffectiveOwner(in options));
            }
        }

        DrawDecorations(managed, origin, options.PaintSpans.Span);
    }

    /// <summary>
    /// Box a run's realization draws into: its raster top sits so the run's own baseline meets the
    /// line's, raised by the run's shift, and the box reaches the line's ink bottom.
    /// </summary>
    private static Rect GetBaselineAlignedBounds(
        ManagedTextLine line,
        Point origin,
        double x,
        double width,
        double baseline,
        double baselineOffset,
        double measuredHeight)
    {
        double lineBaseline = origin.Y + line.Metrics.Bounds.Y + line.Metrics.Baseline - baselineOffset;
        double top = lineBaseline - baseline;
        // The outer trim changes the reported box, not the glyph ink that may overflow it.
        double inkBottom = origin.Y + line.Metrics.Bounds.Bottom + line.TrimBottom;
        if (baselineOffset != 0)
        {
            // A raised run shrinks the line's descent by its shift, so the line bottom alone can
            // end above the run's raster; an unshifted run keeps the box it always had.
            inkBottom = Math.Max(inkBottom, top + measuredHeight);
        }
        return new Rect(origin.X + x, top, Math.Max(1, width), Math.Max(1, inkBottom - top));
    }

    /// <summary>Run box of a fast-path segment, placed like a run-path run so both put glyphs on the line baseline.</summary>
    private static Rect GetFastPathBounds(ManagedTextLine line, Point origin, IFont font, double x, double width)
        => GetBaselineAlignedBounds(line, origin, x, width, line.FastRasterBaseline, 0, font.Ascent + font.Descent);

    private void DrawFastPath(ManagedTextLayout managed, Point origin, Color color, object? owner, bool transient)
    {
        var line = managed.ManagedLines[0];
        var font = managed.GetDefaultFont();
        Rect? clip = _context.GetClipBoundsLocal();
        if (clip is Rect visibleClip)
        {
            DrawFastPathVisibleRange(managed, line, origin, visibleClip, color, owner, font, transient);
            return;
        }

        foreach (var segment in line.FastSegments ?? [])
        {
            var bounds = GetFastPathBounds(line, origin, font, segment.X, segment.Width);
            var realized = GetOrCreateRun(managed, segment.Start, segment.Length, font, bounds.Width, bounds.Height, transient);
            if (realized is not null)
            {
                DrawRun(realized, bounds.Position, color, owner);
                if (transient)
                {
                    realized.Dispose();
                }
            }
        }
    }

    private void DrawFastPathVisibleRange(
        ManagedTextLayout managed,
        ManagedTextLine line,
        Point origin,
        Rect clip,
        Color color,
        object? owner,
        IFont font,
        bool transient)
    {
        const double OVERSCAN = 32;
        double hitY = line.Metrics.Bounds.Y + line.Metrics.Bounds.Height * 0.5;
        CharacterHit startHit = managed.HitTestPoint(new Point(clip.Left - origin.X - OVERSCAN, hitY));
        CharacterHit endHit = managed.HitTestPoint(new Point(clip.Right - origin.X + OVERSCAN, hitY));
        int textStart = Math.Clamp(startHit.FirstCharacterIndex, 0, managed.Snapshot.Text.Length);
        int textEnd = Math.Clamp(endHit.InsertionIndex, textStart, managed.Snapshot.Text.Length);
        if (textEnd <= textStart)
        {
            return;
        }

        Rect startCaret = managed.GetCaretBounds(new CharacterHit(textStart, 0));
        Rect endCaret = managed.GetCaretBounds(new CharacterHit(textEnd, 0));
        var bounds = GetFastPathBounds(line, origin, font, startCaret.X, endCaret.X - startCaret.X);
        var realized = GetOrCreateRun(managed, textStart, textEnd - textStart, font, bounds.Width, bounds.Height, transient);
        if (realized is not null)
        {
            DrawRun(realized, bounds.Position, color, owner);
            if (transient)
            {
                realized.Dispose();
            }
        }
    }

    /// <summary>Draws the trimming ellipsis after the last surviving run of a trimmed line.</summary>
    private void DrawEllipsis(
        ManagedTextLayout managed,
        ManagedTextLine line,
        ReadOnlySpan<ManagedTextRun> runs,
        Point origin,
        Color color,
        object? owner)
    {
        var lineBounds = line.Metrics.Bounds;
        var font = runs.Length > 0 ? runs[^1].Font : managed.GetDefaultFont();
        double x = runs.Length > 0 ? runs[^1].X + runs[^1].Width : lineBounds.X;
        double width = Math.Max(1, lineBounds.Right - x);
        double baseline = runs.Length > 0 ? runs[^1].Baseline : font.Ascent;
        double baselineOffset = runs.Length > 0 ? runs[^1].BaselineOffset : managed.Snapshot.DefaultStyle.BaselineOffset;
        double measuredHeight = runs.Length > 0 ? runs[^1].MeasuredHeight : font.Ascent + font.Descent;
        Rect bounds = GetBaselineAlignedBounds(line, origin, x, width, baseline, baselineOffset, measuredHeight);
        using var run = _backend.CreateRun(ELLIPSIS, font, width, bounds.Height);
        if (run is null)
        {
            return;
        }

        _backend.DrawRun(run, bounds.Position, color, owner);
    }

    private void DrawBackgrounds(ManagedTextLayout layout, Point origin, ReadOnlySpan<TextPaintSpan> spans)
    {
        foreach (var span in spans)
        {
            if (span.Background is Color color)
            {
                DrawRangeRectangles(layout, origin, span.Range, color);
            }
        }
    }

    private void DrawOverlays(ManagedTextLayout layout, Point origin, ReadOnlySpan<TextOverlay> overlays)
    {
        foreach (var overlay in overlays)
        {
            DrawRangeRectangles(layout, origin, overlay.Range, overlay.Color);
        }
    }

    private void DrawRangeRectangles(ManagedTextLayout layout, Point origin, TextRange range, Color color)
    {
        var bounds = new List<Rect>();
        layout.GetRangeBounds(range.Start, range.Length, bounds);
        foreach (var rect in bounds)
        {
            _context.FillRectangle(new Rect(origin.X + rect.X, origin.Y + rect.Y, rect.Width, rect.Height), color);
        }
    }

    private void DrawDecorations(ManagedTextLayout layout, Point origin, ReadOnlySpan<TextPaintSpan> spans)
    {
        foreach (var span in spans)
        {
            if (span.Decoration == TextDecoration.None)
            {
                continue;
            }
            var bounds = new List<Rect>();
            layout.GetRangeBounds(span.Range.Start, span.Range.Length, bounds);
            Color color = span.Foreground ?? Color.FromArgb(255, 0, 0, 0);
            double dpiScale = _context.DpiScale;
            double thickness = LayoutRounding.SnapThicknessToPixels(1, dpiScale, 1);
            foreach (var rect in bounds)
            {
                if (span.Decoration.HasFlag(TextDecoration.Underline))
                {
                    FillSnappedDecoration(
                        origin.X + rect.X,
                        origin.X + rect.Right,
                        origin.Y + Math.Max(rect.Y, rect.Bottom - thickness),
                        thickness, dpiScale, color);
                }
                if (span.Decoration.HasFlag(TextDecoration.Strikethrough))
                {
                    FillSnappedDecoration(
                        origin.X + rect.X,
                        origin.X + rect.Right,
                        origin.Y + rect.Y + Math.Max(0, (rect.Height - thickness) * 0.55),
                        thickness, dpiScale, color);
                }
            }
        }
    }

    /// <summary>
    /// Draws style-run underline/strikethrough as renderer geometry so every backend matches;
    /// font-level decoration support varies by backend and is not relied on.
    /// </summary>
    private void DrawRunDecoration(TextRunStyle style, double left, double right, in Rect runBounds, double baseline, Color color)
    {
        if (style.Decoration == TextDecoration.None)
        {
            return;
        }

        double dpiScale = _context.DpiScale;
        double thickness = LayoutRounding.SnapThicknessToPixels(1, dpiScale, 1);
        double clampedLeft = Math.Max(left, runBounds.X);
        double clampedRight = Math.Min(right, runBounds.Right);
        if (style.Decoration.HasFlag(TextDecoration.Underline))
        {
            FillSnappedDecoration(
                clampedLeft, clampedRight,
                Math.Min(runBounds.Y + baseline + 1, runBounds.Bottom - thickness),
                thickness, dpiScale, color);
        }
        if (style.Decoration.HasFlag(TextDecoration.Strikethrough))
        {
            // FontSize is in points; 4/3 converts to DIPs, strike sits ~30% of the em above baseline.
            FillSnappedDecoration(
                clampedLeft, clampedRight,
                runBounds.Y + baseline - style.FontSize * (4.0 / 3.0) * 0.3,
                thickness, dpiScale, color);
        }
    }

    /// <summary>Fills one decoration stroke snapped to whole device pixels.</summary>
    private void FillSnappedDecoration(double left, double right, double y, double thickness, double dpiScale, Color color)
    {
        // Both ends round the same way, so a decoration cut into segments by paint spans stays
        // contiguous: neighboring segments share an edge and land on the same pixel.
        double snappedLeft = LayoutRounding.RoundToPixel(left, dpiScale);
        double width = LayoutRounding.RoundToPixel(right, dpiScale) - snappedLeft;
        if (width <= 0)
        {
            return;
        }
        _context.FillRectangle(new Rect(snappedLeft, LayoutRounding.RoundToPixel(y, dpiScale), width, thickness), color);
    }

    /// <summary>
    /// Draws one style run partitioned into effective-foreground segments so every pixel is
    /// painted exactly once. The geometry realization stays whole-run (paint spans never split
    /// or recreate it); only the draw is clipped per color segment, so glyph antialiasing blends
    /// against the clean background instead of an overdrawn base pass.
    /// </summary>
    private void DrawRunColorSegments(
        ManagedTextLayout managed,
        int runIndex,
        Point origin,
        Rect runBounds,
        double baseline,
        RealizedRun realized,
        in TextDrawOptions options)
    {
        var spans = options.PaintSpans.Span;
        ref readonly var run = ref managed.GetRun(runIndex);

        // A segment splits on any paint change, not just colour: a span that changes the outline
        // mid-run has to end the previous segment or the old stroke would paint the new glyphs.
        // Boundaries are text elements, so splitting there never cuts a glyph in half.
        var boundaries = managed.GetRunBoundaries(runIndex);
        int first = boundaries.Length > 0 ? boundaries[0] : run.TextStart;
        int segmentStart = 0;
        Color segmentColor = GetSpanForeground(spans, first) ?? options.Foreground;
        TextStroke? segmentStroke = GetSpanStroke(spans, first) ?? options.Stroke;
        TextShadow? segmentShadow = GetSpanShadow(spans, first) ?? options.Shadow;

        for (int index = 1; index <= boundaries.Length; index++)
        {
            Color nextColor = default;
            TextStroke? nextStroke = null;
            TextShadow? nextShadow = null;
            if (index < boundaries.Length)
            {
                int at = boundaries[index];
                nextColor = GetSpanForeground(spans, at) ?? options.Foreground;
                nextStroke = GetSpanStroke(spans, at) ?? options.Stroke;
                nextShadow = GetSpanShadow(spans, at) ?? options.Shadow;
                if (nextColor == segmentColor && nextStroke == segmentStroke && nextShadow == segmentShadow)
                {
                    continue;
                }
            }

            int startOffset = boundaries.Length > 0 ? boundaries[segmentStart] : run.TextStart;
            int endOffset = index < boundaries.Length ? boundaries[index] : run.TextEnd;
            double left = origin.X + managed.GetColumnX(in run, startOffset);
            double right = origin.X + managed.GetColumnX(in run, endOffset);
            bool wholeRun = segmentStart == 0 && index == boundaries.Length;

            if (wholeRun)
            {
                DrawSegment(managed, in run, runBounds, baseline, realized, startOffset, endOffset,
                    segmentColor, segmentStroke, segmentShadow, clip: null, in options);
            }
            else
            {
                // Interior boundaries floor to whole device pixels so adjacent segments agree on
                // pixel ownership; outer edges follow the glyph ink to keep the overhang.
                double dpiScale = _context.DpiScale;
                double antialiasMargin = 1 / dpiScale;
                var ink = realized.Run.Ink;
                double clipLeft = segmentStart == 0
                    ? runBounds.X - ink.Left - antialiasMargin
                    : Math.Floor(left * dpiScale) / dpiScale;
                double clipRight = index == boundaries.Length
                    ? runBounds.Right + ink.Right + antialiasMargin
                    : Math.Floor(right * dpiScale) / dpiScale;
                double clipTop = runBounds.Y - ink.Top - antialiasMargin;
                double clipBottom = runBounds.Bottom + ink.Bottom + antialiasMargin;
                var clip = new Rect(clipLeft, clipTop, Math.Max(0, clipRight - clipLeft), Math.Max(0, clipBottom - clipTop));
                if (!clip.IsEmpty)
                {
                    DrawSegment(managed, in run, runBounds, baseline, realized, startOffset, endOffset,
                        segmentColor, segmentStroke, segmentShadow, clip, in options);
                }
            }
            DrawRunDecoration(managed.Snapshot.GetStyle(startOffset), left, right, runBounds, baseline, segmentColor);

            segmentStart = index;
            segmentColor = nextColor;
            segmentStroke = nextStroke;
            segmentShadow = nextShadow;
        }
    }

    /// <summary>
    /// One uniform segment: optional effects under the same clip as the glyph, so a partial stroke
    /// cannot bleed its outline onto the neighbouring segment's glyphs.
    /// </summary>
    private void DrawSegment(
        ManagedTextLayout managed,
        in ManagedTextRun run,
        Rect runBounds,
        double baseline,
        RealizedRun realized,
        int startOffset,
        int endOffset,
        Color color,
        TextStroke? stroke,
        TextShadow? shadow,
        Rect? clip,
        in TextDrawOptions options)
    {
        if (clip is Rect bounds)
        {
            _context.Save();
            try
            {
                _context.IntersectClip(bounds);
                DrawSegmentPaint(managed, in run, runBounds, baseline, realized,
                    startOffset, endOffset, color, stroke, shadow, in options);
            }
            finally
            {
                _context.Restore();
            }
        }
        else
        {
            DrawSegmentPaint(managed, in run, runBounds, baseline, realized,
                startOffset, endOffset, color, stroke, shadow, in options);
        }
    }

    private void DrawSegmentPaint(
        ManagedTextLayout managed,
        in ManagedTextRun run,
        Rect runBounds,
        double baseline,
        RealizedRun realized,
        int startOffset,
        int endOffset,
        Color color,
        TextStroke? stroke,
        TextShadow? shadow,
        in TextDrawOptions options)
    {
        if (stroke is not null || shadow is not null)
        {
            DrawGlyphEffects(managed, in run, runBounds, baseline, realized,
                startOffset, endOffset, stroke, shadow);
        }
        DrawRun(realized, runBounds.Position, color, EffectiveOwner(in options));
    }

    /// <summary>
    /// Draws a stroke and/or shadow from the real glyph outline: each character's contour is appended
    /// into one path in run-local coordinates, then filled for the shadow and stroked for the outline.
    /// The cached path is reused by every redraw of the run — only the paint changes per frame.
    /// </summary>
    private void DrawGlyphEffects(
        ManagedTextLayout managed,
        in ManagedTextRun run,
        Rect runBounds,
        double baseline,
        RealizedRun realized,
        int startOffset,
        int endOffset,
        TextStroke? stroke,
        TextShadow? shadow)
    {
        var outline = realized.GetOrCreateOutline(managed, in run, baseline, startOffset, endOffset);
        if (outline is null || outline.IsEmpty)
        {
            return;
        }

        var origin = runBounds.Position;
        if (shadow is TextShadow s && s.Color.A > 0)
        {
            var radius = Math.Max(0, s.BlurRadius);
            if (radius < 0.01)
            {
                WithOffset(origin, s.OffsetX, s.OffsetY, () => _context.FillPath(outline, s.Color));
            }
            else
            {
                DrawBlurredShadow(outline, origin, s, radius);
            }
        }

        if (stroke is TextStroke t && t.Color.A > 0 && t.Width > 0)
        {
            WithOffset(origin, 0, 0, () => _context.DrawPath(outline, t.Color, t.Width * 2));
        }
    }

    /// <summary>
    /// Gaussian-ish shadow by weighted multiple fills of the same outline. Real blur would need an
    /// offscreen pass, which cannot be expressed in a retained recording; a small fixed kernel keeps
    /// the shadow glyph-shaped at every backend and costs only a few extra path fills.
    /// </summary>
    private void DrawBlurredShadow(PathGeometry outline, Point origin, TextShadow shadow, double radius)
    {
        (double DX, double DY, double Weight)[] kernel =
        [
            (0, 0, 0.25),
            (-1, 0, 0.125), (1, 0, 0.125), (0, -1, 0.125), (0, 1, 0.125),
            (-1, -1, 0.0625), (1, -1, 0.0625), (-1, 1, 0.0625), (1, 1, 0.0625),
        ];
        foreach (var (dx, dy, weight) in kernel)
        {
            Color color = Color.FromArgb(
                (byte)Math.Round(shadow.Color.A * weight),
                shadow.Color.R, shadow.Color.G, shadow.Color.B);
            WithOffset(origin,
                shadow.OffsetX + dx * radius,
                shadow.OffsetY + dy * radius,
                () => _context.FillPath(outline, color));
        }
    }

    private void WithOffset(Point origin, double dx, double dy, Action draw)
    {
        _context.Save();
        try
        {
            _context.Translate(origin.X + dx, origin.Y + dy);
            draw();
        }
        finally
        {
            _context.Restore();
        }
    }

    /// <summary>Resolves the foreground at a text index; later spans win where ranges overlap.</summary>
    internal static Color? GetSpanForeground(ReadOnlySpan<TextPaintSpan> spans, int index)
    {
        Color? result = null;
        foreach (var span in spans)
        {
            if (span.Foreground is Color color && index >= span.Range.Start && index < span.Range.End)
            {
                result = color;
            }
        }
        return result;
    }

    /// <summary>Resolves the per-range stroke override; later spans win where ranges overlap.</summary>
    internal static TextStroke? GetSpanStroke(ReadOnlySpan<TextPaintSpan> spans, int index)
    {
        TextStroke? result = null;
        foreach (var span in spans)
        {
            if (span.Stroke is TextStroke value && index >= span.Range.Start && index < span.Range.End)
            {
                result = value;
            }
        }
        return result;
    }

    /// <summary>Resolves the per-range shadow override; later spans win where ranges overlap.</summary>
    internal static TextShadow? GetSpanShadow(ReadOnlySpan<TextPaintSpan> spans, int index)
    {
        TextShadow? result = null;
        foreach (var span in spans)
        {
            if (span.Shadow is TextShadow value && index >= span.Range.Start && index < span.Range.End)
            {
                result = value;
            }
        }
        return result;
    }

    private RealizedRun? GetOrCreateRun(
        ManagedTextLayout layout,
        int textStart,
        int textLength,
        IFont font,
        double width,
        double height,
        bool transient)
    {
        var key = new RunRealizationKey(layout, textStart, textLength, font, Math.Round(width, 6), Math.Round(height, 6));
        if (!transient)
        {
            _runsThisFrame++;
        }

        if (!transient && _runs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var backendRun = _backend.CreateRun(
            layout.Snapshot.Text.AsSpan(textStart, textLength),
            font,
            Math.Max(1, width),
            Math.Max(1, height),
            layout.Snapshot.Paragraph.LetterSpacing);
        if (backendRun is null)
        {
            return null;
        }

        // A transient run is drawn once and disposed by the caller; caching it would only pin
        // text that never repeats.
        var created = new RealizedRun(backendRun);
        if (!transient)
        {
            _runs.Add(key, created);
        }
        return created;
    }

    private void DrawRun(RealizedRun realized, Point origin, Color color, object? owner)
        => _backend.DrawRun(realized.Run, origin, color, owner);

    /// <summary>
    /// Sizes the cache for the frame that just ended: room for every run it drew and a quarter more,
    /// never under the base capacity and never over the limit. A frame that draws less gives the room back.
    /// </summary>
    internal void FrameEnded()
    {
        int wanted = _runsThisFrame + (_runsThisFrame / 4);
        _runsThisFrame = 0;
        int capacity = Math.Clamp(wanted, REALIZATION_CAPACITY, REALIZATION_CAPACITY_LIMIT);
        if (capacity != _runs.Capacity)
        {
            _runs.Capacity = capacity;
        }
    }

    public void Dispose() => _runs.Dispose();

    private readonly record struct RunRealizationKey(
        ManagedTextLayout Layout,
        int TextStart,
        int TextLength,
        IFont Font,
        double Width,
        double Height);

    private sealed class RealizedRun(ITextBackendRun run) : IDisposable
    {
        // One entry per drawn range: a paint span can change the stroke mid-run, so contours are
        // cached by the character range they cover instead of once for the whole run.
        private readonly Dictionary<(int Start, int End), PathGeometry?> _outlines = [];

        public ITextBackendRun Run { get; } = run;

        /// <summary>
        /// Glyph contours in run-local coordinates for <paramref name="start"/>..<paramref name="end"/>,
        /// built once per range and shared by every redraw. A font without glyph outlines leaves it null.
        /// </summary>
        public PathGeometry? GetOrCreateOutline(
            ManagedTextLayout layout,
            in ManagedTextRun run,
            double baseline,
            int start,
            int end)
        {
            var key = (start, end);
            if (_outlines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            PathGeometry? result = null;
            if (run.Font is IGlyphOutlineFont outlineFont && run.Kind == ManagedTextRunKind.Text)
            {
                var path = new PathGeometry();
                string text = layout.Snapshot.Text;
                for (int index = start; index < end; index++)
                {
                    char ch = text[index];
                    if (ch is '\r' or '\n' or '\t')
                    {
                        continue;
                    }
                    double x = layout.GetColumnX(in run, index) - run.X;
                    outlineFont.TryAppendGlyphOutline(path, ch, new Point(x, baseline), out _);
                }
                if (!path.IsEmpty)
                {
                    result = path;
                }
            }

            _outlines[key] = result;
            return result;
        }

        public void Dispose()
        {
            Run.Dispose();
        }
    }
}
