using System;
using System.Windows;
using System.Windows.Controls;

namespace DiagKit.Controls;

/// <summary>
/// Lays tiles out in as many equal columns as fit, stretching them to fill the row, so the grid uses the whole
/// window at any size instead of leaving a ragged gap on the right. Collapsed tiles (filtered out by search) take no space.
/// </summary>
public sealed class TileGrid : Panel
{
    public static readonly DependencyProperty MinTileWidthProperty = DependencyProperty.Register(
        nameof(MinTileWidth), typeof(double), typeof(TileGrid),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(TileGrid),
        new FrameworkPropertyMetadata(96.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(TileGrid),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinTileWidth
    {
        get => (double)GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int _columns = 1;
    private double _tileWidth;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinTileWidth * 3 + Spacing * 2 : available.Width;
        _columns = Math.Max(1, (int)((width + Spacing) / (MinTileWidth + Spacing)));
        _tileWidth = Math.Max(0, (width - Spacing * (_columns - 1)) / _columns);

        int visible = 0;
        var tileSize = new Size(_tileWidth, TileHeight);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(tileSize);
            if (child.Visibility != Visibility.Collapsed)
            {
                visible++;
            }
        }

        int rows = (visible + _columns - 1) / _columns;
        return new Size(width, rows == 0 ? 0 : rows * TileHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect());
                continue;
            }

            int row = i / _columns;
            int col = i % _columns;
            child.Arrange(new Rect(col * (_tileWidth + Spacing), row * (TileHeight + Spacing), _tileWidth, TileHeight));
            i++;
        }

        return finalSize;
    }
}
