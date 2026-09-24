using System;
using System.Collections.Generic;
using Lugworm.Core.Settings;

namespace Lugworm.Ui
{
    /// <summary>Which settings folder is in force for which open document. One
    /// Load Settings component per document registers its scan here; every
    /// "⇤ inputs" button then asks this before it creates anything, and falls back to
    /// the built-in ParamSpec defaults when there is no entry or no matching file.
    ///
    /// Session-lived and in-memory only: nothing here is persisted with the .gh file
    /// (the settings folder is a machine-local convenience, not part of the
    /// definition), and there are no watcher threads - the folder component rescans
    /// on solve. Registrations are dropped when the owning component is removed or
    /// the document closes.</summary>
    public static class SettingsRegistry
    {
        sealed class Entry
        {
            public Guid Owner;
            public string Folder = "";
            public SettingsFolderIndex Index = SettingsFolderIndex.Empty;
        }

        static readonly object Gate = new object();
        static readonly Dictionary<Guid, Entry> Documents = new Dictionary<Guid, Entry>();

        /// <summary>Registers (or replaces) the folder in force for one document.
        /// <paramref name="owner"/> is the InstanceGuid of the Load Settings component;
        /// it is what lets a second one recognise that the document is already taken.</summary>
        public static void Register(Guid documentId, Guid owner, string folder, SettingsFolderIndex index)
        {
            if (documentId == Guid.Empty) return;
            lock (Gate)
                Documents[documentId] = new Entry
                {
                    Owner = owner,
                    Folder = folder ?? "",
                    Index = index ?? SettingsFolderIndex.Empty,
                };
        }

        /// <summary>Drops the registration. A non-empty <paramref name="owner"/> that
        /// does not match the registered one is ignored, so a refused second component
        /// cannot unregister the first one's folder.</summary>
        public static void Unregister(Guid documentId, Guid owner = default)
        {
            lock (Gate)
            {
                if (!Documents.TryGetValue(documentId, out var entry)) return;
                if (owner != Guid.Empty && entry.Owner != Guid.Empty && entry.Owner != owner) return;
                Documents.Remove(documentId);
            }
        }

        /// <summary>InstanceGuid of the component that owns this document's folder, or
        /// <see cref="Guid.Empty"/> when no folder is registered.</summary>
        public static Guid OwnerFor(Guid documentId)
        {
            lock (Gate)
                return Documents.TryGetValue(documentId, out var entry) ? entry.Owner : Guid.Empty;
        }

        /// <summary>The registered scan, or null.</summary>
        public static SettingsFolderIndex? IndexFor(Guid documentId)
        {
            lock (Gate)
                return Documents.TryGetValue(documentId, out var entry) ? entry.Index : null;
        }

        /// <summary>The settings file in force for one component key, or null.</summary>
        public static ComponentSettings? SettingsFor(Guid documentId, string settingsKey)
        {
            lock (Gate)
                return Documents.TryGetValue(documentId, out var entry) ? entry.Index.For(settingsKey) : null;
        }

    }
}
