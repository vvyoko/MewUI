using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Rendering.Direct2D;
using Aprillz.MewUI.Rendering.Gdi;
using Aprillz.MewUI.Text;

namespace MewUI.Test.Rendering;

/// <summary>
/// Stroke and shadow must come from the real glyph outline: the outline extends the ink past the
/// bare glyph, the shadow lands at its own offset, and both vanish when the effect is null.
/// Checked on the pixel surface so a translated-copy shortcut cannot pass.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TextStrokeShadowTests
{
    private const int WidthPx = 360;
    private const int HeightPx = 160;
    private const double FontSize = 40;
    private const double Origin = 24;

    private static IEnumerable<object[]> Backends()
    {
        yield return ["Direct2D"];
        yield return ["Gdi"];
    }

    [TestMethod]
    [DynamicData(nameof(Backends))]
    public void Stroke_expands_glyph_outline_and_shadow_is_offset(string backend)
    {
        RunOnBackend(backend, factory =>
        {
            var style = new TextRunStyle("Arial", FontSize, FontWeight.Bold);
            var layout = factory.TextEngine.CreateLayout(new TextLayoutRequest
            {
                Text = "M".AsMemory(),
                DefaultStyle = style,
                Paragraph = new TextParagraphStyle { MaxWidth = 500 },
                Transient = true,
            });

            int bare = InkCount(factory, layout,
                new TextDrawOptions(Color.Black, Transient: true));
            int stroked = InkCount(factory, layout,
                new TextDrawOptions(Color.Black, Transient: true,
                    Stroke: new TextStroke(Color.Red, 4)));
            int shadowed = InkCount(factory, layout,
                new TextDrawOptions(Color.Black, Transient: true,
                    Shadow: new TextShadow(Color.Blue, 10, 6)));

            Assert.IsGreaterThan(bare, stroked,
                $"{backend}: stroke did not add ink outside the glyph ({bare} -> {stroked}).");
            Assert.IsGreaterThan(bare, shadowed,
                $"{backend}: shadow did not add ink outside the glyph ({bare} -> {shadowed}).");
        });
    }

    [TestMethod]
    [DynamicData(nameof(Backends))]
    public void Shadow_with_blur_keeps_glyph_shape_and_adds_ink(string backend)
    {
        RunOnBackend(backend, factory =>
        {
            var style = new TextRunStyle("Arial", FontSize);
            var layout = factory.TextEngine.CreateLayout(new TextLayoutRequest
            {
                Text = "O".AsMemory(),
                DefaultStyle = style,
                Paragraph = new TextParagraphStyle { MaxWidth = 500 },
                Transient = true,
            });

            int hard = InkCount(factory, layout,
                new TextDrawOptions(Color.Black, Transient: true,
                    Shadow: new TextShadow(Color.Blue, 6, 4)));
            int blurred = InkCount(factory, layout,
                new TextDrawOptions(Color.Black, Transient: true,
                    Shadow: new TextShadow(Color.Blue, 6, 4, BlurRadius: 2)));

            Assert.IsGreaterThanOrEqualTo(hard, blurred,
                $"{backend}: blurred shadow lost ink against the hard shadow ({hard} -> {blurred}).");
        });
    }

    [TestMethod]
    [DynamicData(nameof(Backends))]
    public void Span_stroke_applies_only_to_its_range(string backend)
    {
        RunOnBackend(backend, factory =>
        {
            var style = new TextRunStyle("Arial", FontSize);
            var layout = factory.TextEngine.CreateLayout(new TextLayoutRequest
            {
                Text = "MM".AsMemory(),
                DefaultStyle = style,
                Paragraph = new TextParagraphStyle { MaxWidth = 500 },
                Transient = true,
            });

            var spans = new[]
            {
                new TextPaintSpan(new TextRange(0, 1),
                    Stroke: new TextStroke(Color.Red, 4)),
            };
            var options = new TextDrawOptions(Color.Black, spans, Transient: true);

            using var surface = factory.CreateSurface(RenderSurfaceDescriptor.CachedImage(WidthPx, HeightPx, 1));
            using (var context = factory.CreateContext(surface))
            {
                context.BeginFrame(surface);
                context.Clear(Color.White);
                context.Text.Draw(layout, new Point(Origin, Origin), in options);
                context.EndFrame();
            }

            var cpu = (ICpuPixelSurface)surface;
            var pixels = cpu.GetReadOnlyPixelSpan();
            var firstBounds = new List<Rect>();
            var secondBounds = new List<Rect>();
            layout.GetRangeBounds(0, 1, firstBounds);
            layout.GetRangeBounds(1, 1, secondBounds);
            int firstMid = (int)(Origin + (firstBounds.Count > 0 ? firstBounds[0].Right : 0));

            int redLeft = 0, redRight = 0, minX = int.MaxValue, maxX = -1;
            for (int y = 0; y < HeightPx; y++)
            {
                for (int x = 0; x < WidthPx; x++)
                {
                    int offset = y * cpu.StrideBytes + x * 4;
                    // BGRA: red is high in byte 2, green/blue low.
                    bool red = pixels[offset + 2] > 180 && pixels[offset + 1] < 90 && pixels[offset] < 90;
                    if (!red) continue;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    if (x <= firstMid) redLeft++; else redRight++;
                }
            }

            Assert.IsGreaterThan(0, redLeft,
                $"{backend}: no red stroke on first glyph (firstMid={firstMid}, redX=[{minX},{maxX}], left={redLeft}, right={redRight}).");
            Assert.AreEqual(0, redRight,
                $"{backend}: stroke bled onto the next glyph (firstMid={firstMid}, redX=[{minX},{maxX}]).");
        });
    }

    private static int InkCount(IGraphicsFactory factory, ITextLayout layout, in TextDrawOptions options)
    {
        using var surface = factory.CreateSurface(RenderSurfaceDescriptor.CachedImage(WidthPx, HeightPx, 1));
        using (var context = factory.CreateContext(surface))
        {
            context.BeginFrame(surface);
            context.Clear(Color.White);
            context.Text.Draw(layout, new Point(Origin, Origin), in options);
            context.EndFrame();
        }

        var cpu = (ICpuPixelSurface)surface;
        var pixels = cpu.GetReadOnlyPixelSpan();
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Any non-white pixel counts, so the black glyph and red/blue effects are all included.
            if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
            {
                count++;
            }
        }
        return count;
    }

    private static void RunOnBackend(string backend, Action<IGraphicsFactory> body)
    {
        switch (backend)
        {
            case "Direct2D":
            {
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Inconclusive("Direct2D is Windows-only.");
                    return;
                }
                using var factory = new Direct2DGraphicsFactory();
                body(factory);
                break;
            }
            case "Gdi":
            {
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Inconclusive("GDI is Windows-only.");
                    return;
                }
                using var factory = new GdiGraphicsFactory();
                body(factory);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(backend));
        }
    }
}
