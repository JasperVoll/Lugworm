using System;
using System.Net.Http;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Machine
{
    /// <summary>The one place a transport is chosen: by the robot definition's
    /// <see cref="RobotControlKind"/>, never by a socket of its own (ARCHI §12.1).</summary>
    public static class MachineClientFactory
    {
        /// <param name="kind">The requested direct-control transport.</param>
        /// <param name="host">Host or host:port, no scheme.</param>
        /// <param name="password">Duet password (empty = the firmware default) or the
        /// Duet password.</param>
        /// <param name="handler">Test seam: a scripted <see cref="HttpMessageHandler"/>.</param>
        public static IMachineClient Create(RobotControlKind kind, string host, string password,
                                            HttpMessageHandler? handler = null)
        {
            switch (kind)
            {
                case RobotControlKind.Duet:
                    return new DuetPollClient(new DuetClientOptions { Host = host, Password = password, Handler = handler });
                default:
                    throw new ArgumentException($"profile control kind {kind} has no transport", nameof(kind));
            }
        }
    }
}
