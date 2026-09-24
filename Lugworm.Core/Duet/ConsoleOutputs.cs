using System;
using System.Collections.Generic;

namespace Lugworm.Core.Duet
{
    /// <summary>The Duet Console's output NAMES, in one place. A saved document pins
    /// every one of them (Grasshopper reconnects a wire by socket name), so these strings
    /// are a contract: they may be added to, never renamed.</summary>
    public static class ConsoleOutputNames
    {
        public const string Host = "host";
        public const string Connected = "connected";

        public const string Status = "status";
        public const string State = "state";
        public const string Progress = "progress";
        public const string JobFile = "jobFile";
        public const string Uploaded = "uploaded";
        public const string Gcode = "gcode";

        public const string Pos = "pos";
        public const string Homed = "homed";
        public const string XHomed = "xHomed";
        public const string YHomed = "yHomed";
        public const string ZHomed = "zHomed";
        public const string BedOrigin = "bedOrigin";
        public const string ZZero = "zZero";

        public const string Danger = "danger";
        public const string Problems = "problems";
        public const string Messages = "messages";

        public const string SpeedFactor = "speedFactor";
        public const string FlowFactor = "flowFactor";

        public const string Log = "log";
        public const string Info = "info";
        public const string Reply = "reply";

        /// <summary>What a NEW console shows: the host to wire the other components with,
        /// one status line, and the effective job's G-code. Everything else is a category
        /// the operator turns on when they need it.</summary>
        public static readonly IReadOnlyList<string> Default = new[] { Host, Status, Gcode };
    }

    /// <summary>The Console's output CATEGORIES. The component turns each one on or off
    /// from its context menu; Core needs the names because the expire policy and the
    /// output projection are decided here.</summary>
    public static class ConsoleOutputCategories
    {
        public const string Connection = "outConnection";
        public const string Job = "outJob";
        public const string Motion = "outMotion";
        public const string Validation = "outValidation";
        public const string Overrides = "outTemperatures";
        public const string Diagnostics = "outDiagnostics";
    }

    /// <summary>Which of the Console's outputs are actually registered on the component
    /// right now. Immutable and comparable, because it takes part in the expire decision:
    /// a value nobody can see must never re-solve the canvas.</summary>
    public sealed class OutputVisibility : IEquatable<OutputVisibility>
    {
        /// <summary>Everything is visible. The default, so every existing caller of
        /// <see cref="PrintConsoleOutputs.From(PrintConsoleState)"/> keeps its behaviour.</summary>
        public static readonly OutputVisibility All = new OutputVisibility(null);

        readonly HashSet<string>? _names;   // null = all

        OutputVisibility(HashSet<string>? names) { _names = names; }

        public static OutputVisibility Of(params string[] names)
            => Of((IEnumerable<string>)(names ?? Array.Empty<string>()));

        public static OutputVisibility Of(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (names != null) foreach (var n in names) if (!string.IsNullOrEmpty(n)) set.Add(n);
            return new OutputVisibility(set);
        }

        /// <summary>True when this output exists on the component and its value therefore
        /// matters to a downstream definition.</summary>
        public bool Shows(string name) => _names == null || (name != null && _names.Contains(name));

        /// <summary>Number of visible outputs, or -1 for <see cref="All"/> (which does not
        /// know how many outputs there are).</summary>
        public int Count => _names?.Count ?? -1;

        public bool Equals(OutputVisibility? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (_names == null || other._names == null) return _names == null && other._names == null;
            return _names.Count == other._names.Count && _names.SetEquals(other._names);
        }

        public override bool Equals(object? obj) => Equals(obj as OutputVisibility);

        public override int GetHashCode()
        {
            if (_names == null) return 0;
            unchecked
            {
                // order-independent: a set has no order
                int h = 19 + _names.Count;
                foreach (var n in _names) h ^= n.GetHashCode();
                return h;
            }
        }
    }
}
