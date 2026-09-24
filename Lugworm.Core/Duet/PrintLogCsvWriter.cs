using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lugworm.Core.Duet
{
    /// <summary>Append-only CSV sink for print-log records. File failures disable
    /// this sink but never escape into the Duet poll loop.</summary>
    public sealed class PrintLogCsvWriter : IDisposable
    {
        readonly object _sync = new object();
        StreamWriter? _writer;
        bool _disabled;
        string? _lastError;
        long _rowsWritten;
        int _rowsSinceFlush;
        bool _disposed;

        public PrintLogCsvWriter(string path)
        {
            Path = path ?? "";
            TryOpen();
        }

        public string Path { get; }

        public long RowsWritten
        {
            get { lock (_sync) return _rowsWritten; }
        }

        public bool Disabled
        {
            get { lock (_sync) return _disabled; }
        }

        public string? LastError
        {
            get { lock (_sync) return _lastError; }
        }

        public void Append(IEnumerable<PrintLogRecord> records)
        {
            if (records == null) return;
            lock (_sync)
            {
                if (_disabled || _disposed) return;
                try
                {
                    if (_writer == null) TryOpen();
                    if (_disabled || _writer == null) return;
                    foreach (var record in records)
                    {
                        if (record == null) continue;
                        _writer.WriteLine(PrintLogCsv.Row(record));
                        _rowsWritten++;
                        _rowsSinceFlush++;
                        if (record.Kind == PrintLogEventKind.LayerEnd
                            || record.Kind == PrintLogEventKind.LayerStart
                            || record.Kind == PrintLogEventKind.JobEnd
                            || _rowsSinceFlush >= 20)
                            FlushCore();
                    }
                }
                catch (Exception ex)
                {
                    Disable(ex);
                }
            }
        }

        public void Flush()
        {
            lock (_sync)
            {
                if (_disabled || _disposed || _writer == null) return;
                try { FlushCore(); }
                catch (Exception ex) { Disable(ex); }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_writer != null)
                    {
                        _writer.Flush();
                        _writer.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _disabled = true;
                }
                finally { _writer = null; }
            }
        }

        void TryOpen()
        {
            if (_disabled || _disposed) return;
            try
            {
                if (string.IsNullOrWhiteSpace(Path))
                    throw new ArgumentException("CSV path is empty.");
                bool exists = File.Exists(Path);
                var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, false)
                {
                    AutoFlush = false,
                };
                if (!exists || stream.Length == 0)
                {
                    _writer.WriteLine(PrintLogCsv.Header());
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Disable(ex);
            }
        }

        void FlushCore()
        {
            if (_writer == null) return;
            _writer.Flush();
            _rowsSinceFlush = 0;
        }

        void Disable(Exception ex)
        {
            _disabled = true;
            _lastError = ex.Message;
            // best effort: we are already disabling the sink over a prior failure;
            // a second failure while closing the writer (e.g. flushing to a dead
            // stream) must not replace the original diagnosis or escape the poll loop.
            try { _writer?.Dispose(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            _writer = null;
        }
    }
}
