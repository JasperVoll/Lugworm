using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

// The /sys guard (Phase 4.4). IsProtected must catch every path that resolves
// under 0:/sys or 0:/firmware - the user asked specifically whether the slicer
// could clobber config.g, and delete/move are the only ways it could.
public class DuetFileProtectionTests
{
    [Theory]
    [InlineData("0:/sys")]
    [InlineData("0:/sys/config.g")]
    [InlineData("0:/sys/homeall.g")]
    [InlineData("0:/firmware")]
    [InlineData("0:/firmware/Duet2CombinedFirmware.bin")]
    public void ProtectedPaths_AreProtected(string path)
        => Assert.True(DuetFileEntry.IsProtected(path), path);

    [Theory]
    [InlineData("0:/SYS/config.g")]                 // case-insensitive
    [InlineData("0:/Sys/Config.g")]
    [InlineData("sys/config.g")]                     // missing drive defaults to 0:
    [InlineData("/sys/config.g")]
    [InlineData("0:\\sys\\config.g")]               // backslashes
    [InlineData("0:/gcodes/../sys/config.g")]        // .. traversal into sys
    [InlineData("0:/gcodes/../../sys/config.g")]     // over-popped .. cannot escape the drive
    [InlineData("0:/sys/./config.g")]                // . segment
    public void ObfuscatedProtectedPaths_AreStillProtected(string path)
        => Assert.True(DuetFileEntry.IsProtected(path), path);

    [Theory]
    [InlineData("0:/gcodes/print.gcode")]
    [InlineData("0:/gcodes/sub/x.g")]
    [InlineData("0:/sysfoo/x.g")]                    // sysfoo is NOT sys
    [InlineData("0:/system/x.g")]
    [InlineData("0:/macros/home.g")]
    [InlineData("0:/sys/../gcodes/x.g")]             // resolves out of sys
    public void OrdinaryPaths_AreNotProtected(string path)
        => Assert.False(DuetFileEntry.IsProtected(path), path);
}
