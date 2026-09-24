using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;

namespace Lugworm.Core.Machine
{
    /// <summary>How loudly a refused action is reported on the canvas. A gate that is
    /// open, or one whose refusal is simply the operator's own doing, says nothing;
    /// a refusal the operator has to act on is orange.</summary>
    public enum GateSeverity { None = 0, Remark = 1, Warning = 2 }

    /// <summary>One gate: the actions it covers, why they are unavailable (null when
    /// they are available) and how loudly to say so.</summary>
    public readonly struct MachineActionGate
    {
        public MachineActionGate(string? actions, string? reason, GateSeverity level)
        { Actions = actions ?? ""; Reason = reason; Level = level; }

        /// <summary>The action names this gate covers, as shown to the operator.</summary>
        public string Actions { get; }

        /// <summary>Why the actions are unavailable, or null when they are not gated.</summary>
        public string? Reason { get; }

        public GateSeverity Level { get; }
    }

    /// <summary>Everything the gate policy needs to know about a machine session, as
    /// plain data. The plugin reads it once from its live <c>MachineSession</c>; Core
    /// never sees the session, the connection enum or Grasshopper.</summary>
    public sealed class MachineActionState
    {
        /// <summary>An action of this component is already running.</summary>
        public bool Busy;

        /// <summary>A session object exists for the host.</summary>
        public bool HasSession;

        /// <summary>The session is fully connected.</summary>
        public bool Connected;

        /// <summary>The connection state as one lowercase word ("connecting",
        /// "reconnecting", "error", ...), reported when it is not Connected.</summary>
        public string ConnectionState = "";

        /// <summary>Machine-identity refusal (AUD-SAF-004), or null when the connected
        /// machine is the trusted one.</summary>
        public string? TrustRefusalReason;

        /// <summary>The session knows which way the arm is folded - a verified home
        /// establishes it, anything that could have moved the arm blind clears it.</summary>
        public bool ArmConfigurationKnown;

        /// <summary>The latest polled object-model snapshot, or null.</summary>
        public ObjectModelSnapshot? Snapshot;
    }

    /// <summary>One runtime message the plugin has to raise: the text and its level.</summary>
    public sealed class GateMessage
    {
        public GateMessage(string text, GateSeverity level) { Text = text; Level = level; }

        public string Text { get; }
        public GateSeverity Level { get; }

        public override string ToString() => Level + ": " + Text;
    }

    /// <summary>The gate set and the severity policy of the machine-control components
    /// (4.4.0, D8), lifted out of <c>MachineComponentBase</c> so both the gates and
    /// their reporting can be tested without Rhino. The plugin keeps the live session,
    /// the capsule enablement and <c>AddRuntimeMessage</c>; the decision of WHICH gate
    /// refuses, with WHAT reason and at WHAT severity, is here.
    ///
    /// The 1.12.0 rule this exists to protect: an unhomed Print component must WARN
    /// rather than silently grey its buttons, and the warning must disappear by itself
    /// once a verified home has established the arm configuration.</summary>
    public static class ActionGatePolicy
    {
        /// <summary>Runtime messages are capped: a long refusal must not push every
        /// other message off the component's balloon.</summary>
        public const int MaxMessageLength = 240;

        /// <summary>The connection gate every other gate starts from: busy, no session,
        /// not connected, or a machine whose identity is not the trusted one.</summary>
        public static MachineActionGate ConnectionGate(MachineActionState state, string actions)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Busy)
                return new MachineActionGate(actions, "another action is executing", GateSeverity.Remark);
            if (!state.HasSession)
                return new MachineActionGate(actions, "not connected - press Connect", GateSeverity.Remark);
            if (!state.Connected)
                return new MachineActionGate(actions,
                    "connection is " + state.ConnectionState, GateSeverity.Remark);
            // machine-identity trust (AUD-SAF-004): a mismatch or unprobed identity
            // keeps the connection diagnostic-only; the transaction re-checks this
            if (state.TrustRefusalReason is string trustReason)
                return new MachineActionGate(actions, trustReason, GateSeverity.Warning);
            return new MachineActionGate(actions, null, GateSeverity.None);
        }

        /// <summary>The connection gate followed by the machine-state freshness gate
        /// (<see cref="StateGate.Refuse"/>). A state refusal is always a warning - the
        /// operator has to do something about it.</summary>
        public static MachineActionGate MachineStateGate(MachineActionState state, string actions,
                                                         bool requireIdle = true, bool allowPaused = false,
                                                         bool requireHomed = true,
                                                         DateTime? notBeforeUtc = null,
                                                         string activity = "this action")
        {
            var connection = ConnectionGate(state, actions);
            if (connection.Reason != null) return connection;
            string? reason = StateGate.Refuse(state.Snapshot, requireIdle: requireIdle,
                allowPaused: allowPaused, requireHomed: requireHomed,
                notBeforeUtc: notBeforeUtc, activity: activity);
            return new MachineActionGate(actions, reason,
                reason == null ? GateSeverity.None : GateSeverity.Warning);
        }

        /// <summary>The arm-configuration gate: a SCARA move whose elbow branch is
        /// unknown is refused loudly, because the two branches sweep different volumes.
        /// Deliberately independent of idle/homed freshness - it asks only whether the
        /// session still knows which way the arm is folded.</summary>
        public static MachineActionGate ArmConfigurationGate(MachineActionState state, string actions,
                                                             string activity = "this action")
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!state.HasSession)
                return new MachineActionGate(actions, "not connected - press Connect", GateSeverity.Remark);
            return state.ArmConfigurationKnown
                ? new MachineActionGate(actions, null, GateSeverity.None)
                : new MachineActionGate(actions,
                    activity + " refused - arm configuration is Unknown; complete a verified home first",
                    GateSeverity.Warning);
        }

        /// <summary>The messages one solve must raise for a component's gate set: open
        /// gates say nothing, gates sharing a reason are merged into one message naming
        /// every action, and the loudest severity of the merged group wins. Order is
        /// the order the reasons were first seen.</summary>
        public static IReadOnlyList<GateMessage> Report(IEnumerable<MachineActionGate>? gates)
        {
            var messages = new List<GateMessage>();
            if (gates == null) return messages;

            var grouped = new Dictionary<string, MachineActionGate>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var gate in gates)
            {
                if (gate.Level == GateSeverity.None || string.IsNullOrEmpty(gate.Reason)) continue;
                string reason = gate.Reason!;
                string actions = string.IsNullOrWhiteSpace(gate.Actions) ? "action" : gate.Actions;
                if (grouped.TryGetValue(reason, out var existing))
                {
                    GateSeverity level = existing.Level == GateSeverity.Warning || gate.Level == GateSeverity.Warning
                        ? GateSeverity.Warning : GateSeverity.Remark;
                    grouped[reason] = new MachineActionGate(existing.Actions + ", " + actions, reason, level);
                }
                else
                {
                    grouped.Add(reason, new MachineActionGate(actions, reason, gate.Level));
                    order.Add(reason);
                }
            }

            foreach (string reason in order)
            {
                var gate = grouped[reason];
                messages.Add(new GateMessage(Cap(gate.Actions + " unavailable: " + gate.Reason), gate.Level));
            }
            return messages;
        }

        /// <summary>Caps one runtime message at <see cref="MaxMessageLength"/>.</summary>
        public static string Cap(string text)
        {
            if (text == null) return "";
            return text.Length <= MaxMessageLength
                ? text : text.Substring(0, MaxMessageLength - 3) + "...";
        }
    }
}
