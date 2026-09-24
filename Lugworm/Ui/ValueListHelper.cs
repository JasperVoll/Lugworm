using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Lugworm.Ui
{
    /// <summary>Drops a pre-labeled GH Value List next to an input param and wires it
    /// up - so enums (mode, infill, seam, ...) read as named dropdowns instead of
    /// naked integer sliders.</summary>
    public static class ValueListHelper
    {
        public static void Attach(GH_Component comp, IGH_Param target, (string label, string val)[] items)
        {
            var doc = comp.OnPingDocument();
            if (doc == null || target == null) return;

            var vl = new GH_ValueList { NickName = target.NickName };
            vl.ListItems.Clear();
            foreach (var (label, val) in items)
                vl.ListItems.Add(new GH_ValueListItem(label, val));
            vl.CreateAttributes();

            // place to the left of the target input; a socket that has not been laid
            // out yet (empty bounds) would otherwise put the list at the top of the canvas
            float x = comp.Attributes.Bounds.Left - 140f;
            float y = target.Attributes != null && !target.Attributes.Bounds.IsEmpty
                ? target.Attributes.Bounds.Top : comp.Attributes.Bounds.Top;
            vl.Attributes.Pivot = new PointF(x, y);

            doc.AddObject(vl, false);
            target.AddSource(vl);
            target.CollectData();
            comp.ExpireSolution(true);
        }
    }
}
