using System;
using System.Drawing;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Lugworm.Ui.Console
{
    /// <summary>Which surface is actually painting.</summary>
    public enum SurfaceKind
    {
        /// <summary>Skia CPU raster (<c>SKControl</c>) - the default (decision D6).</summary>
        Cpu,
        /// <summary>Skia on OpenGL (<c>SKGLControl</c>) - opt-in.</summary>
        OpenGl,
        /// <summary>Skia could not be used at all. The window degrades to native controls;
        /// E-STOP and Reset are unaffected because they were never drawn by Skia.</summary>
        None,
    }

    /// <summary>Hosts the Skia drawing surface for the console and owns the decision of
    /// WHICH surface that is.</summary>
    /// <remarks>
    /// <para>CPU raster is the default and OpenGL is opt-in because in-process GL inside
    /// Rhino was never verified: the spike's scripted in-Rhino probe would not run, so GL
    /// is a manual test row rather than a ship gate.</para>
    /// <para>A GL-less host does NOT fail at construction - <c>SKGLControl</c> throws on
    /// its first paint (SkiaSharp issue 2676) - so the paint handler is wrapped and the
    /// first failure swaps the surface for <c>SKControl</c> through
    /// <see cref="Control.BeginInvoke(Delegate)"/>, never from inside the paint call
    /// itself. Later failures stay guarded rather than throwing into the message loop.</para>
    /// <para>If even <c>SKControl</c> cannot paint - the native <c>libSkiaSharp</c> is
    /// missing or is the wrong build - the host reports <see cref="SurfaceKind.None"/> and
    /// the form falls back to a plain native panel. <c>SKControl</c> is deliberately NOT
    /// treated as a resilience boundary against that: a missing native kills both surfaces,
    /// which is exactly why E-STOP and Reset are real WinForms buttons outside this
    /// control.</para>
    /// </remarks>
    public sealed class SkiaSurfaceHost : IDisposable
    {
        readonly Panel _container = new Panel();
        readonly Action<SKCanvas, int, int> _paint;

        Control? _surface;
        SurfaceKind _kind = SurfaceKind.None;
        bool _glFallbackPending;
        bool _disposed;

        /// <summary>The control to dock into the form. This is the CONTAINER, not the
        /// drawing control - the surface is a child that fills it and is swapped out on a
        /// GL fallback. Never subscribe mouse input here: the child covers the container
        /// and receives every event, so handlers attached to it would never fire. Use the
        /// forwarded events below, which follow the swap.</summary>
        public Control Control => _container;

        /// <summary>Mouse input on whichever surface is currently painting. Coordinates are
        /// in the surface's client pixels, the same space the paint callback receives, so
        /// they can be hit-tested against the regions the last paint produced.</summary>
        public event MouseEventHandler? SurfaceMouseDown;
        public event MouseEventHandler? SurfaceMouseUp;
        public event MouseEventHandler? SurfaceMouseMove;
        public event MouseEventHandler? SurfaceMouseClick;
        public event EventHandler? SurfaceMouseLeave;
        /// <summary>The painting surface lost (or changed) mouse capture - a modal dialog,
        /// an Alt-Tab, a swap of the surface itself. A press-and-hold must end here,
        /// because the matching mouse-up will never arrive.</summary>
        public event EventHandler? SurfaceMouseCaptureChanged;

        /// <summary>True while the painting surface holds the mouse.</summary>
        public bool SurfaceHasCapture => _surface != null && !_surface.IsDisposed && _surface.Capture;

        /// <summary>Cursor of the painting surface (hand over an enabled control).</summary>
        public void SetCursor(Cursor cursor)
        {
            var s = _surface;
            if (s == null || s.IsDisposed) return;
            if (s.Cursor != cursor) s.Cursor = cursor;
        }

        void Subscribe(Control control)
        {
            control.MouseDown += OnMouseDown;
            control.MouseUp += OnMouseUp;
            control.MouseMove += OnMouseMove;
            control.MouseClick += OnMouseClick;
            control.MouseLeave += OnMouseLeave;
            control.MouseCaptureChanged += OnMouseCaptureChanged;
        }

        void Unsubscribe(Control control)
        {
            control.MouseDown -= OnMouseDown;
            control.MouseUp -= OnMouseUp;
            control.MouseMove -= OnMouseMove;
            control.MouseClick -= OnMouseClick;
            control.MouseLeave -= OnMouseLeave;
            control.MouseCaptureChanged -= OnMouseCaptureChanged;
        }

        void OnMouseDown(object? sender, MouseEventArgs e) => SurfaceMouseDown?.Invoke(this, e);
        void OnMouseUp(object? sender, MouseEventArgs e) => SurfaceMouseUp?.Invoke(this, e);
        void OnMouseMove(object? sender, MouseEventArgs e) => SurfaceMouseMove?.Invoke(this, e);
        void OnMouseClick(object? sender, MouseEventArgs e) => SurfaceMouseClick?.Invoke(this, e);
        void OnMouseLeave(object? sender, EventArgs e) => SurfaceMouseLeave?.Invoke(this, e);
        void OnMouseCaptureChanged(object? sender, EventArgs e) => SurfaceMouseCaptureChanged?.Invoke(this, e);

        /// <summary>The paint boundary. <c>SKGLControl</c> creates its GL context inside
        /// <c>OnPaint</c>, so a GL-less host throws BEFORE the PaintSurface callback is ever
        /// raised (SkiaSharp issue 2676); a guard around the callback alone cannot see it.
        /// Both surfaces therefore wrap the whole paint lifecycle and report to the host,
        /// which swaps or retires the surface outside the paint call.</summary>
        sealed class GuardedGlControl : SKGLControl
        {
            readonly Action<Exception> _failed;
            public GuardedGlControl(Action<Exception> failed) { _failed = failed; }
            protected override void OnPaint(PaintEventArgs e)
            {
                try { base.OnPaint(e); }
                catch (Exception ex) { _failed(ex); }
            }
        }

        sealed class GuardedCpuControl : SKControl
        {
            readonly Action<Exception> _failed;
            public GuardedCpuControl(Action<Exception> failed) { _failed = failed; }
            protected override void OnPaint(PaintEventArgs e)
            {
                try { base.OnPaint(e); }
                catch (Exception ex) { _failed(ex); }
            }
        }

        public SurfaceKind Kind => _kind;

        /// <summary>Why there is no renderer, or null. Names the path and the reason - a
        /// bare "missing or unusable" is not something an operator can act on.</summary>
        public string? NativeFailure { get; private set; }

        /// <summary>Why OpenGL was abandoned for CPU raster, or null while GL was never
        /// tried or is still painting. The footer names the renderer that actually runs;
        /// this says why it is not the one that was asked for.</summary>
        public string? GlFailure { get; private set; }

        /// <summary>Human-readable renderer name for the footer.</summary>
        public string KindLabel
        {
            get
            {
                switch (_kind)
                {
                    case SurfaceKind.OpenGl: return "OpenGL";
                    case SurfaceKind.Cpu: return "CPU";
                    default: return "unavailable";
                }
            }
        }

        /// <summary>Raised when the active surface changes, so the footer can name the
        /// renderer that is really running.</summary>
        /// <remarks>Deliberately an event set AFTER construction rather than a constructor
        /// callback: the constructor already picks a surface, and invoking a caller-supplied
        /// delegate from in there re-enters a half-built owner - the form's field holding
        /// this host is still unassigned at that point. Read <see cref="Kind"/> once after
        /// constructing to get the initial value.</remarks>
        public event Action<SurfaceKind>? KindChanged;

        /// <param name="paint">Draws one frame: canvas, width and height in device pixels.</param>
        /// <param name="preferOpenGl">The persisted opt-in.</param>
        public SkiaSurfaceHost(Action<SKCanvas, int, int> paint, bool preferOpenGl)
        {
            _paint = paint ?? throw new ArgumentNullException(nameof(paint));
            _container.Dock = DockStyle.Fill;
            _container.Margin = Padding.Empty;

            NativeFailure = SkiaNativeLoader.Failure ?? NativeProbeFailure();
            if (NativeFailure != null)
            {
                SetKind(SurfaceKind.None);
                return;
            }
            if (preferOpenGl && TryCreateGl()) return;
            CreateCpu();
        }

        /// <summary>Cheap probe that the native library loads and can allocate. Construction
        /// of SKControl succeeds without touching the native, so without this the first
        /// paint would be the discovery point.</summary>
        /// <returns>Null when Skia works, otherwise what failed - the degraded banner says
        /// it, because "could not allocate" alone sends the operator guessing.</returns>
        static string? NativeProbeFailure()
        {
            try
            {
                SkiaNativeLoader.Ensure();
                using (var bmp = new SKBitmap(1, 1))
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Black);
                    return null;
                }
            }
            catch (Exception ex) { return "Skia could not allocate: " + Describe(ex); }
            // Intentionally broad: a missing or mismatched native surfaces as
            // DllNotFoundException, BadImageFormatException or TypeInitializationException
            // depending on how it failed, and any of them means "no Skia".
        }

        bool TryCreateGl()
        {
            try
            {
                var gl = new GuardedGlControl(OnGlFailed);
                gl.Dock = DockStyle.Fill;
                gl.PaintSurface += OnGlPaint;
                Attach(gl);
                SetKind(SurfaceKind.OpenGl);
                return true;
            }
            catch (Exception ex)
            {
                GlFailure = Describe(ex);
                return false;   // no GL context at all - fall through to CPU
            }
        }

        void CreateCpu()
        {
            var cpu = new GuardedCpuControl(OnCpuFailed);
            cpu.Dock = DockStyle.Fill;
            cpu.PaintSurface += OnCpuPaint;
            Attach(cpu);
            SetKind(SurfaceKind.Cpu);
        }

        static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

        /// <summary>Any failure inside the GL surface paint - context creation, the first
        /// frame, a later one - retires it for CPU raster. Never from inside the paint
        /// call: disposing the control that is currently painting would fault.</summary>
        void OnGlFailed(Exception ex)
        {
            if (GlFailure == null) GlFailure = Describe(ex);
            FallBackToCpu();
        }

        /// <summary>Nothing is left to fall back to. The surface is retired so it stops
        /// throwing on every frame, and the host reports <see cref="SurfaceKind.None"/>
        /// with the reason; the native footer keeps working regardless.</summary>
        void OnCpuFailed(Exception ex)
        {
            if (_disposed || _kind == SurfaceKind.None) return;
            NativeFailure = "CPU raster paint failed - " + Describe(ex);
            SetKind(SurfaceKind.None);
            try
            {
                _container.BeginInvoke((Action)(() =>
                {
                    if (_disposed) return;
                    var failed = _surface;
                    _surface = null;
                    if (failed == null) return;
                    Unsubscribe(failed);
                    _container.Controls.Remove(failed);
                    failed.Dispose();
                }));
            }
            catch (Exception) { /* no handle: nothing is painting anyway */ }
        }

        void Attach(Control control)
        {
            var previous = _surface;
            _surface = control;
            Subscribe(control);
            _container.SuspendLayout();
            try
            {
                _container.Controls.Add(control);
                if (previous != null)
                {
                    Unsubscribe(previous);
                    _container.Controls.Remove(previous);
                    previous.Dispose();
                }
            }
            finally { _container.ResumeLayout(true); }
        }

        // The callbacks run INSIDE the guarded OnPaint, so a throwing frame reaches
        // OnGlFailed / OnCpuFailed through the same boundary as a context failure.
        void OnGlPaint(object? sender, SKPaintGLSurfaceEventArgs e)
            => _paint(e.Surface.Canvas, e.Info.Width, e.Info.Height);

        void OnCpuPaint(object? sender, SKPaintSurfaceEventArgs e)
            => _paint(e.Surface.Canvas, e.Info.Width, e.Info.Height);

        void FallBackToCpu()
        {
            if (_glFallbackPending || _disposed) return;
            _glFallbackPending = true;
            try
            {
                _container.BeginInvoke((Action)(() =>
                {
                    _glFallbackPending = false;
                    if (_disposed) return;
                    CreateCpu();
                    _container.Invalidate(true);
                }));
            }
            catch (Exception)
            {
                _glFallbackPending = false;   // no handle yet; the next paint retries
            }
        }

        void SetKind(SurfaceKind kind)
        {
            if (_kind == kind) return;
            _kind = kind;
            try { KindChanged?.Invoke(kind); } catch (Exception) { /* the footer is cosmetic */ }
        }

        /// <summary>Requests a repaint. Safe to call when no surface exists.</summary>
        public void Invalidate() => _surface?.Invalidate();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_surface != null) { Unsubscribe(_surface); _surface.Dispose(); }
            _container.Dispose();
        }
    }
}
