using System.Windows;
using System.Windows.Media;
using PotatoDM.Core.Models;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace PotatoDM.App.Controls;

/// <summary>
/// Draws one cell per real segment, without creating a WPF element per cell.
/// A long HLS playlist can carry thousands of parts, so an ItemsControl would
/// allocate thousands of progress bars and update every one of them per tick.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<DownloadSegmentProgress>),
        typeof(SegmentBar),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(MediaBrush),
        typeof(SegmentBar),
        new FrameworkPropertyMetadata(
            MediaBrushes.Transparent,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush),
        typeof(MediaBrush),
        typeof(SegmentBar),
        new FrameworkPropertyMetadata(
            MediaBrushes.Orange,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DownloadSegmentProgress>? Segments
    {
        get => (IReadOnlyList<DownloadSegmentProgress>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public MediaBrush? TrackBrush
    {
        get => (MediaBrush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public MediaBrush? FillBrush
    {
        get => (MediaBrush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        var bounds = new Rect(0, 0, width, height);
        drawingContext.DrawRoundedRectangle(TrackBrush, null, bounds, radius, radius);

        var segments = Segments;

        if (segments is null || segments.Count == 0)
        {
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));

        var fill = FillBrush ?? MediaBrushes.Orange;

        if (segments.Count <= width)
        {
            for (var index = 0; index < segments.Count; index++)
            {
                var start = width * index / segments.Count;
                var end = width * (index + 1) / segments.Count;
                DrawSegment(drawingContext, fill, start, end - start, height, segments[index].Percent);
            }
        }
        else
        {
            // More segments than pixels: average adjacent segments into one
            // column. The data still contains every real segment; only the
            // raster has to collapse at this width.
            var columns = Math.Max(1, (int)Math.Floor(width));
            var fractions = new double[columns];
            var counts = new int[columns];

            for (var index = 0; index < segments.Count; index++)
            {
                var column = (int)((long)index * columns / segments.Count);
                fractions[column] += segments[index].Percent / 100.0;
                counts[column]++;
            }

            for (var column = 0; column < columns; column++)
            {
                if (counts[column] == 0)
                {
                    continue;
                }

                DrawSegment(
                    drawingContext,
                    fill,
                    column,
                    1,
                    height,
                    fractions[column] / counts[column] * 100.0);
            }
        }

        drawingContext.Pop();
    }

    private static void DrawSegment(
        DrawingContext drawingContext,
        MediaBrush fill,
        double x,
        double width,
        double height,
        double percent)
    {
        if (width <= 0 || percent <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(percent / 100.0, 0, 1);
        drawingContext.DrawRectangle(
            fill,
            null,
            new Rect(x, 0, width * fraction, height));
    }
}
