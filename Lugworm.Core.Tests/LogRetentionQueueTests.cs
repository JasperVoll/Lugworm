using System;
using System.Linq;
using System.Threading;
using Lugworm.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class LogRetentionQueueTests
{
    [Fact]
    public void DelayedWorkerAttributesDropsAtProducerRunBoundaries()
    {
        var fs = new FakeFileSystem();
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var finishRelease = new ManualResetEventSlim();
        using var service = new LogRetentionService(fs, "queue-test");
        try
        {
            // A public worker callback models a stalled destination without timing races.
            service.ReadArchivedGcode("missing", _ => { blocked.Set(); release.Wait(TimeSpan.FromSeconds(15)); });
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)));
            service.BeginJob("first.gcode");
            for (int i = 0; i < 20100; i++) service.AppendRecord("row");
            long firstDrops = service.RowsDropped;
            Assert.True(firstDrops > 0);
            for (int i = 0; i < 4010; i++) service.AppendDiagnostic("diagnostic");
            service.CompleteJob();
            service.BeginJob("second.gcode");
            for (int i = 0; i < 7; i++) service.AppendRecord("row");
            long secondDrops = service.RowsDropped - firstDrops;
            Assert.Equal(7, secondDrops);
            for (int i = 0; i < 3; i++) service.AppendDiagnostic("diagnostic");
            service.CompleteJob();
            service.ReadArchivedGcode("missing", _ => { finished.Set(); finishRelease.Wait(TimeSpan.FromSeconds(15)); });
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
            var manifests = fs.Files.Keys.Where(p => p.EndsWith("manifest.json", StringComparison.Ordinal))
                .Select(p => Lugworm.Core.Duet.JobLogManifest.Parse(fs.ReadText(p))).ToArray();
            Assert.Equal(firstDrops, Assert.Single(manifests, m => m.JobName == "first.gcode").RowsDropped);
            Assert.Equal(secondDrops, Assert.Single(manifests, m => m.JobName == "second.gcode").RowsDropped);
            var first = Assert.Single(manifests, m => m.JobName == "first.gcode");
            var second = Assert.Single(manifests, m => m.JobName == "second.gcode");
            Assert.Equal(4010, first.DiagnosticsDropped);
            Assert.Equal(10, first.SessionDiagnosticsDropped);
            Assert.Equal(3, second.DiagnosticsDropped);
            Assert.Equal(3, second.SessionDiagnosticsDropped);
        }
        finally { release.Set(); finishRelease.Set(); }
    }
}
