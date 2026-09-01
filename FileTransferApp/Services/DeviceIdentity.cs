using FileTransferApp.Security;
using Microsoft.Maui.Storage;
using System;
using System.Security.Cryptography;

namespace FileTransferApp.Services
{
    /// <summary>
    /// Long-term device identity: a persistent P-256 ECDSA key pair.
    /// The public key (SPKI) is the identity; its SHA-256 is the fingerprint
    /// used as the trust anchor. DeviceId stays a human label only.
    ///
    /// SECURITY NOTE: the private key is stored in Preferences (app-private but not
    /// hardware-backed). Moving it to Android Keystore/secure storage is deferred to
    /// a later stage and recorded in the Stage 5 report as a known limitation.
    /// </summary>
    public sealed class DeviceIdentity
    {
        private const string PrivateKeyPref = "IdentityPrivateKeyV1";

        private static readonly object _lock = new();
        private static DeviceIdentity? _instance;

        private byte[]? _privateKeyPkcs8;
        private byte[]? _publicKeySpki;
        private string? _fingerprintHex;
        private string? _deviceId;

        public static DeviceIdentity Current
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new DeviceIdentity();
                        _instance.EnsureLoaded();
                    }
                }
                return _instance;
            }
        }

        public string DeviceId => _deviceId ??= Preferences.Get("DeviceId", string.Empty) ?? string.Empty;
        public byte[] PublicKeySpki => _publicKeySpki ?? throw new InvalidOperationException("Identity not loaded");
        public byte[] PrivateKeyPkcs8 => _privateKeyPkcs8 ?? throw new InvalidOperationException("Identity not loaded");
        public byte[] Fingerprint => Crypto.ComputeFingerprint(PublicKeySpki);
        public string FingerprintHex => _fingerprintHex ??= Crypto.ToHex(Fingerprint);
        public bool IsLoaded => _privateKeyPkcs8 != null;

        public void EnsureReady() => EnsureLoaded();

        private void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_privateKeyPkcs8 != null) return;

                EnsureDeviceId();

                try
                {
                    var b64 = Preferences.Get(PrivateKeyPref, string.Empty);
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        var key = Convert.FromBase64String(b64);
                        using var ecdsa = ECDsa.Create();
                        ecdsa.ImportPkcs8PrivateKey(key, out _);
                        _privateKeyPkcs8 = key;
                        _publicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();
                    }
                }
                catch
                {
                    _privateKeyPkcs8 = null;
                }

                if (_privateKeyPkcs8 == null)
                {
                    var (priv, pub) = Crypto.GenerateEcdsaKeyPair();
                    _privateKeyPkcs8 = priv;
                    _publicKeySpki = pub;
                    try
                    {
                        Preferences.Set(PrivateKeyPref, Convert.ToBase64String(priv));
                    }
                    catch { /* stored on next successful write; identity still usable this session */ }
                }
            }
        }

        private static void EnsureDeviceId()
        {
            try
            {
                var id = Preferences.Get("DeviceId", string.Empty);
                if (string.IsNullOrWhiteSpace(id))
                {
                    Preferences.Set("DeviceId", Guid.NewGuid().ToString("N"));
                }
            }
            catch { }
        }
    }
}
