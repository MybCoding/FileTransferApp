using System;
using System.Collections.Generic;
using FileTransferApp.Security;
using Xunit;

namespace FileTransferApp.Tests
{
    public class TrustStoreModelTests
    {
        private static string Fp(byte seed) => Crypto.ToHex(Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray());

        [Fact]
        public void TrustAlways_IsTrustedAndPersisted()
        {
            var store = new TrustStoreModel();
            var fp = Fp(1);
            store.TrustAlways("dev-1", fp, "base64pub");

            Assert.True(store.IsTrusted("dev-1", fp));
            Assert.True(store.IsAlwaysTrusted("dev-1", fp));
            Assert.Equal(TrustState.TrustedAlways, store.GetState("dev-1", fp));

            var json = TrustStoreModel.Serialize(store);
            var reloaded = TrustStoreModel.Deserialize(json);
            Assert.True(reloaded.IsTrusted("dev-1", fp));
            Assert.True(reloaded.IsAlwaysTrusted("dev-1", fp));
        }

        [Fact]
        public void TrustOnce_IsTrustedInMemory_ButNotPersisted()
        {
            var store = new TrustStoreModel();
            var fp = Fp(2);
            store.TrustOnce("dev-2", fp, "base64pub");

            Assert.True(store.IsTrusted("dev-2", fp));
            Assert.False(store.IsAlwaysTrusted("dev-2", fp));

            var reloaded = TrustStoreModel.Deserialize(TrustStoreModel.Serialize(store));
            Assert.False(reloaded.IsTrusted("dev-2", fp));
        }

        [Fact]
        public void Revoke_RemovesTrust()
        {
            var store = new TrustStoreModel();
            var fp = Fp(3);
            store.TrustAlways("dev-3", fp, "pub");
            store.Revoke("dev-3");
            Assert.False(store.IsTrusted("dev-3", fp));
            Assert.Equal(TrustState.None, store.GetState("dev-3", fp));
        }

        [Fact]
        public void KeyReplacement_IsNeverAutoTrusted()
        {
            var store = new TrustStoreModel();
            var fpOld = Fp(10);
            var fpNew = Fp(20);
            store.TrustAlways("dev-4", fpOld, "pub-old");

            // The same device now presents a DIFFERENT key.
            Assert.Equal(TrustState.KeyReplaced, store.GetState("dev-4", fpNew));
            Assert.False(store.IsTrusted("dev-4", fpNew));
            Assert.False(store.IsAlwaysTrusted("dev-4", fpNew));

            // Old key still trusted until explicitly revoked.
            Assert.True(store.IsTrusted("dev-4", fpOld));
        }

        [Fact]
        public void KeyReplacement_RequiresExplicitReTrust()
        {
            var store = new TrustStoreModel();
            var fpOld = Fp(10);
            var fpNew = Fp(20);
            store.TrustAlways("dev-5", fpOld, "pub-old");

            // Explicit user action (re-pairing) overwrites with the new key.
            store.TrustAlways("dev-5", fpNew, "pub-new");
            Assert.Equal(TrustState.TrustedAlways, store.GetState("dev-5", fpNew));
            Assert.True(store.IsTrusted("dev-5", fpNew));
            Assert.Equal(TrustState.KeyReplaced, store.GetState("dev-5", fpOld));
        }

        [Fact]
        public void LegacyMigration_DeviceIdsAreKnownButNeverAutoTrusted()
        {
            var store = TrustStoreModel.CreateFromLegacy(new List<string> { "old-device-1", "old-device-2" });

            Assert.True(store.IsLegacyDevice("old-device-1"));
            Assert.True(store.IsLegacyDevice("old-device-2"));

            // No key => must re-pair; never trusted automatically.
            Assert.False(store.IsTrusted("old-device-1", Fp(99)));
            Assert.Equal(TrustState.None, store.GetState("old-device-1", Fp(99)));

            // Serialization round-trips the legacy set.
            var reloaded = TrustStoreModel.Deserialize(TrustStoreModel.Serialize(store));
            Assert.True(reloaded.IsLegacyDevice("old-device-1"));
        }

        [Fact]
        public void TrustingUpgradedDevice_RemovesFromLegacySet()
        {
            var store = TrustStoreModel.CreateFromLegacy(new List<string> { "old-device-3" });
            Assert.True(store.IsLegacyDevice("old-device-3"));

            store.TrustAlways("old-device-3", Fp(5), "pub");
            Assert.False(store.IsLegacyDevice("old-device-3"));
            Assert.True(store.IsTrusted("old-device-3", Fp(5)));
        }

        [Fact]
        public void SameFingerprint_SameDevice_CaseInsensitiveDeviceId()
        {
            var store = new TrustStoreModel();
            var fp = Fp(7);
            store.TrustAlways("Dev-X", fp, "pub");
            Assert.True(store.IsTrusted("dev-x", fp));
        }

        [Fact]
        public void FingerprintNormalization_StripsSpacesAndUppercases()
        {
            var store = new TrustStoreModel();
            var hex = Fp(0).ToLowerInvariant();
            var fpRaw = hex.Insert(8, " ").Insert(20, " ").Insert(48, " ");
            store.TrustAlways("dev-6", fpRaw, "pub");
            Assert.Equal(Fp(0), store.GetFingerprint("dev-6"));
        }

        [Fact]
        public void Deserialize_InvalidJson_ReturnsEmptyStore()
        {
            var store = TrustStoreModel.Deserialize("not json at all {{{");
            Assert.NotNull(store);
            Assert.Empty(store.Entries);
        }

        [Fact]
        public void TrustedOnce_StateIsPreservedByExplicitSnapshotButDroppedBySerialize()
        {
            var store = new TrustStoreModel();
            store.TrustOnce("dev-7", Fp(8), "pub");
            var snapshot = store.ToSnapshot();
            Assert.Contains(snapshot.Entries, e => e.State == TrustState.TrustedOnce);

            var persisted = TrustStoreModel.Serialize(store);
            Assert.DoesNotContain("TrustedOnce", persisted);
        }
    }
}
