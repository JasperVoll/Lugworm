using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Machine
{
    /// <summary>What a transport can and cannot do, so the session and the console
    /// hide what a machine would refuse instead of offering it and failing.</summary>
    public sealed class MachineCapabilities
    {
        public string Name { get; }
        /// <summary>Firmware dry run (RRF M37).</summary>
        public bool Simulate { get; }
        /// <summary>Per-axis home macro files that can be read and replayed (RRF
        /// 0:/sys/home*.g).</summary>
        public bool HomeMacros { get; }
        /// <summary>A reset that may run outside Halted/Off behind an armed override
        /// (the hidden Jog component's M999).</summary>
        public bool MaintenanceReset { get; }
        /// <summary>The wording of the motion-baseline source in log lines.</summary>
        public string MotionBaselineSource { get; }
        public MachineCapabilities(string name, bool simulate, bool homeMacros,
                                   bool maintenanceReset, string motionBaselineSource)
        {
            Name = name; Simulate = simulate; HomeMacros = homeMacros;
            MaintenanceReset = maintenanceReset; MotionBaselineSource = motionBaselineSource;
        }

        public static readonly MachineCapabilities Duet =
            new MachineCapabilities("Duet RRF", simulate: true, homeMacros: true,
                                    maintenanceReset: true, motionBaselineSource: "0:/sys/config.g");

        /// <summary>The capabilities a transport of this kind will report, before a client
        /// exists - so a component can hide a control at solve time and the session can
        /// gate before its first connection.</summary>
        public static MachineCapabilities For(RobotControlKind kind) => Duet;
    }

    /// <summary>The transport seam (4.0.0). Exactly the members the session, the
    /// components and the safety gates call on the Duet client. Every method
    /// keeps the Duet client's contract: bounded, serialized by the caller's command
    /// lock, throwing <see cref="DuetException"/> on a refused or failed request.
    /// A transport that lacks a feature reports it through
    /// <see cref="Capabilities"/> and throws a clear <see cref="DuetException"/>
    /// when the method is called anyway - it never silently no-ops.</summary>
    public interface IMachineClient : IDisposable
    {
        MachineCapabilities Capabilities { get; }

        // ---- connection and identity ----
        bool IsConnected { get; }
        long ConnectionGeneration { get; }
        int ApiLevel { get; }
        string? BoardType { get; }
        /// <summary>Legacy status API (RRF before 3.2).</summary>
        bool Legacy { get; }
        bool LegacyM208Probed { get; }
        MachineFingerprint? Fingerprint { get; }
        string? FingerprintProbeDetail { get; }
        Task ConnectAsync(CancellationToken ct);
        Task DisconnectAsync(CancellationToken ct);

        // ---- probed firmware state ----
        bool KinematicsProbed { get; }
        /// <summary>SCARA kinematics when the firmware reports them; null on a
        /// Cartesian board. The Cartesian gate refuses a non-null value.</summary>
        M669Parameters? Kinematics { get; }
        string? KinematicsProbeDetail { get; }
        /// <summary>Why the firmware's kinematics cannot be validated by the Cartesian
        /// gate, or null. Non-null refuses
        /// upload/start regardless of everything else - fail closed.</summary>
        string? KinematicsRefusal { get; }
        FirmwareMotionLimits? MotionBaseline { get; }
        bool MotionBaselineProbed { get; }
        bool HomeProbeComplete { get; }
        bool HomeAvailable(string axis);
        Task<LiveMotionState?> QueryLiveMotionStateAsync(CancellationToken ct);
        /// <summary>Raw object-model / printer-object text for diagnostics.</summary>
        Task<string> GetModelRawAsync(string key, string flags, CancellationToken ct);

        // ---- status and replies ----
        event Action? OpaqueMotionIssued;
        Task<ObjectModelSnapshot> GetSnapshotAsync(bool includeJob, CancellationToken ct);
        Task BeginReplyPollingAsync(CancellationToken ct);
        void EndReplyPolling();
        string? TakeDrainedReply();
        Task<string?> FetchNewReplyAsync(CancellationToken ct);

        // ---- commands ----
        Task<string> SendCodeAsync(string gcode, CancellationToken ct);
        Task<string?> SendCodeAndWaitForReplyAsync(string gcode, TimeSpan timeout, CancellationToken ct);
        Task SendCodeNoReplyAsync(string gcode, CancellationToken ct);
        Task EmergencyStopAsync(CancellationToken ct);
        /// <summary>Firmware reset (RRF <c>M999</c>).
        /// Returns what the transport could actually observe: a board that resets before
        /// answering is INDISTINGUISHABLE from a dead network, so an unanswered send is
        /// reported as <see cref="ResetDelivery.Uncertain"/> rather than as success. A
        /// refusal the endpoint really answered (401, 403, an error status) still throws
        /// <see cref="DuetException"/>.</summary>
        Task<ResetDelivery> ResetAsync(CancellationToken ct);

        // ---- job files and control ----
        Task<UploadVerification> UploadAsync(string remotePath, byte[] content, CancellationToken ct);
        Task<UploadVerification> UploadAsync(string remotePath, Stream content, long length, CancellationToken ct);
        Task VerifyRemoteContentAsync(string remotePath, byte[] expected, CancellationToken ct);
        Task<long> GetFileSizeAsync(string remotePath, CancellationToken ct);
        Task<List<DuetFileEntry>> ListFilesAsync(string dir, CancellationToken ct);
        Task<byte[]> DownloadAsync(string remotePath, CancellationToken ct);
        Task<TransferVerification> DownloadToAsync(string remotePath, Stream destination, CancellationToken ct);
        Task DeleteFileAsync(string remotePath, CancellationToken ct, bool maintenanceOverride = false);
        Task MoveFileAsync(string oldPath, string newPath, CancellationToken ct, bool maintenanceOverride = false);
        Task StartPrintAsync(string remotePath, CancellationToken ct);
        Task SimulatePrintAsync(string remotePath, CancellationToken ct);
        Task PauseAsync(CancellationToken ct);
        Task ResumeAsync(CancellationToken ct);
        Task CancelAsync(CancellationToken ct);
    }
}
