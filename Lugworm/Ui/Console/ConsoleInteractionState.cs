using System;
using System.Collections.Generic;

namespace Lugworm.Ui.Console
{
    /// <summary>Hot, pressed and time-bounded animation state of the drawn controls. No
    /// machine facts live here. Every transition is short (120-160 ms) and the owner
    /// invalidates only while <see cref="Animating"/> is true; at rest nothing repaints.
    /// Reduced motion removes the interpolation and uses immediate state changes.</summary>
    public sealed class ConsoleInteractionState
    {
        public const double HoverMs = 120, PressMs = 90, NavMs = 150, CrossfadeMs = 160;

        readonly Dictionary<string, (double start, bool entering)> _hover = new Dictionary<string, (double, bool)>(StringComparer.Ordinal);
        double _navStart = double.NegativeInfinity;
        int _navFrom, _navTo;
        string? _pressedId;

        /// <summary>Monotonic milliseconds supplied by the form before each paint.</summary>
        public double Now { get; set; }
        public bool ReducedMotion { get; set; }
        public string? HotId { get; private set; }
        /// <summary>True while any transition is in flight - the form's animation timer runs only then.</summary>
        public bool Animating { get; private set; }

        /// <summary>The control under the mouse changed (null = none).</summary>
        public void SetHot(string? id)
        {
            if (string.Equals(id, HotId, StringComparison.Ordinal)) return;
            if (HotId != null) _hover[HotId] = (Now, false);
            if (id != null) _hover[id] = (Now, true);
            HotId = id;
        }

        public void Press(string? id)
        {
            _pressedId = id;
        }

        public void Release() { _pressedId = null; }

        /// <summary>Start of the navigation indicator slide.</summary>
        public void Navigate(int from, int to)
        {
            if (from == to) return;
            _navFrom = from; _navTo = to; _navStart = Now;
        }

        /// <summary>0..1 hover blend for a control; 1 immediately under reduced motion.</summary>
        public float Hover(string id)
        {
            bool hot = string.Equals(id, HotId, StringComparison.Ordinal);
            if (ReducedMotion) return hot ? 1f : 0f;
            if (!_hover.TryGetValue(id, out var h)) return hot ? 1f : 0f;
            double t = Math.Min(1.0, Math.Max(0.0, (Now - h.start) / HoverMs));
            float eased = (float)(1 - (1 - t) * (1 - t));
            return h.entering ? eased : 1f - eased;
        }

        /// <summary>1 while pressed (immediate; never delays dispatch).</summary>
        public bool IsPressed(string id) => string.Equals(id, _pressedId, StringComparison.Ordinal);

        /// <summary>Navigation indicator position in item units (from -> to).</summary>
        public float NavPosition(int active)
        {
            if (ReducedMotion || double.IsNegativeInfinity(_navStart) || _navTo != active) return active;
            double t = Math.Min(1.0, (Now - _navStart) / NavMs);
            float eased = (float)(1 - Math.Pow(1 - t, 3));
            return _navFrom + (_navTo - _navFrom) * eased;
        }

        /// <summary>Phase 0..1 of the pending-command spinner (a slow, honest rotation).</summary>
        public float SpinnerPhase => (float)((Now % 1200.0) / 1200.0);

        /// <summary>Called after each paint: decides whether another frame is needed.</summary>
        public void AfterPaint(bool pendingIndicator)
        {
            bool live = pendingIndicator && !ReducedMotion;
            if (!ReducedMotion)
            {
                if (Now - _navStart < NavMs) live = true;
                var done = new List<string>();
                foreach (var kv in _hover)
                {
                    if (Now - kv.Value.start < HoverMs) live = true;
                    else if (!kv.Value.entering) done.Add(kv.Key);
                }
                foreach (var k in done) _hover.Remove(k);
            }
            else _hover.Clear();
            Animating = live;
        }
    }
}
