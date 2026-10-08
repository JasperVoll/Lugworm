using System;
using System.Collections.Generic;
using System.Drawing;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Lugworm.Ui;

namespace Lugworm.Samples
{
    /// <summary>A draggable value bar and a themed bitmap, hosted by inherited attributes.</summary>
    public sealed class CanvasStripAndPlotSample : IamComponentBase, ICurveEditorHost, ICanvasStrip, IGraphHost
    {
        double _value = 0.5;
        double _dragStart;
        bool _hover;
        RectangleF _track;
        Bitmap? _graph;

        public CanvasStripAndPlotSample()
            : base("Canvas strip and plot", "Strip", "Drag a value from 0 to 1 and plot it.",
                   "Lugworm Samples", "06 Canvas strip and plot") { }

        public override Guid ComponentGuid => new Guid("6ffcfe3d-7b74-4280-baaa-48b09d31ff11");
        protected override IReadOnlyList<ParamSpec> InputSpecs => Array.Empty<ParamSpec>();
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
            => pManager.AddNumberParameter("Value", "V", "Selected value from 0 to 1", GH_ParamAccess.item);
        public ICanvasStrip CurveEditor => this;
        public Bitmap? GraphImage => _graph;
        public int StripHeight => 34;
        public int StripWidth => IamPlot.PanelWidth;
        public bool Dragging { get; private set; }

        public void Render(Graphics g, RectangleF rect)
        {
            var theme = IamPlot.Current();
            _track = new RectangleF(rect.X + 8, rect.Y + 17, rect.Width - 16, 10);
            IamPlot.Small(g, theme, rect.X + 8, rect.Y + 3,
                          $"{(_hover || Dragging ? "Drag value" : "Value")}: {_value:0.00}");
            // IamAttributes supplies the capsule background. Draw only our content.
            IamPlot.HBar(g, theme, Rectangle.Round(_track), _value,
                         _hover || Dragging ? theme.Accent : theme.Series);
        }

        public bool MouseDown(PointF p)
        {
            if (!_track.Contains(p) || Locked) return false;
            RecordUndoEvent("Change strip value");
            _dragStart = _value;
            Dragging = true; // IamAttributes captures the mouse until MouseUp.
            MouseMove(p);
            Grasshopper.Instances.RedrawCanvas();
            return true;
        }

        public bool MouseMove(PointF p)
        {
            if (!Dragging || _track.Width <= 0) return false;
            double next = Math.Max(0, Math.Min(1, (p.X - _track.Left) / _track.Width));
            if (next == _value) return false;
            _value = next;
            return true;
        }

        public bool MouseUp()
        {
            bool changed = Dragging && _value != _dragStart;
            Dragging = false;
            if (changed) ExpireSolution(true); // One solve when the gesture finishes.
            return changed;
        }

        public bool MouseHover(PointF p)
        {
            bool hover = _track.Contains(p);
            if (hover == _hover) return false;
            _hover = hover;
            return true;
        }
        public bool MouseDoubleClick(PointF p) => false;

        protected override object Compute(IGH_DataAccess DA) => _value;
        protected override void Emit(IGH_DataAccess DA, object result)
        {
            double value = (double)result;
            var theme = IamPlot.Current();
            var bitmap = IamPlot.NewPanel(theme, 140, out var g);
            using (g)
            {
                var plot = IamPlot.Frame2D(g, theme, new Rectangle(8, 4, 354, 96),
                                          "Value", "Value", 0, 1, 0, 1);
                var point = IamPlot.Map(plot, value, 0, 1, value, 0, 1);
                using (var brush = new SolidBrush(theme.Series))
                    g.FillEllipse(brush, point.X - 3, point.Y - 3, 6, 6);
                IamPlot.HBar(g, theme, new Rectangle(24, 106, 330, 8), value, theme.Series);
                IamPlot.Legend(g, theme, 24, 122, (theme.Series, "Selected value"));
            }
            _graph?.Dispose();
            _graph = bitmap;
            Attributes?.ExpireLayout();
            DA.SetData(0, value);
        }

        // Serialization also provides the state restored by RecordUndoEvent.
        public override bool Write(GH_IWriter writer)
        {
            writer.SetDouble("SampleValue", _value);
            return base.Write(writer);
        }
        public override bool Read(GH_IReader reader)
        {
            reader.TryGetDouble("SampleValue", ref _value);
            _value = double.IsNaN(_value) ? 0.5 : Math.Max(0, Math.Min(1, _value));
            Dragging = false;
            return base.Read(reader);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _graph?.Dispose();
            _graph = null;
            base.RemovedFromDocument(document);
        }
        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close)
            {
                _graph?.Dispose();
                _graph = null;
            }
            base.DocumentContextChanged(document, context);
        }
    }
}
