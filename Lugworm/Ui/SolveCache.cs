using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Lugworm.Ui
{
    /// <summary>Per-component-instance cache of the last heavy-solve result, keyed
    /// by the component's InstanceGuid so it survives recompute. Lets a Manual-mode
    /// component re-emit its previous output while inputs change, with no re-solve.</summary>
    public static class SolveCache
    {
        readonly struct Key : System.IEquatable<Key>
        {
            public readonly Guid Id; public readonly int Iteration; public readonly string Identity;
            public Key(Guid id, int iteration, string identity) { Id = id; Iteration = iteration; Identity = identity ?? ""; }
            public bool Equals(Key other) => Id.Equals(other.Id) && Iteration == other.Iteration && Identity == other.Identity;
            public override bool Equals(object? obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => Id.GetHashCode() ^ (Iteration * 397) ^ Identity.GetHashCode();
        }
        static readonly ConcurrentDictionary<Key, object> _cache = new ConcurrentDictionary<Key, object>();

        public static void Store(Guid id, object data) => Store(id, 0, "", data);
        public static void Store(Guid id, int iteration, string identity, object data)
        {
            if (data == null) { Clear(id, iteration, identity); return; }
            _cache[new Key(id, iteration, identity)] = data;
        }

        public static bool TryGet(Guid id, out object? data) => TryGet(id, 0, "", out data);
        public static bool TryGet(Guid id, int iteration, string identity, out object? data)
            => _cache.TryGetValue(new Key(id, iteration, identity), out data);

        public static void Clear(Guid id)
        {
            foreach (var pair in _cache)
                if (pair.Key.Id == id) _cache.TryRemove(pair.Key, out _);
        }
        public static void Clear(Guid id, int iteration, string identity)
            => _cache.TryRemove(new Key(id, iteration, identity), out _);
    }
}
