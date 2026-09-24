using System;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Export copy of a job record (OP-06). Finalisation deletes the staging buffer of
/// a Saved job, so the copy has to come from the destination; and a job with nothing left
/// to copy must fail before the operator's chosen target is touched.</summary>
public class LogRetentionExportTests
{
    const string Root = "C:/staging";
    const string Dest = "//nas/prints";
    int _suffix;

    LogRetentionCoordinator New(FakeFileSystem fs)
        => new LogRetentionCoordinator(fs, Root, () => new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc),
                                       () => (++_suffix).ToString("x6", CultureInfo.InvariantCulture));

    [Fact]
    public void A_saved_job_exports_from_the_destination_copy()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        Assert.True(c.SetKeepAll(true));
        string id = c.BeginJob("vase.gcode");
        c.AppendRecord("r1");
        c.AppendRecord("r2");
        c.CompleteJob();
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, c.Snapshot().Jobs.Single().State);
        Assert.DoesNotContain(Root + "/" + id, fs.Dirs);   // the staging buffer is gone

        c.ExportCopy(id, "C:/out/copy.csv");

        Assert.Equal("r1\nr2\n", fs.Text("C:/out/copy.csv"));
    }

    [Fact]
    public void A_job_with_no_record_left_fails_without_touching_the_target()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        Assert.True(c.SetKeepAll(true));
        string id = c.BeginJob("vase.gcode");
        c.AppendRecord("r1");
        c.CompleteJob();
        Assert.True(c.SyncOnce());
        fs.DeleteDirectory(Dest + "/" + id);   // the kept copy was removed by hand
        fs.WriteTextAtomic("C:/out/keep.csv", "operator data");

        Assert.Throws<InvalidOperationException>(() => c.ExportCopy(id, "C:/out/keep.csv"));

        Assert.Equal("operator data", fs.Text("C:/out/keep.csv"));
    }
}
