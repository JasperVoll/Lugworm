using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    public enum PathRole { OpenSection, Wall, Infill }

    public sealed class TaggedPath
    {
        public List<Vec3> Points = new List<Vec3>();
        public PathRole Role;
        public int WallIndex = -1;
        public bool Closed;
        /// <summary>Vertical offset, mm, a whole-stack pass (Bricklayer) has already applied
        /// to this path relative to its layer level; 0 when untouched. Read by the non-planar
        /// wall guard, which must see the real gap to the same loop on the next layer.</summary>
        public double ZShift;
    }

    public sealed class LayerPaths
    {
        public List<TaggedPath> Paths = new List<TaggedPath>();
        public double Level;
        public int Index;
        public string? Diagnostic;

        public List<List<Vec3>> Flatten()
        {
            var flattened = new List<List<Vec3>>(Paths.Count);
            foreach (var path in Paths) flattened.Add(path.Points);
            return flattened;
        }
    }
}
