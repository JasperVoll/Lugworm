using System;
using System.Collections.Generic;
using System.Threading;

namespace Lugworm.Core.Duet
{
    /// <summary>Serialized background file sink for print-log records.
    ///
    /// The Duet poll loop must never block on disk. A network folder that has gone
    /// away can stall a synchronous write indefinitely - there is no timeout for file
    /// I/O - and the poll loop is what keeps the session alive and keeps
    /// <see cref="StateGate"/> snapshots fresh, so a stalled write would take machine
    /// control down with it. Producers therefore only enqueue; one dedicated thread
    /// performs every open, append, flush and close, strictly in order.
    ///
    /// The queue is bounded. When a slow sink cannot keep up, rows are dropped and
    /// counted rather than allowed to grow without limit or to push back on the
    /// producer: losing log rows is acceptable, stalling the machine session is not.</summary>
    public sealed class PrintLogSink : IDisposable
    {
        public const int DefaultCapacity = 20000;

        enum CommandKind { Open, Append, Close }

        readonly struct Command
        {
            public readonly CommandKind Kind;
            public readonly string? Path;
            public readonly PrintLogRecord? Record;

            Command(CommandKind kind, string? path, PrintLogRecord? record)
            { Kind = kind; Path = path; Record = record; }

            public static Command Open(string path) => new Command(CommandKind.Open, path, null);
            public static Command Append(PrintLogRecord record) => new Command(CommandKind.Append, null, record);
            public static Command Close() => new Command(CommandKind.Close, null, null);
        }

        readonly object _sync = new object();
        readonly Queue<Command> _queue = new Queue<Command>();
        readonly int _capacity;
        readonly Thread _thread;
        PrintLogCsvWriter? _writer;
        string? _path;
        string? _lastError;
        long _rowsWritten;
        long _rowsDroppedThisFile;
        bool _stopping;
        bool _draining;

        public PrintLogSink(int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(16, capacity);
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Lugworm print log",
            };
            _thread.Start();
        }

        /// <summary>Path of the most recently requested file. Set when the open is
        /// enqueued, so the UI can report it without waiting for the pump.</summary>
        public string? Path { get { lock (_sync) return _path; } }

        public string? LastError { get { lock (_sync) return _lastError; } }

        public long RowsWritten { get { lock (_sync) return _rowsWritten; } }

        /// <summary>Rows discarded since this file was opened. The per-file figure is
        /// what tells a reader whether THIS run's CSV has gaps; the lifetime total would
        /// keep every later clean run falsely marked incomplete.</summary>
        public long RowsDropped { get { lock (_sync) return _rowsDroppedThisFile; } }

        /// <summary>True once every queued command has been executed.</summary>
        public bool Idle { get { lock (_sync) return _queue.Count == 0 && !_draining; } }

        public void Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (_sync)
            {
                _path = path;
                // Reset here rather than when the pump executes the open: drops are
                // counted on the producer side under this same lock, so resetting on the
                // pump thread would race with drops already attributed to the new file.
                _rowsDroppedThisFile = 0;
                EnqueueLocked(Command.Open(path), force: true);
            }
        }

        public void Append(PrintLogRecord record)
        {
            if (record == null) return;
            lock (_sync) EnqueueLocked(Command.Append(record), force: false);
        }

        public void Append(IEnumerable<PrintLogRecord> records)
        {
            if (records == null) return;
            lock (_sync)
                foreach (var record in records)
                    if (record != null) EnqueueLocked(Command.Append(record), force: false);
        }

        public void Close()
        {
            lock (_sync) EnqueueLocked(Command.Close(), force: true);
        }

        /// <summary>Waits for the queue to drain. Used by teardown and tests; never
        /// called from the poll loop.</summary>
        public bool WaitForIdle(int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                if (Idle) return true;
                Thread.Sleep(5);
            }
            return Idle;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_stopping) return;
                // Close is enqueued rather than executed here: the pump owns the writer,
                // and disposing it from another thread mid-write would race.
                EnqueueLocked(Command.Close(), force: true);
                _stopping = true;
                Monitor.PulseAll(_sync);
            }
            // Bounded join - teardown must not hang on a dead network share either.
            // The thread is a background thread, so abandoning it cannot block exit.
            // best effort: a thread that never started, or an interrupted wait, still
            // leaves Dispose() free to return - there is nothing more to wait for.
            try { _thread.Join(2000); }
            catch (ThreadStateException) { }
            catch (ThreadInterruptedException) { }
        }

        void EnqueueLocked(Command command, bool force)
        {
            if (_stopping) return;
            if (!force && _queue.Count >= _capacity)
            {
                _rowsDroppedThisFile++;
                _lastError = "print-log sink queue full - " + _rowsDroppedThisFile
                           + " row(s) dropped from this file (slow or unreachable folder)";
                return;
            }
            _queue.Enqueue(command);
            Monitor.PulseAll(_sync);
        }

        void Pump()
        {
            while (true)
            {
                Command command;
                lock (_sync)
                {
                    while (_queue.Count == 0 && !_stopping) Monitor.Wait(_sync);
                    if (_queue.Count == 0 && _stopping) break;
                    command = _queue.Dequeue();
                    _draining = true;
                }

                try { Execute(command); }
                catch (Exception ex)
                {
                    // PrintLogCsvWriter already swallows its own I/O failures; this is
                    // the backstop so the pump thread can never die and silently stop
                    // logging without saying why.
                    lock (_sync) _lastError = ex.Message;
                }
                finally
                {
                    lock (_sync)
                    {
                        _draining = false;
                        Monitor.PulseAll(_sync);
                    }
                }
            }

            CloseWriter();
        }

        void Execute(Command command)
        {
            switch (command.Kind)
            {
                case CommandKind.Open:
                    CloseWriter();
                    var writer = new PrintLogCsvWriter(command.Path!);
                    _writer = writer;
                    if (writer.Disabled) lock (_sync) _lastError = writer.LastError;
                    break;

                case CommandKind.Append:
                    if (_writer == null) return;
                    long before = _writer.RowsWritten;
                    _writer.Append(new[] { command.Record! });
                    lock (_sync)
                    {
                        _rowsWritten += _writer.RowsWritten - before;
                        if (_writer.LastError != null) _lastError = _writer.LastError;
                    }
                    break;

                case CommandKind.Close:
                    CloseWriter();
                    break;
            }
        }

        void CloseWriter()
        {
            var writer = _writer;
            if (writer == null) return;
            _writer = null;
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception ex) { lock (_sync) _lastError = ex.Message; }
            if (writer.LastError != null) lock (_sync) _lastError = writer.LastError;
        }
    }
}
