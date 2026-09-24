using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lugworm.Duet
{
    /// <summary>Registry of one shared session per canonical host. Documents hold
    /// explicit owner leases; the last lease releases the connection.</summary>
    public static class MachineSessionManager
    {
        sealed class Entry
        {
            public readonly MachineSession Session;
            public readonly ConcurrentDictionary<Guid, byte> Owners = new ConcurrentDictionary<Guid, byte>();
            public Entry(MachineSession session) { Session = session; }
        }

        static readonly ConcurrentDictionary<string, Entry> Sessions =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static int _hooked;

        public static string CanonicalHost(string host)
        {
            string raw = (host ?? "").Trim().TrimEnd('/');
            if (raw.Length == 0) return "";
            Uri uri;
            try { uri = new Uri(raw.Contains("://") ? raw : "http://" + raw, UriKind.Absolute); }
            // A host string the Uri parser rejects is still a usable dictionary key: it
            // is canonicalised by case alone, so two components typing the same bad host
            // share one session instead of opening two. The caller reports the failure
            // when it tries to connect.
            catch (UriFormatException) { return raw.ToLowerInvariant(); }
            // Uri.Host already returns an IPv6 literal in brackets on every TFM; wrapping it
            // again made a key ("[[fe80::1]]:80") no client URI can be built from.
            return uri.Host.ToLowerInvariant() + ":" + uri.Port;
        }

        public static MachineSession GetOrCreate(string host, string password)
        {
            EnsureCloseHook();
            string key = CanonicalHost(host);
            if (key.Length == 0) throw new ArgumentException("machine host is empty", nameof(host));
            var entry = Sessions.GetOrAdd(key, _ => new Entry(new MachineSession(key, password)));
            if (!string.IsNullOrEmpty(password)) entry.Session.SetPassword(password);
            return entry.Session;
        }

        public static MachineSession Acquire(string host, string password, Guid owner,
                                          Core.Duet.MachineFingerprint? expectedFingerprint = null)
        {
            var session = GetOrCreate(host, password);
            string key = CanonicalHost(host);
            if (Sessions.TryGetValue(key, out var entry))
            {
                entry.Owners[owner] = 0;
                // the expectation must be installed before the session starts probing,
                // or a fast connect could auto-baseline a mismatched machine (AUD-SAF-004).
                // Installed per owner: owners sharing the host must agree, conflicting
                // persisted identities fail closed instead of last-writer-wins.
                if (expectedFingerprint != null) entry.Session.InstallPersistedExpectation(owner, expectedFingerprint);
                entry.Session.Start();
            }
            return session;
        }

        public static void Release(string host, Guid owner)
        {
            string key = CanonicalHost(host);
            if (!Sessions.TryGetValue(key, out var entry)) return;
            entry.Owners.TryRemove(owner, out _);
            entry.Session.ForgetPersistedExpectation(owner);
            if (entry.Owners.IsEmpty && ((ICollection<KeyValuePair<string, Entry>>)Sessions).Remove(new KeyValuePair<string, Entry>(key, entry)))
            {
                entry.Session.Stop();
                _ = entry.Session.DisposeAsync();
            }
        }

        /// <summary>Closes the session for a host outright, regardless of how many
        /// components still hold leases.
        ///
        /// <see cref="Release"/> only drops one owner, and stops the session solely when
        /// the last one goes. Every Duet component takes its own lease, so on a canvas
        /// holding a Status or Jog component - the normal case - pressing Disconnect on
        /// the Connect component dropped that one lease and left the session polling.
        /// Disconnect is an explicit operator instruction to end the connection, so it
        /// closes the host rather than releasing a share of it. Nothing re-acquires
        /// implicitly: the Session accessor only peeks, so the other components simply
        /// observe that there is no session.</summary>
        public static void CloseHost(string host)
        {
            string key = CanonicalHost(host);
            if (key.Length == 0) return;
            if (!Sessions.TryRemove(key, out var entry)) return;
            entry.Owners.Clear();
            // best effort: the entry is already out of the map, so nothing can reach this
            // session again - a teardown fault must not stop the caller disconnecting.
            try { entry.Session.Stop(); _ = entry.Session.DisposeAsync(); } catch (Exception) { }
        }

        /// <summary>True when this owner is still registered against the CURRENT entry for
        /// the host. Components cache the host they leased, but <see cref="CloseHost"/>
        /// removes the entry outright, so a cached host string alone cannot prove the
        /// component is an owner of the session that exists now.</summary>
        public static bool HasOwner(string host, Guid owner)
        {
            string key = CanonicalHost(host);
            return key.Length != 0 && Sessions.TryGetValue(key, out var entry)
                                   && entry.Owners.ContainsKey(owner);
        }

        public static MachineSession? Peek(string host)
            => host != null && Sessions.TryGetValue(CanonicalHost(host), out var e) ? e.Session : null;

        /// <summary>How long Rhino's closing waits, in total, for the sessions to finish
        /// disposing before the retention service shuts down.</summary>
        const int ClosingWaitMs = 3000;

        /// <summary>Stops and disposes every session; returns the disposal tasks.</summary>
        public static Task[] DisposeAll()
        {
            var disposing = new List<Task>();
            foreach (var kv in Sessions)
            {
                // best effort: Rhino is closing and every session must get its turn -
                // one that cannot be stopped must not hold back the others.
                try { kv.Value.Session.Stop(); disposing.Add(kv.Value.Session.DisposeAsync()); } catch (Exception) { }
            }
            Sessions.Clear();
            return disposing.ToArray();
        }

        /// <summary>Rhino is closing. The sessions' final job-log records (the end row and
        /// the outcome) are enqueued as their poll loops unwind, and the retention service
        /// drops anything that arrives after Shutdown, so the disposals are waited for
        /// first - bounded, because a session stuck in a request must not hold Rhino open.
        /// No disposal step needs the UI thread, so blocking it here cannot deadlock.</summary>
        static void OnRhinoClosing()
        {
            var disposing = DisposeAll();
            // best effort: a disposal that faults or outlives the wait is abandoned; the
            // clean shutdown below still runs, and what did not arrive is recovered from
            // the staging buffer at the next launch.
            try { if (disposing.Length > 0) Task.WaitAll(disposing, ClosingWaitMs); } catch (Exception) { }
            LogRetentionService.Instance.Shutdown();
        }

        static void EnsureCloseHook()
        {
            if (System.Threading.Interlocked.Exchange(ref _hooked, 1) != 0) return;
            try
            {
                // Sessions first (their retention recorders finish), then the clean
                // shutdown that removes completed, unsaved temporary job logs.
                Rhino.RhinoApp.Closing += (s, e) => OnRhinoClosing();
            }
            catch (Exception) { /* headless test host without RhinoApp */ }
            // Warm the retention service on the UI thread so recovery of leftover
            // buffers never runs on a poll thread.
            // best effort: the service reports its own faults on the console's retention
            // status line, and a warm-up that fails must not stop a session being created.
            try { _ = LogRetentionService.Instance; } catch (Exception) { }
        }
    }
}
