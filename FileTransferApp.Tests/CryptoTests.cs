using System;
using System.Linq;
using FileTransferApp.Security;
using Xunit;

namespace FileTransferApp.Tests
{
    public class CryptoTests
    {
        [Fact]
        public void EcdsaKeyGen_ProducesNonEmptyPair()
        {
            var (priv, pub) = Crypto.GenerateEcdsaKeyPair();
            Assert.NotNull(priv);
            Assert.NotNull(pub);
            Assert.True(priv.Length > 0 && pub.Length > 0);
        }

        [Fact]
        public void SignVerify_RoundTrip_Succeeds()
        {
            var (priv, pub) = Crypto.GenerateEcdsaKeyPair();
            var data = Crypto.Utf8("test data");
            var sig = Crypto.Sign(priv, data);
            Assert.True(Crypto.Verify(pub, data, sig));
        }

        [Fact]
        public void Verify_RejectsTamperedData()
        {
            var (priv, pub) = Crypto.GenerateEcdsaKeyPair();
            var data = Crypto.Utf8("test data");
            var sig = Crypto.Sign(priv, data);
            var tampered = (byte[])data.Clone();
            tampered[0] ^= 0xFF;
            Assert.False(Crypto.Verify(pub, tampered, sig));
        }

        [Fact]
        public void Verify_RejectsSignatureFromDifferentKey()
        {
            var (_, pubA) = Crypto.GenerateEcdsaKeyPair();
            var (privB, _) = Crypto.GenerateEcdsaKeyPair();
            var data = Crypto.Utf8("data");
            var sig = Crypto.Sign(privB, data);
            Assert.False(Crypto.Verify(pubA, data, sig));
        }

        [Fact]
        public void Fingerprint_IsStableAndSha256Length()
        {
            var (_, pub) = Crypto.GenerateEcdsaKeyPair();
            var fp1 = Crypto.ComputeFingerprint(pub);
            var fp2 = Crypto.ComputeFingerprint(pub);
            Assert.Equal(fp1, fp2);
            Assert.Equal(Crypto.FingerprintSize, fp1.Length);
        }

        [Fact]
        public void Fingerprint_DiffersAcrossKeys()
        {
            var (_, pub1) = Crypto.GenerateEcdsaKeyPair();
            var (_, pub2) = Crypto.GenerateEcdsaKeyPair();
            Assert.NotEqual(Crypto.ComputeFingerprint(pub1), Crypto.ComputeFingerprint(pub2));
        }

        [Fact]
        public void Ecdh_BothPartiesDeriveSameSecret()
        {
            var (apriv, apub) = Crypto.GenerateEcdhKeyPair();
            var (bpriv, bpub) = Crypto.GenerateEcdhKeyPair();
            var s1 = Crypto.EcdhDeriveSharedSecret(apriv, bpub);
            var s2 = Crypto.EcdhDeriveSharedSecret(bpriv, apub);
            Assert.Equal(s1, s2);
            Assert.Equal(Crypto.KeySize, s1.Length);
        }

        [Fact]
        public void Ecdh_ThirdPartyDerivesDifferentSecret()
        {
            var (apriv, apub) = Crypto.GenerateEcdhKeyPair();
            var (bpriv, bpub) = Crypto.GenerateEcdhKeyPair();
            var (cpriv, _) = Crypto.GenerateEcdhKeyPair();
            var sAB = Crypto.EcdhDeriveSharedSecret(apriv, bpub);
            var sCB = Crypto.EcdhDeriveSharedSecret(cpriv, bpub);
            Assert.NotEqual(sAB, sCB);
        }

        [Fact]
        public void Hkdf_DeterministicAndCorrectLength()
        {
            var ikm = Crypto.RandomBytes(32);
            var info = Crypto.Utf8("info");
            var a = Crypto.HkdfDerive(ikm, null, info, 32);
            var b = Crypto.HkdfDerive(ikm, null, info, 32);
            Assert.Equal(a, b);
            Assert.Equal(32, a.Length);
        }

        [Fact]
        public void Hkdf_DiffersAcrossInfo()
        {
            var ikm = Crypto.RandomBytes(32);
            var a = Crypto.HkdfDerive(ikm, null, Crypto.Utf8("info-a"), 32);
            var b = Crypto.HkdfDerive(ikm, null, Crypto.Utf8("info-b"), 32);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void AesGcm_RoundTrip()
        {
            var key = Crypto.RandomBytes(32);
            var nonce = Crypto.RandomBytes(Crypto.NonceSize);
            var plain = Crypto.Utf8("hello secure world");
            var aad = Crypto.Utf8("aad");
            var ct = Crypto.EncryptAesGcm(key, nonce, plain, aad);
            Assert.Equal(plain.Length + Crypto.TagSize, ct.Length);
            var dec = Crypto.DecryptAesGcm(key, nonce, ct, aad);
            Assert.Equal(plain, dec);
        }

        [Fact]
        public void AesGcm_TamperedCiphertext_Throws()
        {
            var key = Crypto.RandomBytes(32);
            var nonce = Crypto.RandomBytes(Crypto.NonceSize);
            var plain = Crypto.Utf8("hello secure world");
            var aad = Crypto.Utf8("aad");
            var ct = Crypto.EncryptAesGcm(key, nonce, plain, aad);
            ct[0] ^= 0xFF;
            Assert.ThrowsAny<Exception>(() => Crypto.DecryptAesGcm(key, nonce, ct, aad));
        }

        [Fact]
        public void AesGcm_WrongKey_Throws()
        {
            var key1 = Crypto.RandomBytes(32);
            var key2 = Crypto.RandomBytes(32);
            var nonce = Crypto.RandomBytes(Crypto.NonceSize);
            var aad = Crypto.Utf8("aad");
            var ct = Crypto.EncryptAesGcm(key1, nonce, Crypto.Utf8("data"), aad);
            Assert.ThrowsAny<Exception>(() => Crypto.DecryptAesGcm(key2, nonce, ct, aad));
        }

        [Fact]
        public void AesGcm_WrongAad_Throws()
        {
            var key = Crypto.RandomBytes(32);
            var nonce = Crypto.RandomBytes(Crypto.NonceSize);
            var ct = Crypto.EncryptAesGcm(key, nonce, Crypto.Utf8("data"), Crypto.Utf8("aad1"));
            Assert.ThrowsAny<Exception>(() => Crypto.DecryptAesGcm(key, nonce, ct, Crypto.Utf8("aad2")));
        }

        [Fact]
        public void Sas_IsSixDigitsAndStable()
        {
            var master = Crypto.RandomBytes(32);
            var th = Crypto.RandomBytes(32);
            var s1 = Crypto.ComputeSas(master, th);
            var s2 = Crypto.ComputeSas(master, th);
            Assert.Equal(s1, s2);
            Assert.Matches(@"^\d{6}$", s1);
        }

        [Fact]
        public void Sas_ChangesWithMasterKey()
        {
            var m1 = Crypto.RandomBytes(32);
            var m2 = Crypto.RandomBytes(32);
            var th = Crypto.RandomBytes(32);
            Assert.NotEqual(Crypto.ComputeSas(m1, th), Crypto.ComputeSas(m2, th));
        }

        [Fact]
        public void Sas_ChangesWithTranscript()
        {
            var master = Crypto.RandomBytes(32);
            var t1 = Crypto.RandomBytes(32);
            var t2 = Crypto.RandomBytes(32);
            Assert.NotEqual(Crypto.ComputeSas(master, t1), Crypto.ComputeSas(master, t2));
        }

        [Fact]
        public void RandomBytes_AreRandom()
        {
            var a = Crypto.RandomBytes(16);
            var b = Crypto.RandomBytes(16);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void ConstantTimeEquals_ComparesCorrectly()
        {
            Assert.True(Crypto.ConstantTimeEquals(Crypto.Utf8("abc"), Crypto.Utf8("abc")));
            Assert.False(Crypto.ConstantTimeEquals(Crypto.Utf8("abc"), Crypto.Utf8("abd")));
            Assert.False(Crypto.ConstantTimeEquals("abc", (string?)null));
            Assert.True(Crypto.ConstantTimeEquals((string?)null, (string?)null));
        }

        [Fact]
        public void FormatFingerprint_GroupsHex()
        {
            var fp = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var s = Crypto.FormatFingerprint(fp);
            Assert.Contains(' ', s);
            Assert.Equal(79, s.Length); // 64 hex chars + 15 separators
        }

        [Fact]
        public void SessionId_IsSixteenBytes()
        {
            Assert.Equal(16, Crypto.GenerateSessionId().Length);
        }
    }
}
