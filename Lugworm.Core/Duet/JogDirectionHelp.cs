using System;

namespace Lugworm.Core.Duet
{
    /// <summary>Direction help for the console's jog pad (finding U1, checklist row 8.3).
    /// The pad's buttons are labelled with MACHINE axes, because that is what the firmware
    /// moves and what the Position card reads back; the document the operator is looking at
    /// is in the RHINO frame, and on a SCARA the two are not the same. An operator who
    /// cannot see which way "X+" goes finds out by pressing it, which is the one way of
    /// finding out that can hit the print.</summary>
    /// <remarks>
    /// <para>Pure text, and deliberately in Core: the wording is the whole feature, so it
    /// is pinned by tests rather than typed into a page renderer.</para>
    /// <para>Three rules the text may never break. It names a machine-coordinate INCREMENT
    /// first, because that is the only thing the button is guaranteed to do. It names Rhino
    /// AXES, never a camera-relative left/right/up/down - the viewport can be rotated, so
    /// "left" is not a fact about anything. And when the mechanism is not established it
    /// says the mapping is unknown instead of guessing: the Cartesian identity and the
    /// mirrored SCARA frame send the nozzle in different directions, so a wrong promise is
    /// worse than no promise.</para>
    /// </remarks>
    public static class JogDirectionHelp
    {
        /// <summary>The SCARA machine frame is mirrored about y = -x relative to the Rhino
        /// frame (Rhino X = -machine Y, Rhino Y = -machine X - the same mapping Live View
        /// and the position output use). A machine axis therefore moves the nozzle along
        /// the OTHER Rhino axis, with the sign flipped.</summary>
        const string ScaraFrame = "This machine's frame is mirrored about y = -x, so machine ";

        /// <summary>A Cartesian profile maps machine to Rhino as the identity.</summary>
        const string CartesianFrame = "This machine's Cartesian axes match the Rhino frame, so machine ";

        /// <summary>Said whenever a Rhino direction is named at all. Operators read a drawn
        /// arrow as a screen direction; the axis it names is a document fact, the screen
        /// direction is not.</summary>
        const string Caveat = " Rhino axes, not screen directions - the viewport can be rotated.";

        /// <summary>The tooltip for one jog hit-target.</summary>
        /// <param name="id">the console hit-target id: "jogxplus", "jogxminus", "jogyplus",
        /// "jogyminus", "jogzplus", "jogzminus". Compared ordinally, like every other
        /// console action id.</param>
        /// <param name="cartesian">true when the machine's Cartesian axes map to the Rhino
        /// frame as identity; false for the SCARA frame. Ignored when
        /// <paramref name="frameKnown"/> is false.</param>
        /// <param name="frameKnown">false when the mechanism is not established (an
        /// unresolved robot profile) - then the text describes the machine axis only and
        /// claims no Rhino mapping.</param>
        /// <param name="refusalReason">why the control is currently disabled, or null. It
        /// is appended so that adding directional help can never hide the reason a press
        /// would do nothing.</param>
        /// <returns>the tooltip, or null when the id is not a jog control.</returns>
        public static string? Describe(string? id, bool cartesian, bool frameKnown, string? refusalReason)
        {
            char axis;
            bool positive;
            switch (id)
            {
                case "jogxplus": axis = 'X'; positive = true; break;
                case "jogxminus": axis = 'X'; positive = false; break;
                case "jogyplus": axis = 'Y'; positive = true; break;
                case "jogyminus": axis = 'Y'; positive = false; break;
                case "jogzplus": axis = 'Z'; positive = true; break;
                case "jogzminus": axis = 'Z'; positive = false; break;
                default: return null;
            }

            string sign = positive ? "+" : "-";
            // The step is named as "the selected size" rather than a number: the segmented
            // step selector is right above the pad, and a number baked in here would go
            // stale the moment the operator changed it.
            string text = "Jogs machine " + axis + " by " + sign + "1 step of the selected size, in machine coordinates. "
                        + Mapping(axis, positive, cartesian, frameKnown);
            if (frameKnown) text += Caveat;
            if (!string.IsNullOrWhiteSpace(refusalReason))
                text += " Jogging is unavailable: " + refusalReason!.Trim();
            return text;
        }

        static string Mapping(char axis, bool positive, bool cartesian, bool frameKnown)
        {
            if (!frameKnown)
                return "The mechanism is not established yet, so which way that is in Rhino is unknown.";
            string sign = positive ? "+" : "-";
            if (cartesian)
            {
                // identity, so the up/down note only applies to Z
                string suffix = axis == 'Z' ? (positive ? " (up)" : " (down)") : "";
                return CartesianFrame + axis + sign + " moves toward Rhino " + sign + axis + suffix + ".";
            }
            if (axis == 'Z')
            {
                // The mirror is about a vertical plane, so it leaves Z alone. Saying so
                // explicitly stops the operator from assuming Z is flipped as well.
                return "Z is not mirrored: machine Z" + sign + " is Rhino " + sign + "Z, "
                     + (positive ? "up" : "down") + " in both frames.";
            }
            // Rhino X = -machine Y and Rhino Y = -machine X: the other axis, opposite sign.
            char rhinoAxis = axis == 'X' ? 'Y' : 'X';
            string rhinoSign = positive ? "-" : "+";
            return ScaraFrame + axis + sign + " moves toward Rhino " + rhinoSign + rhinoAxis + ".";
        }
    }
}
