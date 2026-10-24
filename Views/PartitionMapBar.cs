using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Replicore.Models;

namespace Replicore.Views;

/// <summary>
/// A disk's partitions drawn to scale in one horizontal bar, like Disk Management.
/// Small partitions (EFI, Reserved) get a minimum width so they stay visible; hover a
/// partition for its details.
/// </summary>
public class PartitionMapBar : Grid
{
    // Every partition gets at least this share of the bar, and a label only from LabelShare up
    private const double MinimumShare = 0.05;
    private const double LabelShare = 0.12;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<DiskSegment>), typeof(PartitionMapBar),
        new PropertyMetadata(null, (d, _) => ((PartitionMapBar)d).Rebuild()));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(PartitionMapBar),
        new PropertyMetadata("Empty disk", (d, _) => ((PartitionMapBar)d).Rebuild()));

    public IEnumerable<DiskSegment>? Segments
    {
        get => (IEnumerable<DiskSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Shown across the whole bar when there are no partitions.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public PartitionMapBar()
    {
        Height = 24;
        Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();

        var segments = Segments?.Where(s => s.Size > 0).ToList() ?? new();
        if (segments.Count == 0)
            segments.Add(new DiskSegment { Kind = SegmentKind.Unallocated, Size = 1, Title = EmptyText, Description = EmptyText });

        double total = segments.Sum(s => (double)s.Size);
        for (int i = 0; i < segments.Count; i++)
        {
            double share = segments[i].Size / total;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, MinimumShare), GridUnitType.Star) });

            var cell = BuildCell(segments[i], showLabel: share >= LabelShare);
            SetColumn(cell, i);
            Children.Add(cell);
        }
    }

    private static FrameworkElement BuildCell(DiskSegment segment, bool showLabel)
    {
        var cell = new Grid { Margin = new Thickness(1, 0, 1, 0), Background = Brushes.Transparent, ToolTip = segment.Description };
        var shape = new Rectangle { RadiusX = 3, RadiusY = 3 };
        string textBrush;
        switch (segment.Kind)
        {
            case SegmentKind.Volume:
                shape.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
                textBrush = "TextOnAccentFillColorPrimaryBrush";
                break;
            case SegmentKind.System:
                shape.SetResourceReference(Shape.FillProperty, "ControlStrongFillColorDefaultBrush");
                textBrush = "TextOnAccentFillColorPrimaryBrush";
                break;
            default:
                shape.SetResourceReference(Shape.FillProperty, "SubtleFillColorSecondaryBrush");
                shape.SetResourceReference(Shape.StrokeProperty, "ControlStrongStrokeColorDefaultBrush");
                shape.StrokeThickness = 1;
                shape.StrokeDashArray = new DoubleCollection { 3, 2 };
                textBrush = "TextFillColorSecondaryBrush";
                break;
        }
        cell.Children.Add(shape);

        if (showLabel)
        {
            var label = new TextBlock
            {
                Text = segment.Title,
                FontSize = 11,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, textBrush);
            cell.Children.Add(label);
        }
        return cell;
    }
}
