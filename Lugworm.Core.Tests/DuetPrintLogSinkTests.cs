using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The sink exists so the Duet poll loop never blocks on disk: a stalled
/// write has no timeout, and the poll loop is what keeps the session alive and keeps
/// gate snapshots fresh.</summary>
public class DuetPrintLogSinkTests
{
    static PrintLogRecord Row(long seq, PrintLogEventKind kind = PrintLogEventKind.Interval)
        => new PrintLogRecord { Seq = seq, Kind = kind, Status = "Processing" };

    static string TempPath()
        => Path.Combine(Path.GetTempPath(), "printlogsink_" + Guid.NewGuid().ToString("N") + ".csv");

    [Fact]
    public void WritesQueuedRecordsToTheFile()
    {
        string path = TempPath();
        try
        {
            using (var sink = new PrintLogSink())
            {
                sink.Open(path);
                sink.Append(Row(1));
                sink.Append(Row(2, PrintLogEventKind.LayerEnd));
                sink.Close();
                Assert.True(sink.WaitForIdle(5000), "sink did not drain");
                Assert.Equal(2, sink.RowsWritten);
                Assert.Equal(path, sink.Path);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(PrintLogCsv.Header(), lines[0]);
            Assert.Equal(3, lines.Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void OneFilePerRun_SecondOpenStartsANewFile()
    {
        string first = TempPath();
        string second = TempPath();
        try
        {
            using (var sink = new PrintLogSink())
            {
                sink.Open(first);
                sink.Append(Row(1));
                sink.Close();
                sink.Open(second);
                sink.Append(Row(2));
                sink.Close();
                Assert.True(sink.WaitForIdle(5000));
            }

            Assert.Equal(2, File.ReadAllLines(first).Length);
            Assert.Equal(2, File.ReadAllLines(second).Length);
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [Fact]
    public void ProducerNeverBlocks_EvenWhenTheTargetIsUnusable()
    {
        // a directory can never be opened as a file; the pump absorbs the failure
        using var sink = new PrintLogSink();
        sink.Open(Path.GetTempPath());

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++) sink.Append(Row(i));
        sink.Close();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            "enqueueing must not wait on the file; took " + stopwatch.ElapsedMilliseconds + " ms");
        Assert.True(sink.WaitForIdle(5000));
        Assert.False(string.IsNullOrEmpty(sink.LastError));
        Assert.Equal(0, sink.RowsWritten);
    }

    [Fact]
    public void AppendsBeforeAnOpenAreDroppedRatherThanThrowing()
    {
        using var sink = new PrintLogSink();

        var exception = Record.Exception(() => sink.Append(Row(1)));

        Assert.Null(exception);
        Assert.True(sink.WaitForIdle(5000));
        Assert.Equal(0, sink.RowsWritten);
    }

    [Fact]
    public void DisposeFlushesAndClosesWithoutAnExplicitClose()
    {
        string path = TempPath();
        try
        {
            using (var sink = new PrintLogSink())
            {
                sink.Open(path);
                sink.Append(Row(1));
            }   // Dispose must drain and release the handle

            // the file is readable and writable again, i.e. the handle is gone
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(stream.Length > 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var sink = new PrintLogSink();
        sink.Dispose();

        Assert.Null(Record.Exception(() => sink.Dispose()));
    }

    [Fact]
    public void RecordsKeepTheirOrder()
    {
        string path = TempPath();
        try
        {
            using (var sink = new PrintLogSink())
            {
                sink.Open(path);
                for (int i = 1; i <= 50; i++) sink.Append(Row(i));
                sink.Close();
                Assert.True(sink.WaitForIdle(5000));
            }

            var seqs = File.ReadAllLines(path).Skip(1)
                .Select(l => long.Parse(l.Split(',')[0])).ToArray();
            Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i).ToArray(), seqs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
