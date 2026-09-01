using FileTransferApp.Security;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FileTransferApp.Services
{
    /// <summary>
    /// Trust management for Stage 5. The trust anchor is the fingerprint
    /// (SHA-256 of the peer's ECDSA public key), NOT the deviceId. DeviceId
    /// remains as a human label and for legacy/AllowLegacy deviceId-only trust.
    ///
    /// Legacy single-argument methods are kept for compile compatibility and
    /// for the explicit AllowLegacy plaintext path; they never grant trust to
    /// secured peers (which always require a fingerprint match).
    /// </summary>
    public static class TrustService
    {
        private const string StorePrefKey = "TrustStoreJsonV1";
        private const string LegacyPrefKey = "TrustedDevicesJson";

        private static readonly object _lock = new();
        private static TrustStoreModel _store;
        private static readonly HashSet<string> _legacySessionTrusted = new(StringComparer.OrdinalIgnoreCase);

        static TrustService()
        {
            _store = LoadOrMigrate();
        }

        // ============================ KEYED API (Stage 5) ============================

        public static bool IsTrusted(string? deviceId, string? fingerprint)
        {
            lock (_lock) { return _store.IsTrusted(deviceId, fingerprint); }
        }

        public static bool IsAlwaysTrusted(string? deviceId, string? fingerprint)
        {
            lock (_lock) { return _store.IsAlwaysTrusted(deviceId, fingerprint); }
        }

        public static TrustState GetState(string? deviceId, string? fingerprint)
        {
            lock (_lock) { return _store.GetState(deviceId, fingerprint); }
        }

        public static string? GetFingerprint(string? deviceId)
        {
            lock (_lock) { return _store.GetFingerprint(deviceId); }
        }

        public static bool IsLegacyDevice(string? deviceId)
        {
            lock (_lock) { return _store.IsLegacyDevice(deviceId); }
        }

        /// <summary>Persistent trust: explicit user action on a verified fingerprint.</summary>
        public static void TrustAlways(string? deviceId, string? fingerprint, byte[]? publicKeySpki)
        {
            if (publicKeySpki == null) return;
            lock (_lock)
            {
                _store.TrustAlways(deviceId, fingerprint, Convert.ToBase64String(publicKeySpki));
                Save();
            }
        }

        /// <summary>Session-only trust: explicit user action on a verified fingerprint.</summary>
        public static void TrustOnce(string? deviceId, string? fingerprint, byte[]? publicKeySpki)
        {
            if (publicKeySpki == null) return;
            lock (_lock) { _store.TrustOnce(deviceId, fingerprint, Convert.ToBase64String(publicKeySpki)); }
        }

        // ============================ LEGACY API (compat / AllowLegacy) ============================

        public static bool IsTrusted(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            lock (_lock)
            {
                if (_legacySessionTrusted.Contains(deviceId)) return true;
                if (_store.IsLegacyDevice(deviceId)) return true;
                var entry = _store.FindByDeviceId(deviceId);
                return entry != null && (entry.State == TrustState.TrustedAlways || entry.State == TrustState.TrustedOnce);
            }
        }

        public static bool IsAlwaysTrusted(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            lock (_lock)
            {
                if (_store.IsLegacyDevice(deviceId)) return true;
                var entry = _store.FindByDeviceId(deviceId);
                return entry != null && entry.State == TrustState.TrustedAlways;
            }
        }

        public static bool IsSessionTrusted(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            lock (_lock)
            {
                if (_legacySessionTrusted.Contains(deviceId)) return true;
                var entry = _store.FindByDeviceId(deviceId);
                return entry != null && entry.State == TrustState.TrustedOnce;
            }
        }

        /// <summary>Legacy deviceId-only session trust (AllowLegacy plaintext path only).</summary>
        public static void TrustOnce(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_lock) { _legacySessionTrusted.Add(deviceId); }
        }

        /// <summary>Legacy deviceId-only persistent trust (AllowLegacy plaintext path only).</summary>
        public static void TrustAlways(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_lock)
            {
                _store.ImportLegacyDeviceIds(new[] { deviceId });
                _legacySessionTrusted.Add(deviceId);
                Save();
            }
        }

        public static void Revoke(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_lock)
            {
                _store.Revoke(deviceId);
                _legacySessionTrusted.Remove(deviceId);
                Save();
            }
        }

        public static void RevokeByFingerprint(string? fingerprint)
        {
            lock (_lock)
            {
                _store.RevokeByFingerprint(fingerprint);
                Save();
            }
        }

        // ============================ STORAGE ============================

        private static TrustStoreModel LoadOrMigrate()
        {
            try
            {
                var json = Preferences.Get(StorePrefKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var store = TrustStoreModel.Deserialize(json);
                    // Migrate any remaining legacy device ids into the new store (idempotent).
                    var legacyIds = ReadLegacyDeviceIds();
                    if (legacyIds.Count > 0)
                    {
                        var before = store.LegacyDeviceIds.Count;
                        store.ImportLegacyDeviceIds(legacyIds);
                        if (store.LegacyDeviceIds.Count != before) Save(store);
                    }
                    return store;
                }
            }
            catch { }

            var ids = ReadLegacyDeviceIds();
            var migrated = TrustStoreModel.CreateFromLegacy(ids);
            if (ids.Count > 0) Save(migrated);
            return migrated;
        }

        private static List<string> ReadLegacyDeviceIds()
        {
            try
            {
                var json = Preferences.Get(LegacyPrefKey, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return new List<string>();
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        private static void Save() => Save(_store);

        private static void Save(TrustStoreModel store)
        {
            try
            {
                Preferences.Set(StorePrefKey, TrustStoreModel.Serialize(store));
            }
            catch { /* persist on next successful write */ }
        }
    }
}
