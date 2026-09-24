using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Lugworm.Core.Duet;
using Lugworm.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>The Duet Console window: a persistent command shell drawn with
    /// Skia (navigation rail, status header, pages) plus a native safety footer. Keeps
    /// the form contract - <see cref="Post"/>, an action-id callback, a hidden callback
    /// and <see cref="SelectedPoint"/> - so the component's wiring is unchanged.</summary>
    /// <remarks>
    /// <para>E-STOP and Reset are real WinForms buttons docked OUTSIDE the drawing surface
    /// (decision D6). Skia is not a resilience boundary: a missing or mismatched native
    /// kills both the CPU and the GL surface at once, so the two controls that must work
    /// when everything else is broken are never drawn by it.</para>
    /// <para>The terminal input is a native TextBox positioned over the surface. Text
    /// entry, IME, selection and clipboard are not worth reimplementing on a canvas.</para>
    /// <para>The form is a pure view of <see cref="PrintConsoleState"/> plus a
    /// <see cref="ButtonSet"/>; it holds no machine facts and decides no gates. It owns
    /// WinForms concerns only: dialogs, cursor and capture, the animation timer that runs
    /// solely while a transition or pending indicator is active, and window settings.</para>
    /// </remarks>
    public sealed partial class DuetConsoleForm : Form
    {
        readonly Action<string> _onAction;
        readonly Action _onHidden;
        readonly ConsoleView _view = new ConsoleView();
        /// <summary>Null until the constructor builds it. WinForms raises layout - and thus
        /// OnResize - while the form is still being constructed (setting ClientSize is
        /// enough), so every use below has to tolerate its absence rather than assume the
        /// constructor finished.</summary>
        SkiaSurfaceHost? _surface;
        readonly TextBox _terminal = new TextBox();
        readonly Panel _footer = new Panel();
        readonly Label _footerText = new Label();
        readonly Label _footerHealth = new Label();
        readonly Button _reset = new Button();
        readonly Button _estop = new Button();
        readonly Label _degraded = new Label();
        readonly Timer _animation = new Timer { Interval = 16 };
        readonly Stopwatch _clock = Stopwatch.StartNew();

        readonly object _gate = new object();
        PrintConsoleState? _latestState;
        ButtonSet? _latestButtons;
        IReadOnlyList<string>? _latestLabels;
        bool _renderQueued;
        /// <summary>The mechanism the component resolved, so a page can say which Rhino
        /// direction a machine axis moves along. Defaults to "not known", which makes the
        /// pages claim no mapping at all rather than assume the Cartesian identity.</summary>
        bool _cartesian, _frameKnown;
        /// <summary>The region the tooltip is currently showing for, so hover text is
        /// pushed only when it actually changes. Tracked apart from
        /// <see cref="ConsoleView.HotId"/> because a DISABLED control has no hot id and is
        /// exactly the one whose tooltip carries the refusal reason.</summary>
        string? _tipId;

        volatile int _selectedPoint = -1;
        ConsoleTheme _theme;

        public DuetConsoleForm(Action<string> onAction) : this(onAction, () => { }) { }

        /// <param name="preferOpenGl">The persisted opt-in (D6). CPU raster is the default;
        /// the surface falls back to it by itself if this host cannot paint GL.</param>
        /// <param name="reducedMotion">The persisted reduced-motion preference.</param>
        public DuetConsoleForm(Action<string> onAction, Action onHidden, bool preferOpenGl = false, bool reducedMotion = false)
        {
            _onAction = onAction ?? throw new ArgumentNullException(nameof(onAction));
            _onHidden = onHidden ?? throw new ArgumentNullException(nameof(onHidden));

            Text = "Lugworm Duet Console";
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(0x11, 0x14, 0x17);
            ForeColor = Color.FromArgb(0xF2, 0xF5, 0xF7);
            // Scaled to the monitor, then the saved bounds: a restored rectangle is already
            // in device pixels and wins, and its clamp uses the scaled minimum.
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = FitTo(ScaleSize(DesignMinimumSize, DeviceDpi), area);
            ClientSize = ScaleSize(DesignClientSize, DeviceDpi);
            Size = FitTo(Size, area);
            ShowInTaskbar = true;
            DoubleBuffered = true;
            RestoreWindowBounds();

            _theme = NewTheme();
            _view.SetTheme(_theme);
            _view.Interaction.ReducedMotion = reducedMotion;

            // ---- native footer: E-STOP and Reset never depend on the paint path -------
            _footer.Dock = DockStyle.Bottom;
            _footer.BackColor = Color.FromArgb(0x17, 0x1B, 0x20);

            _estop.Text = "E-STOP";
            _estop.AutoSize = false;
            _estop.Dock = DockStyle.Right;
            _estop.UseVisualStyleBackColor = false;
            _estop.FlatStyle = FlatStyle.Flat;
            _estop.FlatAppearance.BorderSize = 0;
            _estop.BackColor = Color.FromArgb(0xE5, 0x48, 0x4D);
            _estop.ForeColor = Color.White;
            _estop.Font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
            _estop.Click += (s, e) => _onAction("estop");

            _reset.Text = "Reset (M999)";
            _reset.AutoSize = false;
            _reset.Dock = DockStyle.Right;
            _reset.FlatStyle = FlatStyle.Flat;
            _reset.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x58, 0x66);
            _reset.BackColor = Color.FromArgb(0x25, 0x2C, 0x34);
            _reset.ForeColor = Color.FromArgb(0xF2, 0xF5, 0xF7);
            _reset.Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            _reset.Click += (s, e) => _onAction("reset");

            _footerHealth.Dock = DockStyle.Right;
            _footerHealth.AutoSize = false;
            _footerHealth.TextAlign = ContentAlignment.MiddleRight;
            _footerHealth.AutoEllipsis = true;
            _footerHealth.ForeColor = Color.FromArgb(0xF0, 0xB4, 0x4D);
            _footerHealth.Visible = false;

            _footerText.Dock = DockStyle.Fill;
            _footerText.TextAlign = ContentAlignment.MiddleLeft;
            _footerText.AutoEllipsis = true;
            _footerText.ForeColor = Color.FromArgb(0xAA, 0xB4, 0xBF);

            _footer.Controls.Add(_footerText);
            _footer.Controls.Add(_footerHealth);
            _footer.Controls.Add(_reset);
            _footer.Controls.Add(_estop);
            Controls.Add(_footer);

            // ---- degraded banner, shown only when Skia cannot paint at all ------------
            _degraded.Dock = DockStyle.Top;
            _degraded.TextAlign = ContentAlignment.MiddleLeft;
            _degraded.BackColor = Color.FromArgb(0xF0, 0xB4, 0x4D);
            _degraded.ForeColor = Color.FromArgb(0x1A, 0x14, 0x06);
            _degraded.Text = "The console could not start its renderer. "
                           + "E-STOP and Reset below still work; use the canvas components for everything else.";
            _degraded.Visible = false;
            Controls.Add(_degraded);
            ApplyNativeMetrics(DeviceDpi);

            // ---- terminal input, positioned over the surface by the view --------------
            _terminal.Visible = false;
            _terminal.Font = new Font("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
            _terminal.BackColor = Color.FromArgb(0x1E, 0x24, 0x2B);
            _terminal.ForeColor = Color.FromArgb(0xF2, 0xF5, 0xF7);
            _terminal.BorderStyle = BorderStyle.FixedSingle;
            _terminal.TextChanged += (s, e) => _onAction("terminalchanged");
            _terminal.KeyDown += OnTerminalKeyDown;
            Controls.Add(_terminal);

            // ---- the drawing surface --------------------------------------------------
            var surface = new SkiaSurfaceHost(PaintSurface, preferOpenGl);
            _surface = surface;
            surface.KindChanged += OnSurfaceKindChanged;
            surface.Control.Dock = DockStyle.Fill;
            Controls.Add(surface.Control);
            surface.Control.BringToFront();
            _terminal.BringToFront();

            // Subscribe to the HOST's forwarded events, not to _surface.Control: that is
            // the container, and the drawing control fills it, so it - not the container -
            // receives the mouse. The host also re-wires them across a GL->CPU swap.
            surface.SurfaceMouseClick += OnSurfaceClick;
            surface.SurfaceMouseMove += OnSurfaceMove;
            surface.SurfaceMouseDown += OnSurfaceDown;
            surface.SurfaceMouseUp += OnSurfaceUp;
            surface.SurfaceMouseLeave += (s, e) =>
            {
                _view.HotId = null;
                ClearTip();
                // Leaving the surface with the button still down must end the hold, or a
                // drag off the pad would leave it jogging.
                ReleaseHeldJog();
                _view.Interaction.Release();
                surface.SetCursor(Cursors.Default);
                surface.Invalidate();
            };
            // Capture loss (a dialog, Alt-Tab, the surface being swapped) eats the mouse-up.
            surface.SurfaceMouseCaptureChanged += (s, e) =>
            {
                if (!surface.SurfaceHasCapture) ReleaseHeldJog();
            };

            _animation.Tick += (s, e) => _surface?.Invalidate();

            // The host no longer calls back from its own constructor, so read the initial
            // kind explicitly now that the field is assigned.
            OnSurfaceKindChanged(surface.Kind);
        }

        // ---- public API (unchanged from the 2.6.0 form) --------------------------------

        /// <summary>The calibration point the operator picked, or -1. Read by the component
        /// from any thread, so it stays a volatile field rather than a control query.</summary>
        public int SelectedPoint => _selectedPoint;

        /// <summary>Drops the selection and returns the list to its first page. The
        /// component calls this whenever the point list is replaced: an index kept across
        /// a replacement would silently name a different target.</summary>
        public void ClearPointSelection()
        {
            _selectedPoint = -1;
            if (IsDisposed || Disposing) return;
            void Apply()
            {
                _view.PointPage = 0;
                _surface?.Invalidate();
            }
            if (IsHandleCreated && InvokeRequired)
            {
                // best effort: IsHandleCreated is a check, not a lock - the window can be
                // destroyed between it and the marshal. There is then nothing left to
                // repaint, and the selection field above is already cleared.
                try { BeginInvoke((Action)Apply); } catch (Exception) { }
            }
            else Apply();
        }

        /// <summary>The persisted reduced-motion preference changed.</summary>
        public void SetReducedMotion(bool reduced)
        {
            _view.Interaction.ReducedMotion = reduced;
            _surface?.Invalidate();
        }

        /// <summary>Accepts a state from any thread and coalesces to one queued render.</summary>
        /// <param name="cartesian">The machine's Cartesian axes map to the Rhino frame as
        /// identity. Read only when <paramref name="frameKnown"/> is true.</param>
        /// <param name="frameKnown">The robot profile resolved, so the machine-to-Rhino
        /// mapping is a fact. Optional and false by default: a caller that has not resolved
        /// a profile must leave the pages claiming nothing rather than assume a frame.</param>
        public void Post(PrintConsoleState state, ButtonSet buttons, IReadOnlyList<string> pointLabels,
                         bool cartesian = false, bool frameKnown = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (buttons == null) throw new ArgumentNullException(nameof(buttons));
            if (IsDisposed || Disposing) return;
            lock (_gate)
            {
                _latestState = state;
                _latestButtons = buttons;
                _latestLabels = pointLabels ?? Array.Empty<string>();
                _cartesian = cartesian;
                _frameKnown = frameKnown;
                if (_renderQueued) return;
                _renderQueued = true;
            }
            try
            {
                if (IsHandleCreated) BeginInvoke((Action)DrainAndRender);
                else lock (_gate) _renderQueued = false;
            }
            // best effort: the window was destroyed between IsHandleCreated and the
            // marshal. Clearing the queued flag is the point - leaving it set would make
            // every later Post believe a render is already on its way.
            catch (Exception) { lock (_gate) _renderQueued = false; }
        }

        /// <summary>Applies the newest posted state immediately on the UI thread.</summary>
        public void Render(PrintConsoleState state, ButtonSet buttons, IReadOnlyList<string> pointLabels)
        {
            lock (_gate)
            {
                _latestState = state;
                _latestButtons = buttons;
                _latestLabels = pointLabels ?? Array.Empty<string>();
            }
            DrainAndRender();
        }

        void DrainAndRender()
        {
            lock (_gate) _renderQueued = false;
            if (IsDisposed || Disposing) return;
            UpdateFooter();
            LayoutTerminal();
            _surface?.Invalidate();
        }

        // ---- painting -----------------------------------------------------------------

        void PaintSurface(SKCanvas canvas, int width, int height)
        {
            PrintConsoleState? state;
            ButtonSet? buttons;
            IReadOnlyList<string> labels;
            bool cartesian, frameKnown;
            lock (_gate)
            {
                state = _latestState;
                buttons = _latestButtons;
                labels = _latestLabels ?? Array.Empty<string>();
                cartesian = _cartesian;
                frameKnown = _frameKnown;
            }
            if (state == null || buttons == null)
            {
                canvas.Clear(_theme.Background);
                return;
            }
            _view.SelectedPoint = _selectedPoint;
            _view.Interaction.Now = _clock.Elapsed.TotalMilliseconds;
            _view.Paint(canvas, width, height, state, buttons, labels, _surface?.KindLabel ?? "", cartesian, frameKnown);
            // The idle screen consumes no render loop: the timer runs only while a hover,
            // press, navigation slide or pending indicator is in flight.
            bool live = _view.Interaction.Animating || state.PendingAction != null;
            if (_animation.Enabled != live) _animation.Enabled = live;
            LayoutTerminal();
        }

        // ---- input --------------------------------------------------------------------

        /// <summary>The jog pad is press-and-hold, so it is driven by MouseDown/MouseUp
        /// rather than Click. The component owns the repeat policy; the form only reports
        /// the two edges.</summary>
        static bool IsJogId(string id) => id.StartsWith("jog", StringComparison.Ordinal);

        string? _heldJog;

        void OnSurfaceDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            string? id = _view.HitTest(e.X, e.Y);
            if (id == null) return;
            _view.Interaction.Press(id);   // immediate press feedback; never delays dispatch
            _surface?.Invalidate();
            if (!IsJogId(id)) return;
            _heldJog = id;
            _onAction("jogdown:" + id);
        }

        void OnSurfaceUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _view.Interaction.Release();
            _surface?.Invalidate();
            ReleaseHeldJog();
        }

        /// <summary>The single release path for a held jog. Idempotent, and wired to every
        /// way the mouse-up can be lost: mouse-up itself, leaving the surface, losing
        /// capture, switching page, deactivation, hiding, a renderer swap and disposal. The
        /// step already sent stays in motion; nothing further goes out until a fresh press.</summary>
        void ReleaseHeldJog()
        {
            if (_heldJog == null) return;
            _heldJog = null;
            _onAction("jogup");
        }

        void OnSurfaceClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            string? id = _view.HitTest(e.X, e.Y);
            if (id == null) return;
            if (IsJogId(id)) return;   // already handled as a press-and-hold
            if (id.StartsWith("tab:", StringComparison.Ordinal))
            {
                if (int.TryParse(id.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tab))
                {
                    // The jog pad leaves the screen with its page; the hold must not outlive it.
                    ReleaseHeldJog();
                    _view.ActiveTab = (ConsoleView.Tab)tab;
                    LayoutTerminal();
                    _surface?.Invalidate();
                }
                return;
            }
            if (id.StartsWith("point:", StringComparison.Ordinal))
            {
                if (int.TryParse(id.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int point))
                {
                    _selectedPoint = point;
                    _surface?.Invalidate();
                }
                return;
            }
            if (id == "pointnext" || id == "pointprevious")
            {
                _view.PointPage += id == "pointnext" ? 1 : -1;   // clamped at paint
                _surface?.Invalidate();
                return;
            }
            if (id == "job:messages")
            {
                _view.ShowValidationMessages = !_view.ShowValidationMessages;
                _surface?.Invalidate();
                return;
            }
            if (id == "historynext" || id == "historyprevious")
            {
                // Paging the Recent prints list is view-local, like the go-to list: it
                // selects nothing and reaches no machine.
                int page = _view.RunPage + (id == "historynext" ? 1 : -1);
                _view.RunPage = Math.Max(0, Math.Min(page, Math.Max(0, _view.RunPageCount - 1)));
                _surface?.Invalidate();
                return;
            }
            if (id.StartsWith("log:select:", StringComparison.Ordinal))
            {
                _view.SelectedJobId = id.Substring(11);
                _surface?.Invalidate();
                return;
            }
            if (id.StartsWith("log:filter:", StringComparison.Ordinal))
            {
                string suffix = id.Substring("log:filter:".Length);
                _view.EventFilter = int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                                     && Enum.IsDefined(typeof(ConsoleEventCategory), idx)
                    ? (ConsoleEventCategory)idx : ConsoleEventCategory.All;
                _surface?.Invalidate();
                return;
            }
            if (id == "sendbox")
            {
                SendTerminalLine();
                return;
            }
            // Per-axis homing leaves the other axes unhomed and the arm configuration
            // unknown until a full Home - the same confirmation the 2.6.0 form showed.
            if ((id == "homex" || id == "homey" || id == "homez")
                && MessageBox.Show(this,
                    "Per-axis homing leaves the other axes unhomed and the arm configuration unknown until a full Home. Continue?",
                    "Lugworm Duet Console", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            // ---- logging workspace: dialogs and confirmations are the form's job ----
            if (id == "log:keepall")
            {
                LogRetentionStatus? logging;
                lock (_gate) logging = _latestState?.Logging;
                if (logging != null && logging.KeepAll) { _onAction("log:keepall:off"); return; }
                if (logging == null || logging.Destination == null)
                {
                    string? folder = ChooseFolder(null);
                    if (folder == null) return;   // cancelled: permanent logging stays off
                    _onAction("log:destination:" + folder);
                }
                _onAction("log:keepall:on");
                return;
            }
            if (id == "log:changedest")
            {
                string? current;
                lock (_gate) current = _latestState?.Logging.Destination;
                string? folder = ChooseFolder(current);
                if (folder != null) _onAction("log:destination:" + folder);
                return;
            }
            if (id.StartsWith("log:export:", StringComparison.Ordinal))
            {
                string jobId = id.Substring(11);
                if (jobId.Length == 0) return;
                using (var dialog = new SaveFileDialog
                {
                    Title = "Export a copy of the job record",
                    Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                    FileName = jobId + "_print.csv",
                    OverwritePrompt = true,
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    _onAction("log:exportto:" + jobId + "|" + dialog.FileName);
                }
                return;
            }
            if (id.StartsWith("log:discard:", StringComparison.Ordinal))
            {
                string jobId = id.Substring(12);
                if (jobId.Length == 0) return;
                if (MessageBox.Show(this,
                        "Delete the local buffer of job " + jobId + "?\n\nThis removes the staged CSV and events for good. "
                        + "Anything already saved at the destination is not touched.",
                        "Lugworm Duet Console", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                _onAction("log:discardconfirmed:" + jobId);
                return;
            }

            _onAction(id);
        }

        string? ChooseFolder(string? current)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Choose the folder kept job logs are copied to. Every job is buffered locally first; only kept jobs land here.",
                ShowNewFolderButton = true,
            })
            {
                // best effort: pre-selecting the folder the operator chose last time. A
                // path that no longer exists or is malformed simply opens the dialog at
                // its default root, which is what they would pick from anyway.
                if (!string.IsNullOrEmpty(current)) { try { dialog.SelectedPath = current!; } catch (Exception) { } }
                return dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                    ? dialog.SelectedPath : null;
            }
        }

        void OnSurfaceMove(object? sender, MouseEventArgs e)
        {
            string? id = _view.HitTest(e.X, e.Y);
            var surface = _surface;
            surface?.SetCursor(id != null ? Cursors.Hand : Cursors.Default);
            // The tooltip follows the region under the pointer, which is NOT the same as
            // the hot id: a disabled control has no hot id (so it never lights up, and a
            // click cannot leak through it) yet is the one whose tooltip carries the
            // refusal reason. Tracked separately, so hover feedback is unchanged.
            UpdateTip(_view.RegionAt(e.X, e.Y), e.X, e.Y);
            if (id == _view.HotId) return;
            _view.HotId = id;
            surface?.Invalidate();
        }

        /// <summary>Shows the hover text a page registered with its hit rectangle. Only
        /// pushed when the region changes, and it never invalidates the surface: a tooltip
        /// is a separate window, so this cannot add a repaint per mouse move.</summary>
        void UpdateTip(string? regionId, int x, int y)
        {
            if (string.Equals(regionId, _tipId, StringComparison.Ordinal)) return;
            _tipId = regionId;
            var host = _surface?.Control;
            if (host == null || host.IsDisposed) return;
            string? tip = _view.TipFor(regionId);
            // The Skia surface is one control with many drawn buttons, so there is nothing
            // to hang a per-control tooltip on: it is shown explicitly, positioned clear of
            // the cursor. Hide() first, or a second Show reuses the first one's placement.
            try
            {
                _tips.Hide(host);
                if (!string.IsNullOrEmpty(tip)) _tips.Show(tip, host, x + 18, y + 24, TipDurationMs);
            }
            catch (Exception) { /* the surface is going away; a tooltip is never worth a throw */ }
        }

        /// <summary>Long enough to read a two-sentence direction hint without re-hovering,
        /// short enough that a tooltip left behind by a lost mouse-leave still goes away.</summary>
        const int TipDurationMs = 20000;

        /// <summary>Drops any tooltip the drawn surface is showing.</summary>
        void ClearTip()
        {
            _tipId = null;
            var host = _surface?.Control;
            if (host == null || host.IsDisposed) return;
            // best effort: the surface is going away; a tooltip is never worth a throw.
            try { _tips.Hide(host); } catch (Exception) { }
        }

        void OnTerminalKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SendTerminalLine();
        }

        void SendTerminalLine() => _onAction("send:" + _terminal.Text);

        /// <summary>Places the native input over the rectangle the view reserved for it.</summary>
        void LayoutTerminal()
        {
            if (_surface == null) return;
            if (!_view.TerminalInputVisible || _view.ActiveTab != ConsoleView.Tab.Terminal)
            {
                if (_terminal.Visible) _terminal.Visible = false;
                return;
            }
            var surface = _surface;
            if (surface == null) return;
            var r = _view.TerminalInputRect;
            var host = surface.Control;
            var bounds = new Rectangle(host.Left + (int)r.Left, host.Top + (int)r.Top + 3,
                                       Math.Max(40, (int)r.Width), Math.Max(20, (int)r.Height - 6));
            if (_terminal.Bounds != bounds) _terminal.Bounds = bounds;
            if (!_terminal.Visible) _terminal.Visible = true;
            _terminal.BringToFront();
        }

        // ---- chrome -------------------------------------------------------------------

        void UpdateFooter()
        {
            PrintConsoleState? state;
            ButtonSet? buttons;
            lock (_gate) { state = _latestState; buttons = _latestButtons; }
            if (state == null || buttons == null) return;

            string last = state.PendingAction ?? (state.LastResult.Length > 0 ? state.LastResult : "-");
            // A live recovery outranks the last result: after an emergency stop the
            // operator needs to be told what is actually known - that the endpoint is not
            // answering, or that a reset's delivery was never confirmed - and no button
            // state can say that on its own.
            string? recovery = state.RecoveryNotice;
            _footerText.Text = recovery ?? ("last: " + last);
            _footerText.ForeColor = state.LastError != null
                ? Color.FromArgb(0xE5, 0x48, 0x4D)
                : recovery != null || state.LastActionWarned
                    ? Color.FromArgb(0xF0, 0xB4, 0x4D) : Color.FromArgb(0xAA, 0xB4, 0xBF);

            // logging health in the middle, only when it needs attention
            string? attention = state.Logging.Attention;
            _footerHealth.Visible = attention != null;
            if (attention != null)
            {
                _footerHealth.Text = attention;
                _footerHealth.ForeColor = attention.StartsWith("logging fault", StringComparison.Ordinal)
                    ? Color.FromArgb(0xE5, 0x48, 0x4D) : Color.FromArgb(0xF0, 0xB4, 0x4D);
            }

            _reset.Enabled = buttons.Reset;
            // The label names what the press will actually do - the blind recovery after
            // an emergency stop arms on the first press and sends on the second, and a
            // button reading "Reset (M999)" would not say that.
            string resetLabel = ResetPolicy.Decide(state.ResetContext(), DateTime.UtcNow).Label;
            if (_reset.Text != resetLabel) _reset.Text = resetLabel;
            _reset.ForeColor = buttons.Reset ? Color.FromArgb(0xF2, 0xF5, 0xF7) : Color.FromArgb(0x74, 0x81, 0x8E);
            _estop.Enabled = buttons.EStop;
            // An explicit BackColor paints whether or not the button is enabled, so a
            // disabled E-STOP would still look armed. It is the one control an operator
            // must never misread, so its disabled state is made unmistakable.
            _estop.BackColor = buttons.EStop ? Color.FromArgb(0xE5, 0x48, 0x4D) : Color.FromArgb(0x2A, 0x30, 0x37);
            _estop.ForeColor = buttons.EStop ? Color.White : Color.FromArgb(0x74, 0x81, 0x8E);
            // The footer label ellipsizes a long notice, so the full text also goes on the
            // tooltip of the very button the operator is about to press.
            if (buttons.Reset && recovery != null)
            {
                if (_tips.GetToolTip(_reset) != recovery) _tips.SetToolTip(_reset, recovery);
            }
            else SetTip(_reset, buttons.Reset, buttons, "reset");
            SetTip(_estop, buttons.EStop, buttons, "estop");
        }

        readonly ToolTip _tips = new ToolTip { ShowAlways = true };

        void SetTip(Control control, bool enabled, ButtonSet buttons, string id)
        {
            string tip = enabled ? "" : buttons.Reasons.TryGetValue(id, out string? r) ? r : "";
            if (_tips.GetToolTip(control) != tip) _tips.SetToolTip(control, tip);
        }

        void OnSurfaceKindChanged(SurfaceKind kind)
        {
            // A renderer swap replaces the control under the mouse; its mouse-up is lost,
            // and any tooltip is owned by the control that is going away.
            ReleaseHeldJog();
            ClearTip();
            void Apply()
            {
                _degraded.Visible = kind == SurfaceKind.None;
                // Say WHAT failed and where. "missing or unusable" sent the first field
                // report chasing a missing file when the real fault was a 32-bit native
                // copied into a 64-bit process.
                string? why = _surface?.NativeFailure;
                if (kind == SurfaceKind.None && !string.IsNullOrEmpty(why))
                {
                    _degraded.Text = "The console could not start its renderer: " + why
                                   + "  -  E-STOP and Reset below still work; use the canvas components for everything else.";
                    _tips.SetToolTip(_degraded, why);
                }
                _terminal.Visible = _terminal.Visible && kind != SurfaceKind.None;
                string? glWhy = _surface?.GlFailure;
                _tips.SetToolTip(_footerText, kind == SurfaceKind.Cpu && !string.IsNullOrEmpty(glWhy)
                    ? "OpenGL was requested but fell back to CPU raster: " + glWhy : "");
                UpdateFooter();
            }
            if (IsHandleCreated && InvokeRequired)
            {
                // best effort: the window was destroyed between the handle check and the
                // marshal - there is no footer left to relabel. The renderer's own failure
                // text is still carried on SkiaSurfaceHost.GlFailure for the next open.
                try { BeginInvoke((Action)Apply); } catch (Exception) { }
            }
            else Apply();
        }

        ConsoleTheme NewTheme()
        {
            float scale = DeviceDpi <= 0 ? 1f : DeviceDpi / 96f;
            return new ConsoleTheme(scale);
        }

        // ---- native sizes: design values at 96 DPI ------------------------------------
        // The form sets no AutoScaleMode, so WinForms scales none of its own sizes, while
        // everything Skia draws is scaled by DeviceDpi / 96. Unscaled, 150 % left a
        // 640 x 429 logical surface, below the 820 design minimum, and the pages laid
        // whole cards out below it.

        static readonly Size DesignClientSize = new Size(960, 700);
        static readonly Size DesignMinimumSize = new Size(820, 620);

        static int ScalePx(int design, int dpi) => dpi <= 0 ? design : (int)Math.Round(design * dpi / 96.0);

        static Size ScaleSize(Size design, int dpi) => new Size(ScalePx(design.Width, dpi), ScalePx(design.Height, dpi));

        /// <summary>Never larger than the screen's working area: the footer with E-STOP
        /// must stay on screen whatever the scale, even if the pages then fall back to
        /// their narrow layout.</summary>
        static Size FitTo(Size size, Rectangle area)
            => area.Width <= 0 || area.Height <= 0 ? size
             : new Size(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));

        /// <summary>The native footer and banner, scaled like everything Skia draws.
        /// Absolute from the design values rather than multiplied by a ratio, so the result
        /// is the same whether or not the framework already rescaled the child controls on
        /// a DPI change (.NET Framework does, .NET 7 does not).</summary>
        void ApplyNativeMetrics(int dpi)
        {
            int Px(int design) => ScalePx(design, dpi);
            _footer.Height = Px(56);
            _footer.Padding = new Padding(Px(12), Px(7), Px(12), Px(7));
            _estop.Size = new Size(Px(150), Px(42));
            _reset.Size = new Size(Px(140), Px(38));
            _footerHealth.Width = Px(260);
            _footerHealth.Padding = new Padding(0, 0, Px(12), 0);
            _degraded.Height = Px(36);
            _degraded.Padding = new Padding(Px(12), 0, Px(12), 0);
        }

        // ---- window settings (user-global, atomic; never the active page) ----------------

        void RestoreWindowBounds()
        {
            try
            {
                var bounds = LogRetentionService.Instance.Settings.WindowBounds;
                if (bounds == null || bounds.Length != 4) { StartPosition = FormStartPosition.CenterScreen; return; }
                var rect = new Rectangle(bounds[0], bounds[1], Math.Max(MinimumSize.Width, bounds[2]), Math.Max(MinimumSize.Height, bounds[3]));
                // only a rectangle that is at least partly on a current screen is accepted
                bool visible = false;
                foreach (var screen in Screen.AllScreens)
                    if (screen.WorkingArea.IntersectsWith(rect)) { visible = true; break; }
                if (!visible) { StartPosition = FormStartPosition.CenterScreen; return; }
                StartPosition = FormStartPosition.Manual;
                Bounds = rect;
            }
            // Unreadable settings, an unreachable settings folder or a screen enumeration
            // that fails all mean the same thing here: no usable saved rectangle, so the
            // window opens centred. The settings fault itself is reported on the console's
            // own retention status line, not by moving the window.
            catch (Exception) { StartPosition = FormStartPosition.CenterScreen; }
        }

        void PersistBounds()
        {
            if (WindowState != FormWindowState.Normal) return;
            // best effort: the window position is a convenience. The service records an
            // unwritable settings file as a warning on its status, which the console shows.
            try { LogRetentionService.Instance.SetWindowBounds(Left, Top, Width, Height); } catch (Exception) { }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            PersistBounds();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // The window itself is resized by the base, to the rectangle Windows suggests,
            // which is already scaled by the DPI ratio; scaling it here again would double
            // it. The minimum goes first, or the old one stops a move to a lower-DPI
            // monitor from shrinking the window to that rectangle, and again after the
            // base, which may rescale it (.NET Framework).
            var area = Screen.FromRectangle(e.SuggestedRectangle).WorkingArea;
            MinimumSize = FitTo(ScaleSize(DesignMinimumSize, e.DeviceDpiNew), area);
            base.OnDpiChanged(e);
            MinimumSize = FitTo(ScaleSize(DesignMinimumSize, e.DeviceDpiNew), area);
            ApplyNativeMetrics(e.DeviceDpiNew);
            // Per-monitor DPI: rebuild the metrics and let the next paint re-lay-out and
            // re-derive the hit regions. No SetHighDpiMode call - the host owns the mode.
            _theme = NewTheme();
            _view.SetTheme(_theme);
            LayoutTerminal();
            _surface?.Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Reached during construction, before _surface exists: setting ClientSize is
            // itself a resize.
            LayoutTerminal();
            _surface?.Invalidate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            // Another window took the focus while a jog button was down: the mouse-up will
            // land there, never here.
            ReleaseHeldJog();
            _view.Interaction.Release();
            base.OnDeactivate(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible)
            {
                ReleaseHeldJog();
                // Only a paint switches the animation off, and a hidden window never
                // paints: a pending action would keep it invalidating every 16 ms.
                _animation.Enabled = false;
            }
            base.OnVisibleChanged(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The window is modeless and reopened from the capsule: hide rather than
            // destroy, so the component's projection and lease rules stay in charge.
            // Closing never stops recording or synchronisation - those live elsewhere.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                PersistBounds();
                Hide();
                _onHidden();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseHeldJog();
                _animation.Dispose();
                _surface?.Dispose();
                _tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
