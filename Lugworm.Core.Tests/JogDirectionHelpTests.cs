using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>U1 (checklist row 8.3): the jog pad's direction help. The operator passed the
/// jog test but could not tell which way +X and +Y actually go, because the console draws
/// MACHINE axes while the document is in the Rhino frame. These tests pin the exact text,
/// because the whole value of the tooltip is that it names the right axis and sign.</summary>
public class JogDirectionHelpTests
{
    const string Caveat = " Rhino axes, not screen directions - the viewport can be rotated.";

    [Fact]
    public void The_scara_frame_is_mirrored_about_y_equals_minus_x()
    {
        // Rhino X = -machine Y and Rhino Y = -machine X, so a machine axis moves the
        // nozzle along the OTHER Rhino axis, with the sign flipped.
        Assert.Equal("Jogs machine X by +1 step of the selected size, in machine coordinates. "
                   + "This machine's frame is mirrored about y = -x, so machine X+ moves toward Rhino -Y." + Caveat,
                     JogDirectionHelp.Describe("jogxplus", cartesian: false, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine X by -1 step of the selected size, in machine coordinates. "
                   + "This machine's frame is mirrored about y = -x, so machine X- moves toward Rhino +Y." + Caveat,
                     JogDirectionHelp.Describe("jogxminus", cartesian: false, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine Y by +1 step of the selected size, in machine coordinates. "
                   + "This machine's frame is mirrored about y = -x, so machine Y+ moves toward Rhino -X." + Caveat,
                     JogDirectionHelp.Describe("jogyplus", cartesian: false, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine Y by -1 step of the selected size, in machine coordinates. "
                   + "This machine's frame is mirrored about y = -x, so machine Y- moves toward Rhino +X." + Caveat,
                     JogDirectionHelp.Describe("jogyminus", cartesian: false, frameKnown: true, refusalReason: null));
    }

    [Fact]
    public void The_cartesian_frame_is_the_identity()
    {
        Assert.Equal("Jogs machine X by +1 step of the selected size, in machine coordinates. "
                   + "This machine's Cartesian axes match the Rhino frame, so machine X+ moves toward Rhino +X." + Caveat,
                     JogDirectionHelp.Describe("jogxplus", cartesian: true, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine X by -1 step of the selected size, in machine coordinates. "
                   + "This machine's Cartesian axes match the Rhino frame, so machine X- moves toward Rhino -X." + Caveat,
                     JogDirectionHelp.Describe("jogxminus", cartesian: true, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine Y by +1 step of the selected size, in machine coordinates. "
                   + "This machine's Cartesian axes match the Rhino frame, so machine Y+ moves toward Rhino +Y." + Caveat,
                     JogDirectionHelp.Describe("jogyplus", cartesian: true, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine Y by -1 step of the selected size, in machine coordinates. "
                   + "This machine's Cartesian axes match the Rhino frame, so machine Y- moves toward Rhino -Y." + Caveat,
                     JogDirectionHelp.Describe("jogyminus", cartesian: true, frameKnown: true, refusalReason: null));
    }

    [Fact]
    public void Z_is_never_mirrored_and_is_up_in_both_frames()
    {
        Assert.Equal("Jogs machine Z by +1 step of the selected size, in machine coordinates. "
                   + "Z is not mirrored: machine Z+ is Rhino +Z, up in both frames." + Caveat,
                     JogDirectionHelp.Describe("jogzplus", cartesian: false, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine Z by -1 step of the selected size, in machine coordinates. "
                   + "Z is not mirrored: machine Z- is Rhino -Z, down in both frames." + Caveat,
                     JogDirectionHelp.Describe("jogzminus", cartesian: false, frameKnown: true, refusalReason: null));
        Assert.Equal("Jogs machine Z by +1 step of the selected size, in machine coordinates. "
                   + "This machine's Cartesian axes match the Rhino frame, so machine Z+ moves toward Rhino +Z (up)." + Caveat,
                     JogDirectionHelp.Describe("jogzplus", cartesian: true, frameKnown: true, refusalReason: null));
    }

    [Fact]
    public void An_unknown_mechanism_claims_no_rhino_mapping()
    {
        // Guessing a mapping is worse than admitting there is none: the two frames send
        // the nozzle in different directions, so a wrong promise is a crash.
        foreach (bool cartesian in new[] { false, true })
        {
            string? text = JogDirectionHelp.Describe("jogyplus", cartesian, frameKnown: false, refusalReason: null);
            Assert.Equal("Jogs machine Y by +1 step of the selected size, in machine coordinates. "
                       + "The mechanism is not established yet, so which way that is in Rhino is unknown.", text);
            Assert.DoesNotContain("Rhino +", text);
            Assert.DoesNotContain("Rhino -", text);
        }
    }

    [Fact]
    public void Nothing_ever_promises_a_camera_relative_direction()
    {
        string[] ids = { "jogxplus", "jogxminus", "jogyplus", "jogyminus", "jogzplus", "jogzminus" };
        foreach (string id in ids)
            foreach (bool cartesian in new[] { false, true })
                foreach (bool known in new[] { false, true })
                {
                    string text = JogDirectionHelp.Describe(id, cartesian, known, null)!;
                    Assert.DoesNotContain("left", text);
                    Assert.DoesNotContain("right", text);
                    Assert.DoesNotContain("toward you", text);
                    Assert.DoesNotContain("away from you", text);
                    Assert.Contains("machine coordinates", text);
                }
    }

    [Fact]
    public void A_refusal_reason_is_appended_so_the_help_never_hides_it()
    {
        string text = JogDirectionHelp.Describe("jogxplus", cartesian: false, frameKnown: true,
                                                refusalReason: "not connected (disconnected)")!;
        Assert.Contains("machine X+ moves toward Rhino -Y", text);
        Assert.EndsWith(" Jogging is unavailable: not connected (disconnected)", text);

        // even with no frame to describe, the reason still reaches the operator
        string unknown = JogDirectionHelp.Describe("jogzminus", cartesian: true, frameKnown: false,
                                                   refusalReason: "arm configuration unknown")!;
        Assert.EndsWith(" Jogging is unavailable: arm configuration unknown", unknown);

        // a blank reason is not a refusal
        Assert.DoesNotContain("unavailable",
            JogDirectionHelp.Describe("jogxplus", cartesian: true, frameKnown: true, refusalReason: "   ")!);
    }

    [Fact]
    public void Only_the_six_jog_ids_get_direction_help()
    {
        Assert.Null(JogDirectionHelp.Describe(null, false, true, null));
        Assert.Null(JogDirectionHelp.Describe("", false, true, null));
        Assert.Null(JogDirectionHelp.Describe("jog", false, true, null));
        Assert.Null(JogDirectionHelp.Describe("home", false, true, null));
        Assert.Null(JogDirectionHelp.Describe("jogaplus", false, true, null));
        Assert.Null(JogDirectionHelp.Describe("JOGXPLUS", false, true, null));   // ids are ordinal
    }
}
