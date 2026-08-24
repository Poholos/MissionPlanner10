using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using MissionPlanner.Mavlink;
using MissionPlanner.Utilities;

namespace MissionPlanner
{
    /// <summary>
    /// Durable encrypted storage for MAVLink signing keys. The encryption material is persisted
    /// separately so changing NIC enumeration cannot make the key store unreadable. Existing files
    /// are migrated by trying every currently available legacy MAC-derived key.
    /// </summary>
    internal sealed class MavAuthKeyStore : IDisposable
    {
        private static readonly byte[] MaterialMagic = Encoding.ASCII.GetBytes("MPAK1");
        private const int KeyLength = 32;
        private const int IvLength = 16;

        private readonly object _sync = new object();
        private readonly string _keyFile;
        private readonly string _materialFile;
        private readonly Func<IReadOnlyList<Crypto>> _legacyCandidates;
        private Crypto _crypto;
        private bool _loaded;

        public MavAuthKeyStore(string keyFile, string materialFile)
            : this(keyFile, materialFile, Crypto.CreateLegacyCandidates)
        {
        }

        internal MavAuthKeyStore(string keyFile, string materialFile,
            Func<IReadOnlyList<Crypto>> legacyCandidates)
        {
            _keyFile = keyFile ?? throw new ArgumentNullException(nameof(keyFile));
            _materialFile = materialFile ?? throw new ArgumentNullException(nameof(materialFile));
            _legacyCandidates = legacyCandidates ??
                                throw new ArgumentNullException(nameof(legacyCandidates));
        }

        public MAVAuthKeys.AuthKeys Load()
        {
            lock (_sync)
            {
                DisposeCrypto();
                _loaded = false;

                if (!File.Exists(_keyFile))
                {
                    _crypto = File.Exists(_materialFile)
                        ? ReadMaterial()
                        : CreateAndPersistMaterial();
                    _loaded = true;
                    return new MAVAuthKeys.AuthKeys();
                }

                Exception materialError = null;
                if (File.Exists(_materialFile))
                {
                    Crypto persisted = null;
                    try
                    {
                        persisted = ReadMaterial();
                        MAVAuthKeys.AuthKeys loaded = Deserialize(persisted);
                        _crypto = persisted;
                        persisted = null;
                        _loaded = true;
                        return loaded;
                    }
                    catch (Exception ex)
                    {
                        materialError = ex;
                    }
                    finally
                    {
                        persisted?.Dispose();
                    }
                }

                IReadOnlyList<Crypto> candidates = _legacyCandidates();
                Exception legacyError = null;
                for (int index = 0; index < candidates.Count; index++)
                {
                    Crypto candidate = candidates[index];
                    try
                    {
                        MAVAuthKeys.AuthKeys loaded = Deserialize(candidate);
                        _crypto = candidate;
                        TryPersistMaterial(candidate);
                        for (int remaining = index + 1; remaining < candidates.Count; remaining++)
                            candidates[remaining]?.Dispose();
                        candidate = null;
                        _loaded = true;
                        return loaded;
                    }
                    catch (Exception ex)
                    {
                        legacyError = ex;
                    }
                    finally
                    {
                        candidate?.Dispose();
                    }
                }

                throw new InvalidDataException(
                    "The existing MAVLink signing-key file could not be decrypted. It was left " +
                    "unchanged; reconnect the network adapter used when it was created or restore " +
                    "the matching authkeys.key file.", legacyError ?? materialError);
            }
        }

        public void Save(MAVAuthKeys.AuthKeys keys)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));

            lock (_sync)
            {
                if (!_loaded || _crypto == null)
                    throw new InvalidOperationException(
                        "Signing keys were not loaded successfully; the existing file will not be overwritten.");

                string directory = Path.GetDirectoryName(_keyFile);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string temporary = _keyFile + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    var serializer = CreateSerializer();
                    using (var file = new FileStream(
                               temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var encrypted = new CryptoStream(
                               file, _crypto.algorithm.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        serializer.WriteObject(encrypted, keys);
                    }

                    ReplaceAtomically(temporary, _keyFile);
                }
                finally
                {
                    TryDelete(temporary);
                }
            }
        }

        private MAVAuthKeys.AuthKeys Deserialize(Crypto crypto)
        {
            var serializer = CreateSerializer();
            using (var file = new FileStream(
                       _keyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var decrypted = new CryptoStream(
                       file, crypto.algorithm.CreateDecryptor(), CryptoStreamMode.Read))
            {
                var keys = serializer.ReadObject(decrypted) as MAVAuthKeys.AuthKeys;
                if (keys == null)
                    throw new SerializationException("The signing-key file contained no key collection.");
                return keys;
            }
        }

        private Crypto CreateAndPersistMaterial()
        {
            var key = new byte[KeyLength];
            var iv = new byte[IvLength];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(key);
                random.GetBytes(iv);
            }

            try
            {
                var crypto = new Crypto(key, iv);
                PersistMaterial(crypto);
                return crypto;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
                Array.Clear(iv, 0, iv.Length);
            }
        }

        private Crypto ReadMaterial()
        {
            byte[] contents = File.ReadAllBytes(_materialFile);
            int expectedLength = MaterialMagic.Length + KeyLength + IvLength;
            if (contents.Length != expectedLength)
                throw new InvalidDataException("The MAVLink signing-key material file has an invalid length.");

            for (int index = 0; index < MaterialMagic.Length; index++)
            {
                if (contents[index] != MaterialMagic[index])
                    throw new InvalidDataException("The MAVLink signing-key material file has an invalid header.");
            }

            var key = new byte[KeyLength];
            var iv = new byte[IvLength];
            Buffer.BlockCopy(contents, MaterialMagic.Length, key, 0, key.Length);
            Buffer.BlockCopy(contents, MaterialMagic.Length + key.Length, iv, 0, iv.Length);
            return new Crypto(key, iv);
        }

        private void TryPersistMaterial(Crypto crypto)
        {
            try
            {
                PersistMaterial(crypto);
            }
            catch
            {
                // The already-readable legacy file remains usable and unchanged. A later start can
                // retry migration; failing the whole load here would unnecessarily hide valid keys.
            }
        }

        private void PersistMaterial(Crypto crypto)
        {
            byte[] key;
            byte[] iv;
            crypto.ExtractBinaryKeys(out key, out iv);

            var contents = new byte[MaterialMagic.Length + key.Length + iv.Length];
            Buffer.BlockCopy(MaterialMagic, 0, contents, 0, MaterialMagic.Length);
            Buffer.BlockCopy(key, 0, contents, MaterialMagic.Length, key.Length);
            Buffer.BlockCopy(iv, 0, contents, MaterialMagic.Length + key.Length, iv.Length);

            string directory = Path.GetDirectoryName(_materialFile);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporary = _materialFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(
                           temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(contents, 0, contents.Length);
                    file.Flush();
                }

                ReplaceAtomically(temporary, _materialFile);
            }
            finally
            {
                TryDelete(temporary);
                Array.Clear(key, 0, key.Length);
                Array.Clear(iv, 0, iv.Length);
                Array.Clear(contents, 0, contents.Length);
            }
        }

        private static DataContractSerializer CreateSerializer()
        {
            return new DataContractSerializer(typeof(MAVAuthKeys.AuthKeys),
                new[] { typeof(MAVAuthKeys.AuthKey) });
        }

        private static void ReplaceAtomically(string temporary, string destination)
        {
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destination + ".bak");
                return;
            }

            File.Move(temporary, destination);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private void DisposeCrypto()
        {
            _crypto?.Dispose();
            _crypto = null;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                DisposeCrypto();
                _loaded = false;
            }
        }
    }
}
