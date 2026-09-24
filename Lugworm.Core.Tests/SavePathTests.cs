using System;
using System.IO;
using Lugworm.Core.Gcode;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Gcode/SavePath.cs: Resolve() turns whatever a user typed into a save
/// dialog (a file, a folder, a folder with a trailing slash, a bare name with no
/// extension) into a concrete file path with its parent directory guaranteed to
/// exist, and Explain() turns a failed write into a message that says what to do
/// about it. No other test file references SavePath by name. Every case here
/// creates its fixtures under a unique %TEMP% subfolder and removes them in a
/// finally block; none of it touches the process's current directory, so it is
/// safe under parallel test execution.</summary>
public class SavePathTests
{
    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "iamlab_savepath_" + Guid.NewGuid().ToString("N"));
        return dir;
    }

    [Fact]
    public void Resolve_TrailingSeparator_AppendsDefaultNameAndCreatesTheFolder()
    {
        string dir = NewTempDir();
        try
        {
            string result = SavePath.Resolve(dir + "\\", "out.gcode");
            Assert.Equal(Path.Combine(dir, "out.gcode"), result);
            Assert.True(Directory.Exists(dir));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Resolve_TrailingForwardSlash_AlsoTreatedAsAFolder()
    {
        string dir = NewTempDir();
        try
        {
            string result = SavePath.Resolve(dir + "/", "out.gcode");
            Assert.Equal(Path.Combine(dir, "out.gcode"), result);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Resolve_ExistingDirectoryWithoutTrailingSeparator_AppendsDefaultName()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string result = SavePath.Resolve(dir, "out.gcode");
            Assert.Equal(Path.Combine(dir, "out.gcode"), result);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Resolve_ExtensionlessPathThatDoesNotExist_IsTreatedAsAFolder()
    {
        string dir = NewTempDir();
        string extensionless = Path.Combine(dir, "myproject");
        try
        {
            string result = SavePath.Resolve(extensionless, "out.gcode");
            Assert.Equal(Path.Combine(extensionless, "out.gcode"), result);
            Assert.True(Directory.Exists(extensionless));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Resolve_ExistingExtensionlessFile_CollisionRuleKeepsItAsTheFile()
    {
        // The classic trap in reverse: an existing file with no extension must
        // NOT be treated as a folder, or the writer would try to create a
        // directory on top of a real file.
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        string extensionlessFile = Path.Combine(dir, "README");
        File.WriteAllText(extensionlessFile, "hi");
        try
        {
            string result = SavePath.Resolve(extensionlessFile, "out.gcode");
            Assert.Equal(Path.GetFullPath(extensionlessFile), Path.GetFullPath(result));
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Resolve_PathWithExtensionThatDoesNotExistYet_IsKeptAsAFile()
    {
        string dir = NewTempDir();
        string file = Path.Combine(dir, "out.gcode");
        try
        {
            string result = SavePath.Resolve(file, "default.gcode");
            Assert.Equal(file, result);
            Assert.True(Directory.Exists(dir));  // parent created
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Resolve_QuotesAndSurroundingWhitespace_AreStripped()
    {
        string dir = NewTempDir();
        string file = Path.Combine(dir, "out.gcode");
        try
        {
            string result = SavePath.Resolve("  \"" + file + "\"  ", "default.gcode");
            Assert.Equal(file, result);
        }
        finally { TryDeleteDir(dir); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Resolve_EmptyPath_ThrowsArgumentException(string bad)
    {
        Assert.Throws<ArgumentException>(() => SavePath.Resolve(bad, "out.gcode"));
    }

    [Fact]
    public void Resolve_EmbeddedNulCharacter_ThrowsRatherThanWritingGarbage()
    {
        // Windows/.NET rejects a NUL inside a path outright; SavePath does not
        // catch it, so the documented failure is whatever Path.GetFullPath
        // itself throws for an illegal path character.
        Assert.Throws<ArgumentException>(() => SavePath.Resolve("bad\0name.gcode", "default.gcode"));
    }

    [Fact]
    public void Resolve_RelativeFilePath_KeepsItRelativeButStillCreatesTheParentDirectory()
    {
        // Resolve only calls Path.GetFullPath internally, to find the parent
        // directory to create - the returned string itself is not rooted: a
        // relative file path with an extension goes through neither special
        // branch (not a trailing separator, not an existing directory, not
        // extensionless), so it comes back exactly as given.
        string relativeDir = "iamlab_savepath_relcase_" + Guid.NewGuid().ToString("N");
        string relativeFile = Path.Combine(relativeDir, "out.gcode");
        string createdDir = Path.Combine(Environment.CurrentDirectory, relativeDir);
        try
        {
            string result = SavePath.Resolve(relativeFile, "default.gcode");
            Assert.False(Path.IsPathRooted(result));
            Assert.Equal(relativeFile, result);
            Assert.True(Directory.Exists(createdDir));
        }
        finally { TryDeleteDir(createdDir); }
    }

    // ---- Explain() -----------------------------------------------------------

    [Fact]
    public void Explain_UnauthorizedAccessOnAFolder_SaysAFileNameIsAppendedAutomatically()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string msg = SavePath.Explain(new UnauthorizedAccessException(), dir);
            Assert.Contains("is a folder, not a file", msg);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Explain_UnauthorizedAccessOnAFile_MentionsLockedOrProtectedCauses()
    {
        string msg = SavePath.Explain(new UnauthorizedAccessException(), @"C:\some\out.gcode");
        Assert.Contains("write access", msg);
        Assert.Contains("Ransomware protection", msg);
    }

    [Fact]
    public void Explain_DirectoryNotFound_NamesTheMissingFolder()
    {
        string msg = SavePath.Explain(new DirectoryNotFoundException(), @"C:\missing\out.gcode");
        Assert.Contains("does not exist", msg);
        Assert.Contains(@"C:\missing\out.gcode", msg);
    }

    [Fact]
    public void Explain_IOException_SuggestsTheFileMayBeOpenElsewhere()
    {
        string msg = SavePath.Explain(new IOException("The process cannot access the file."), @"C:\out.xlsx");
        Assert.Contains("The process cannot access the file.", msg);
        Assert.Contains("file locked/open in Excel?", msg);
    }

    [Fact]
    public void Explain_OtherException_FallsBackToItsMessage()
    {
        string msg = SavePath.Explain(new InvalidOperationException("boom"), @"C:\out.gcode");
        Assert.Contains("boom", msg);
        Assert.Contains(@"C:\out.gcode", msg);
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
