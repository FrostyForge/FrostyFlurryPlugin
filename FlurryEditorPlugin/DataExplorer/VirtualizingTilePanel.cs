using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Flurry.Editor.DataExplorer
{
    public class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
    {
        public VirtualizingTilePanel()
        {
            this.RenderTransform = _trans;
        }

        public static readonly DependencyProperty ChildSizeProperty
           = DependencyProperty.RegisterAttached("ChildSize", typeof(double), typeof(VirtualizingTilePanel),
              new FrameworkPropertyMetadata(200.0d, FrameworkPropertyMetadataOptions.AffectsMeasure |
              FrameworkPropertyMetadataOptions.AffectsArrange));

        public double ChildSize
        {
            get => (double)GetValue(ChildSizeProperty);
            set => SetValue(ChildSizeProperty, value);
        }

        private double actualChildWidth;
        private double actualChildHeight;
        private int childrenPerRow;

        protected override Size MeasureOverride(Size availableSize)
        {
            UpdateScrollInfo(availableSize);

            GetVisibleRange(out int firstVisibleItemIndex, out int lastVisibleItemIndex);

            UIElementCollection children = this.InternalChildren;
            IItemContainerGenerator generator = this.ItemContainerGenerator;

            GeneratorPosition startPos = generator.GeneratorPositionFromIndex(firstVisibleItemIndex);

            int childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;

            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int itemIndex = firstVisibleItemIndex; itemIndex <= lastVisibleItemIndex; ++itemIndex, ++childIndex)
                {
                    UIElement child = generator.GenerateNext(out bool newlyRealized) as UIElement;
                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count)
                        {
                            base.AddInternalChild(child);
                        }
                        else
                        {
                            base.InsertInternalChild(childIndex, child);
                        }
                        generator.PrepareItemContainer(child);
                    }
                    else
                    {
                        Debug.Assert(child == children[childIndex], "Wrong child was generated");
                    }

                    child.Measure(GetChildSize());
                }
            }

            CleanUpItems(firstVisibleItemIndex, lastVisibleItemIndex);

            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            IItemContainerGenerator generator = this.ItemContainerGenerator;

            UpdateScrollInfo(finalSize);

            for (int i = 0; i < this.Children.Count; i++)
            {
                UIElement child = this.Children[i];

                int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));

                ArrangeChild(itemIndex, child, finalSize);
            }

            return finalSize;
        }

        private void CleanUpItems(int minDesiredGenerated, int maxDesiredGenerated)
        {
            UIElementCollection children = this.InternalChildren;
            IItemContainerGenerator generator = this.ItemContainerGenerator;

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
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                case NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;
            }
        }

        #region Layout specific code

        private Size CalculateExtent(Size availableSize, int itemCount)
        {
            childrenPerRow = CalculateChildrenPerRow(availableSize);

            actualChildWidth = availableSize.Width / childrenPerRow;
            actualChildHeight = ChildSize + 40;

            return new Size(childrenPerRow * actualChildWidth,
                actualChildHeight * Math.Ceiling((double)itemCount / childrenPerRow));
        }

        private void GetVisibleRange(out int firstVisibleItemIndex, out int lastVisibleItemIndex)
        {
            firstVisibleItemIndex = (int)Math.Floor(_offset.Y / actualChildHeight) * childrenPerRow;
            lastVisibleItemIndex = (int)Math.Ceiling((_offset.Y + _viewport.Height) / actualChildHeight) * childrenPerRow - 1;

            ItemsControl itemsControl = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsControl.HasItems ? itemsControl.Items.Count : 0;
            if (lastVisibleItemIndex >= itemCount)
                lastVisibleItemIndex = itemCount - 1;
        }

        private Size GetChildSize()
        {
            return new Size(actualChildWidth, actualChildHeight);
        }

        private void ArrangeChild(int itemIndex, UIElement child, Size finalSize)
        {
            int row = itemIndex / childrenPerRow;
            int column = itemIndex % childrenPerRow;

            child.Arrange(new Rect(column * actualChildWidth, row * actualChildHeight, actualChildWidth, actualChildHeight));
        }

        private int CalculateChildrenPerRow(Size availableSize)
        {
            int childrenPerRow;
            if (availableSize.Width == Double.PositiveInfinity)
                childrenPerRow = this.Children.Count;
            else
                childrenPerRow = Math.Max(1, (int)Math.Floor(availableSize.Width / this.ChildSize));
            return childrenPerRow;
        }

        #endregion

        #region IScrollInfo implementation

        private void UpdateScrollInfo(Size availableSize)
        {
            ItemsControl itemsControl = ItemsControl.GetItemsOwner(this);
            int itemCount = itemsControl.HasItems ? itemsControl.Items.Count : 0;

            Size extent = CalculateExtent(availableSize, itemCount);
            if (extent != _extent)
            {
                _extent = extent;
                _owner?.InvalidateScrollInfo();
            }

            if (availableSize != _viewport)
            {
                _viewport = availableSize;
                _owner?.InvalidateScrollInfo();
            }
        }

        public ScrollViewer ScrollOwner
        {
            get => _owner;
            set => _owner = value;
        }

        public bool CanHorizontallyScroll
        {
            get => _canHScroll;
            set => _canHScroll = value;
        }

        public bool CanVerticallyScroll
        {
            get => _canVScroll;
            set => _canVScroll = value;
        }

        public double HorizontalOffset => _offset.X;
        public double VerticalOffset => _offset.Y;
        public double ExtentHeight => _extent.Height;
        public double ExtentWidth => _extent.Width;
        public double ViewportHeight => _viewport.Height;
        public double ViewportWidth => _viewport.Width;

        public void LineUp() => SetVerticalOffset(this.VerticalOffset - 10);
        public void LineDown() => SetVerticalOffset(this.VerticalOffset + 10);

        public void PageUp() => SetVerticalOffset(this.VerticalOffset - _viewport.Height);
        public void PageDown() => SetVerticalOffset(this.VerticalOffset + _viewport.Height);

        public void MouseWheelUp() => SetVerticalOffset(this.VerticalOffset - 10);
        public void MouseWheelDown() => SetVerticalOffset(this.VerticalOffset + 10);

        public Rect MakeVisible(Visual visual, Rect rectangle) => new Rect();

        public void LineLeft() => throw new InvalidOperationException();
        public void LineRight() => throw new InvalidOperationException();

        public void MouseWheelLeft() => throw new InvalidOperationException();
        public void MouseWheelRight() => throw new InvalidOperationException();

        public void PageLeft() => throw new InvalidOperationException();
        public void PageRight() => throw new InvalidOperationException();

        public void SetHorizontalOffset(double offset) => throw new InvalidOperationException();
        public void SetVerticalOffset(double offset)
        {
            if (offset < 0 || _viewport.Height >= _extent.Height)
            {
                offset = 0;
            }
            else
            {
                if (offset + _viewport.Height >= _extent.Height)
                {
                    offset = _extent.Height - _viewport.Height;
                }
            }

            _offset.Y = offset;

            _owner?.InvalidateScrollInfo();

            _trans.Y = -offset;

            InvalidateMeasure();
        }

        private TranslateTransform _trans = new TranslateTransform();
        private ScrollViewer _owner;
        private bool _canHScroll = false;
        private bool _canVScroll = false;
        private Size _extent = new Size(0, 0);
        private Size _viewport = new Size(0, 0);
        private Point _offset;

        #endregion
    }
}
