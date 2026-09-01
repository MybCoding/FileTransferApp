using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileTransferApp.Security
{
    /// <summary>
    /// Pure cryptographic primitives used by the Stage 5 P2P transport.
    /// Only BCL crypto APIs; no external packages. This class is intentionally
    /// free of MAUI dependencies so it can be linked into the unit test project.
    /// </summary>
    public static class Crypto
    {
        public const int NonceSize = 12;          // 96-bit AES-GCM nonce
        public const int TagSize = 16;            // 128-bit GCM tag
        public const int KeySize = 32;            // AES-256 / HKDF output
        public const int SessionIdSize = 16;
        public const int FingerprintSize = 32;    // SHA-256 of SPKI public key
        public const int SasModulus = 1000000;    // 6-digit SAS

        // Domain separators (must match the protocol spec exactly).
        public const string HandshakeDomain = "FileTransferApp-Handshake-v1";
        public const string KdfDomain = "FileTransferApp-KDF-v1";
        public const string SasDomain = "FileTransferApp-SAS-v1";

        public static byte[] RandomBytes(int count)
        {
            var data = new byte[count];
            RandomNumberGenerator.Fill(data);
            return data;
        }

        public static byte[] GenerateSessionId() => RandomBytes(SessionIdSize);

        public static byte[] HashSha256(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return SHA256.HashData(data);
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts)
            {
                if (p == null) throw new ArgumentNullException(nameof(parts));
                total += p.Length;
            }
            var result = new byte[total];
            int offset = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, result, offset, p.Length);
                offset += p.Length;
            }
            return result;
        }

        public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text ?? string.Empty);

        // ============================ ECDSA (identity) ============================

        /// <summary>Generates a P-256 ECDSA key pair. Returns PKCS#8 private key and SPKI public key.</summary>
        public static (byte[] privateKey, byte[] publicKey) GenerateEcdsaKeyPair()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var priv = ecdsa.ExportPkcs8PrivateKey();
            var pub = ecdsa.ExportSubjectPublicKeyInfo();
            return (priv, pub);
        }

        public static byte[] Sign(byte[] privateKeyPkcs8, byte[] data)
        {
            if (privateKeyPkcs8 == null) throw new ArgumentNullException(nameof(privateKeyPkcs8));
            if (data == null) throw new ArgumentNullException(nameof(data));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
            return ecdsa.SignData(data, HashAlgorithmName.SHA256);
        }

        public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
        {
            if (publicKeySpki == null || data == null || signature == null) return false;
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
                return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }

        public static byte[] ComputeFingerprint(byte[] publicKeySpki)
        {
            if (publicKeySpki == null) throw new ArgumentNullException(nameof(publicKeySpki));
            return SHA256.HashData(publicKeySpki);
        }

        // ============================ ECDHE (ephemeral) ============================

        public static (byte[] privateKey, byte[] publicKey) GenerateEcdhKeyPair()
        {
            using var dh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var priv = dh.ExportPkcs8PrivateKey();
            var pub = dh.ExportSubjectPublicKeyInfo();
            return (priv, pub);
        }

        /// <summary>
        /// ECDH shared secret using P-256, hashed with SHA-256 (TLS-style) then fed to HKDF.
        /// Returns 32 bytes.
        /// </summary>
        public static byte[] EcdhDeriveSharedSecret(byte[] myPrivateKeyPkcs8, byte[] peerPublicKeySpki)
        {
            if (myPrivateKeyPkcs8 == null) throw new ArgumentNullException(nameof(myPrivateKeyPkcs8));
            if (peerPublicKeySpki == null) throw new ArgumentNullException(nameof(peerPublicKeySpki));

            using var dh = ECDiffieHellman.Create();
            dh.ImportPkcs8PrivateKey(myPrivateKeyPkcs8, out _);
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerPublicKeySpki, out _);
            return dh.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, null, null);
        }

        // ============================ HKDF-SHA256 ============================

        public static byte[] HkdfDerive(byte[] ikm, byte[]? salt, byte[] info, int length)
        {
            // RFC 5869 HKDF-SHA256 (manual implementation; Hkdf is not available on all TFM sets).
            if (ikm == null) throw new ArgumentNullException(nameof(ikm));
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

            var prk = HashHmacSha256(salt ?? new byte[KeySize], ikm);   // extract
            var okm = new byte[length];
            var t = Array.Empty<byte>();
            uint counter = 1;
            int pos = 0;
            using (var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, prk))
            {
                while (pos < length)
                {
                    hmac.AppendData(t);
                    hmac.AppendData(info);
                    hmac.AppendData(new[] { (byte)counter });
                    t = hmac.GetHashAndReset();
                    Buffer.BlockCopy(t, 0, okm, pos, Math.Min(t.Length, length - pos));
                    pos += t.Length;
                    counter++;
                }
            }
            return okm;
        }

        private static byte[] HashHmacSha256(byte[] key, byte[] data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        // ============================ AES-256-GCM ============================

        /// <summary>Encrypts plaintext; returns ciphertext (tag appended at the end).</summary>
        public static byte[] EncryptAesGcm(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("Invalid AES key length", nameof(key));
            if (nonce == null || nonce.Length != NonceSize) throw new ArgumentException("Invalid nonce length", nameof(nonce));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return Concat(ciphertext, tag);
        }

        /// <summary>Decrypts a payload produced by <see cref="EncryptAesGcm"/> (tag appended at the end).</summary>
        public static byte[] DecryptAesGcm(byte[] key, byte[] nonce, byte[] payloadWithTag, byte[] aad)
        {
            if (payloadWithTag == null || payloadWithTag.Length < TagSize)
                throw new ArgumentException("Payload too short", nameof(payloadWithTag));
            if (key == null || key.Length != KeySize) throw new ArgumentException("Invalid AES key length", nameof(key));
            if (nonce == null || nonce.Length != NonceSize) throw new ArgumentException("Invalid nonce length", nameof(nonce));

            int ctLen = payloadWithTag.Length - TagSize;
            var plaintext = new byte[ctLen];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, payloadWithTag.AsSpan(0, ctLen), payloadWithTag.AsSpan(ctLen), plaintext, aad);
            return plaintext;
        }

        // ============================ SAS / fingerprint display ============================

        /// <summary>
        /// Computes the 6-digit short authentication string from the session master key
        /// and the handshake transcript hash. Both peers derive the identical value,
        /// so a MITM produces different values and is detected by human comparison.
        /// </summary>
        public static string ComputeSas(byte[] masterKey, byte[] transcriptHash)
        {
            if (masterKey == null) throw new ArgumentNullException(nameof(masterKey));
            if (transcriptHash == null) throw new ArgumentNullException(nameof(transcriptHash));
            using var hmac = new HMACSHA256(masterKey);
            var digest = hmac.ComputeHash(Concat(Utf8(SasDomain), transcriptHash));
            uint v = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
            return (v % SasModulus).ToString("D6", CultureInfo.InvariantCulture);
        }

        public static string ToHex(byte[] data)
        {
            if (data == null) return string.Empty;
            return Convert.ToHexString(data);
        }

        /// <summary>Fingerprint formatted for human display: "A1B2 C3D4 ..." (4 hex digits per group).</summary>
        public static string FormatFingerprint(byte[] fingerprint)
        {
            if (fingerprint == null) return string.Empty;
            var hex = Convert.ToHexString(fingerprint);
            var sb = new StringBuilder(hex.Length + hex.Length / 4);
            for (int i = 0; i < hex.Length; i += 4)
            {
                if (i > 0) sb.Append(' ');
                int take = Math.Min(4, hex.Length - i);
                sb.Append(hex, i, take);
            }
            return sb.ToString();
        }

        /// <summary>Constant-time comparison used for pairing codes/SAS equality checks.</summary>
        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        public static bool ConstantTimeEquals(string? a, string? b)
        {
            if (a == null || b == null) return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            return ConstantTimeEquals(Utf8(a), Utf8(b));
        }
    }
}
