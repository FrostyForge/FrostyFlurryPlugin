using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Flurry.Editor.DataExplorer
{
    /// <summary>
    /// Virtualizing panel for the Data Explorer's tile view. Layout is a fixed-height grid
    /// of tiles; only vertical scrolling is supported, so horizontal IScrollInfo members
    /// are no-ops rather than exceptions.
    /// </summary>
    public class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
    {
        private const double TileTextRowHeight = 40.0;
        private const double WheelScrollDelta = 48.0;

        public static readonly DependencyProperty ChildSizeProperty = DependencyProperty.Register(
            nameof(ChildSize), typeof(double), typeof(VirtualizingTilePanel),
            new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public double ChildSize
        {
            get => (double)GetValue(ChildSizeProperty);
            set => SetValue(ChildSizeProperty, value);
        }

        private readonly TranslateTransform translate = new TranslateTransform();
        private ScrollViewer scrollOwner;
        private Size extent = new Size(0, 0);
        private Size viewport = new Size(0, 0);
        private double verticalOffset;
        private double actualChildWidth;
        private double actualChildHeight;
        private int childrenPerRow;

        public VirtualizingTilePanel()
        {
            RenderTransform = translate;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateScrollInfo(availableSize);
            GetVisibleRange(out int firstVisibleItemIndex, out int lastVisibleItemIndex);

            UIElementCollection children = InternalChildren;
            IItemContainerGenerator generator = ItemContainerGenerator;
            GeneratorPosition startPos = generator.GeneratorPositionFromIndex(firstVisibleItemIndex);
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int itemIndex = firstVisibleItemIndex; itemIndex <= lastVisibleItemIndex; itemIndex++, childIndex++)
                {
                    UIElement child = generator.GenerateNext(out bool newlyRealized) as UIElement;
                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count)
                            AddInternalChild(child);
                        else
                            InsertInternalChild(childIndex, child);

                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(actualChildWidth, actualChildHeight));
                }
            }

            CleanUpItems(firstVisibleItemIndex, lastVisibleItemIndex);
            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            IItemContainerGenerator generator = ItemContainerGenerator;
            UpdateScrollInfo(finalSize);

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                child.Arrange(ComputeItemRect(itemIndex));
            }

            return finalSize;
        }

        private void CleanUpItems(int minDesiredGenerated, int maxDesiredGenerated)
        {
            UIElementCollection children = InternalChildren;
            IItemContainerGenerator generator = ItemContainerGenerator;

            for (int i = children.Count - 1; i >= 0; i--)
            {
                GeneratorPosition childGeneratorPos = new GeneratorPosition(i, 0);
                int itemIndex = generator.IndexFromGeneratorPosition(childGeneratorPos);
                if (itemIndex < minDesiredGenerated || itemIndex > maxDesiredGenerated)
                {
                    generator.Remove(childGeneratorPos, 1);
                    RemoveInternalChildRange(i, 1);
                }
            }
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Reset:
                    RemoveInternalChildRange(0, InternalChildren.Count);
                    break;
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                case NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;
            }
        }

        #region Layout

        private Size CalculateExtent(Size availableSize, int itemCount)
        {
            childrenPerRow = CalculateChildrenPerRow(availableSize);
            actualChildWidth = double.IsInfinity(availableSize.Width) ? ChildSize : availableSize.Width / childrenPerRow;
            actualChildHeight = ChildSize + TileTextRowHeight;

            return new Size(childrenPerRow * actualChildWidth, actualChildHeight * Math.Ceiling((double)itemCount / childrenPerRow));
        }

        private void GetVisibleRange(out int firstVisibleItemIndex, out int lastVisibleItemIndex)
        {
            if (actualChildHeight <= 0 || childrenPerRow <= 0)
            {
                firstVisibleItemIndex = 0;
                lastVisibleItemIndex = -1;
                return;
            }

            firstVisibleItemIndex = (int)Math.Floor(verticalOffset / actualChildHeight) * childrenPerRow;
            lastVisibleItemIndex = (int)Math.Ceiling((verticalOffset + viewport.Height) / actualChildHeight) * childrenPerRow - 1;

            ItemsControl itemsControl = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsControl.HasItems ? itemsControl.Items.Count : 0;
            if (lastVisibleItemIndex >= itemCount)
                lastVisibleItemIndex = itemCount - 1;
        }

        private Rect ComputeItemRect(int itemIndex)
        {
            int row = itemIndex / childrenPerRow;
            int column = itemIndex % childrenPerRow;
            return new Rect(column * actualChildWidth, row * actualChildHeight, actualChildWidth, actualChildHeight);
        }

        private int CalculateChildrenPerRow(Size availableSize)
        {
            if (double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 || ChildSize <= 0)
                return 1;

            return Math.Max(1, (int)Math.Floor(availableSize.Width / ChildSize));
        }

        #endregion

        #region IScrollInfo

        private void UpdateScrollInfo(Size availableSize)
        {
            ItemsControl itemsControl = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsControl.HasItems ? itemsControl.Items.Count : 0;

            Size newExtent = CalculateExtent(availableSize, itemCount);
            if (newExtent != extent)
            {
                extent = newExtent;
                scrollOwner?.InvalidateScrollInfo();
            }

            if (availableSize != viewport)
            {
                viewport = availableSize;
                scrollOwner?.InvalidateScrollInfo();
            }
        }

        public ScrollViewer ScrollOwner
        {
            get => scrollOwner;
            set => scrollOwner = value;
        }

        public bool CanHorizontallyScroll { get; set; }

        public bool CanVerticallyScroll { get; set; }

        public double HorizontalOffset => 0.0;
        public double VerticalOffset => verticalOffset;
        public double ExtentHeight => extent.Height;
        public double ExtentWidth => extent.Width;
        public double ViewportHeight => viewport.Height;
        public double ViewportWidth => viewport.Width;

        public void LineUp() => SetVerticalOffset(verticalOffset - WheelScrollDelta);
        public void LineDown() => SetVerticalOffset(verticalOffset + WheelScrollDelta);
        public void MouseWheelUp() => SetVerticalOffset(verticalOffset - WheelScrollDelta);
        public void MouseWheelDown() => SetVerticalOffset(verticalOffset + WheelScrollDelta);

        public void PageUp() => SetVerticalOffset(verticalOffset - viewport.Height);
        public void PageDown() => SetVerticalOffset(verticalOffset + viewport.Height);

        public void LineLeft() { }
        public void LineRight() { }
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }
        public void PageLeft() { }
        public void PageRight() { }
        public void SetHorizontalOffset(double offset) { }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                if (!ReferenceEquals(InternalChildren[i], visual))
                    continue;

                int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                Rect slot = ComputeItemRect(itemIndex);
                SetVerticalOffset(slot.Top);
                return slot;
            }

            return rectangle;
        }

        public void SetVerticalOffset(double offset)
        {
            if (offset < 0 || viewport.Height >= extent.Height)
                offset = 0;
            else if (offset + viewport.Height >= extent.Height)
                offset = extent.Height - viewport.Height;

            verticalOffset = offset;
            translate.Y = -offset;
            scrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        #endregion
    }
}