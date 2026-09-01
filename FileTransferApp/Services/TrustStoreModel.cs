using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FileTransferApp.Security
{
    public enum TrustState
    {
        None = 0,
        TrustedOnce = 1,    // session-only (not persisted as always-trust)
        TrustedAlways = 2,  // persisted
        KeyReplaced = 3     // a DIFFERENT key was seen for this device; never auto-trusted
    }

    public sealed class TrustEntry
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;      // uppercase hex, SHA-256(SPKI)
        public string PublicKeyBase64 { get; set; } = string.Empty;
        public TrustState State { get; set; } = TrustState.None;
        public long LastSeenUtcTicks { get; set; }
    }

    public sealed class TrustStoreSnapshot
    {
        public List<TrustEntry> Entries { get; set; } = new();
        public List<string> LegacyDeviceIds { get; set; } = new();
    }

    /// <summary>
    /// Pure, keyed trust store (fingerprint of the long-term ECDSA public key is the
    /// trust anchor; DeviceId is only a human label). No MAUI dependencies so it can
    /// be unit tested directly.
    /// </summary>
    public sealed class TrustStoreModel
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, TrustEntry> _byDeviceId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TrustEntry> _byFingerprint = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _legacyDeviceIds = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<TrustEntry> Entries => _byFingerprint.Values.ToList();
        public IReadOnlyCollection<string> LegacyDeviceIds => _legacyDeviceIds;

        public static string NormalizeFingerprint(string? fingerprint) =>
            string.IsNullOrWhiteSpace(fingerprint) ? string.Empty : fingerprint.Trim().Replace(" ", string.Empty).ToUpperInvariant();

        public TrustEntry? FindByDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            return _byDeviceId.TryGetValue(deviceId, out var e) ? e : null;
        }

        public TrustEntry? FindByFingerprint(string? fingerprint)
        {
            var fp = NormalizeFingerprint(fingerprint);
            if (string.IsNullOrEmpty(fp)) return null;
            return _byFingerprint.TryGetValue(fp, out var e) ? e : null;
        }

        public bool HasEntry(string? deviceId) => FindByDeviceId(deviceId) != null;

        public string? GetFingerprint(string? deviceId) => FindByDeviceId(deviceId)?.Fingerprint;

        public bool IsLegacyDevice(string? deviceId) =>
            !string.IsNullOrWhiteSpace(deviceId) && _legacyDeviceIds.Contains(deviceId);

        public bool IsTrusted(string? deviceId, string? fingerprint)
        {
            var state = GetState(deviceId, fingerprint);
            return state == TrustState.TrustedAlways || state == TrustState.TrustedOnce;
        }

        public bool IsAlwaysTrusted(string? deviceId, string? fingerprint)
            => GetState(deviceId, fingerprint) == TrustState.TrustedAlways;

        /// <summary>
        /// Returns the trust state for (deviceId, fingerprint). If the device has an
        /// entry with a DIFFERENT fingerprint it returns <see cref="TrustState.KeyReplaced"/>
        /// (never auto-trusted) regardless of the stored state.
        /// </summary>
        public TrustState GetState(string? deviceId, string? fingerprint)
        {
            var fp = NormalizeFingerprint(fingerprint);
            var deviceIdOk = !string.IsNullOrWhiteSpace(deviceId);
            var fpOk = !string.IsNullOrEmpty(fp);

            // Device is known: its recorded key is the trust anchor. A different
            // presented key is an explicit key replacement and is never auto-trusted.
            if (deviceIdOk)
            {
                var key = deviceId!;
                if (_byDeviceId.TryGetValue(key, out var current))
                {
                    if (fpOk)
                        return string.Equals(current.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)
                            ? current.State
                            : TrustState.KeyReplaced;
                    return current.State;
                }
            }

            // Unknown device label: fall back to pure fingerprint trust, but refuse a
            // label that claims a key already bound to a different device (spoofing).
            var byFp = fpOk ? FindByFingerprint(fp) : null;
            if (byFp != null)
            {
                if (deviceIdOk && !string.Equals(byFp.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    return TrustState.None;
                return byFp.State;
            }

            return TrustState.None;
        }

        /// <summary>Explicit user action: permanently trust (deviceId, fingerprint, public key).</summary>
        public void TrustAlways(string? deviceId, string? fingerprint, string publicKeyBase64)
        {
            var fp = NormalizeFingerprint(fingerprint);
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrEmpty(fp)) return;
            if (_byDeviceId.TryGetValue(deviceId, out var old) &&
                !string.Equals(old.Fingerprint, fp, StringComparison.OrdinalIgnoreCase))
                _byFingerprint.Remove(old.Fingerprint);   // key replacement: drop the stale key
            var now = DateTime.UtcNow.Ticks;
            var entry = _byFingerprint.TryGetValue(fp, out var e) ? e : new TrustEntry();
            entry.DeviceId = deviceId;
            entry.Fingerprint = fp;
            entry.PublicKeyBase64 = publicKeyBase64 ?? string.Empty;
            entry.State = TrustState.TrustedAlways;
            entry.LastSeenUtcTicks = now;
            _byFingerprint[fp] = entry;
            _byDeviceId[deviceId] = entry;
            _legacyDeviceIds.Remove(deviceId);
        }

        /// <summary>Explicit user action: trust (deviceId, fingerprint) for this session only.</summary>
        public void TrustOnce(string? deviceId, string? fingerprint, string publicKeyBase64)
        {
            var fp = NormalizeFingerprint(fingerprint);
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrEmpty(fp)) return;
            if (_byDeviceId.TryGetValue(deviceId, out var old) &&
                !string.Equals(old.Fingerprint, fp, StringComparison.OrdinalIgnoreCase))
                _byFingerprint.Remove(old.Fingerprint);   // key replacement: drop the stale key
            var now = DateTime.UtcNow.Ticks;
            var entry = _byFingerprint.TryGetValue(fp, out var e) ? e : new TrustEntry();
            entry.DeviceId = deviceId;
            entry.Fingerprint = fp;
            entry.PublicKeyBase64 = publicKeyBase64 ?? string.Empty;
            entry.State = TrustState.TrustedOnce;
            entry.LastSeenUtcTicks = now;
            _byFingerprint[fp] = entry;
            _byDeviceId[deviceId] = entry;
            _legacyDeviceIds.Remove(deviceId);
        }

        public void Revoke(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            if (_byDeviceId.TryGetValue(deviceId, out var entry))
            {
                _byFingerprint.Remove(entry.Fingerprint);
                _byDeviceId.Remove(deviceId);
            }
            _legacyDeviceIds.Remove(deviceId);
        }

        public void RevokeByFingerprint(string? fingerprint)
        {
            var fp = NormalizeFingerprint(fingerprint);
            if (string.IsNullOrEmpty(fp)) return;
            if (_byFingerprint.TryGetValue(fp, out var entry))
            {
                _byFingerprint.Remove(fp);
                if (entry != null) _byDeviceId.Remove(entry.DeviceId);
            }
        }

        public TrustStoreSnapshot ToSnapshot() => new()
        {
            Entries = _byFingerprint.Values.OrderBy(e => e.DeviceId, StringComparer.OrdinalIgnoreCase).ToList(),
            LegacyDeviceIds = _legacyDeviceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };

        /// <summary>Adds legacy (pre-key) device ids during migration. Idempotent.</summary>
        public void ImportLegacyDeviceIds(IEnumerable<string>? legacyDeviceIds)
        {
            if (legacyDeviceIds == null) return;
            foreach (var id in legacyDeviceIds)
            {
                if (!string.IsNullOrWhiteSpace(id)) _legacyDeviceIds.Add(id);
            }
        }

        public static TrustStoreModel FromSnapshot(TrustStoreSnapshot? snapshot)
        {
            var store = new TrustStoreModel();
            if (snapshot == null) return store;
            foreach (var e in snapshot.Entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Fingerprint)) continue;
                var fp = NormalizeFingerprint(e.Fingerprint);
                if (string.IsNullOrEmpty(fp)) continue;
                e.Fingerprint = fp;
                if (e.State != TrustState.TrustedAlways && e.State != TrustState.TrustedOnce)
                    e.State = TrustState.None;
                store._byFingerprint[fp] = e;
                if (!string.IsNullOrWhiteSpace(e.DeviceId)) store._byDeviceId[e.DeviceId] = e;
            }
            foreach (var id in snapshot.LegacyDeviceIds)
            {
                if (!string.IsNullOrWhiteSpace(id)) store._legacyDeviceIds.Add(id);
            }
            return store;
        }

        public static string Serialize(TrustStoreModel store)
        {
            var snap = (store ?? new TrustStoreModel()).ToSnapshot();
            // Session-only (TrustedOnce) trust must never survive a restart.
            snap.Entries = snap.Entries.Where(e => e.State == TrustState.TrustedAlways).ToList();
            return JsonSerializer.Serialize(snap, JsonOpts);
        }

        public static TrustStoreModel Deserialize(string json)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<TrustStoreSnapshot>(json, JsonOpts);
                return FromSnapshot(snapshot);
            }
            catch
            {
                return new TrustStoreModel();
            }
        }

        /// <summary>Migration entry point: pre-Stage-5 deviceId-only trust list (no keys).</summary>
        public static TrustStoreModel CreateFromLegacy(IEnumerable<string>? legacyDeviceIds)
        {
            var store = new TrustStoreModel();
            if (legacyDeviceIds == null) return store;
            foreach (var id in legacyDeviceIds)
            {
                if (!string.IsNullOrWhiteSpace(id)) store._legacyDeviceIds.Add(id);
            }
            return store;
        }
    }
}
