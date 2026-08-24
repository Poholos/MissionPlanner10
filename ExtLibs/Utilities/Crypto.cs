using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace MissionPlanner.Utilities
{
    public sealed class Crypto : IDisposable
    {
        private static readonly byte[] DefaultKey =
        {
            0xd1, 0x3c, 0x35, 0x6f, 0xb5, 0xd, 0x87, 0xf0,
            0x92, 0x07, 0x6d, 0xab, 0x76, 0x82, 0x36, 0xa,
            0x13, 0x5a, 0x77, 0xfe, 0x77, 0xf3, 0x7f, 0xa8,
            0xa4, 0x04, 0x11, 0x46, 0x68, 0x2d, 0x48, 0xa1
        };

        private static readonly byte[] DefaultIV =
        {
            0x6d, 0x2d, 0xf5, 0x34, 0xc7, 0x60, 0xc5, 0x33,
            0xe2, 0xa3, 0xd7, 0xc3, 0xf3, 0x39, 0xf2, 0x16
        };

        /// <summary>
        /// Abstract object
        /// </summary>
        public SymmetricAlgorithm algorithm;

        /// <summary>
        /// Default constructor
        /// </summary>
        public Crypto()
            : this(CreateLegacyMaterial(FirstPhysicalAddress()))
        {
        }

        private Crypto(KeyMaterial material)
            : this(material.Key, material.IV)
        {
        }

        internal Crypto(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 32)
                throw new ArgumentException("A 256-bit key is required.", nameof(key));
            if (iv == null || iv.Length != 16)
                throw new ArgumentException("A 128-bit IV is required.", nameof(iv));

            this.algorithm = new RijndaelManaged();
            this.algorithm.Mode = CipherMode.CBC;
            this.algorithm.Padding = PaddingMode.PKCS7;
            this.algorithm.Key = (byte[]) key.Clone();
            this.algorithm.IV = (byte[]) iv.Clone();
        }

        internal static IReadOnlyList<Crypto> CreateLegacyCandidates()
        {
            var candidates = new List<Crypto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    byte[] address = nic.GetPhysicalAddress()?.GetAddressBytes();
                    AddLegacyCandidate(candidates, seen, address);
                }
            }
            catch
            {
                // The historical implementation silently used the built-in material when network
                // adapter enumeration failed, so retain that as a migration candidate.
            }

            AddLegacyCandidate(candidates, seen, null);
            return candidates;
        }

        private static void AddLegacyCandidate(
            ICollection<Crypto> candidates, ISet<string> seen, byte[] address)
        {
            KeyMaterial material = CreateLegacyMaterial(address);
            string identity = Convert.ToBase64String(material.Key) + ":" +
                              Convert.ToBase64String(material.IV);
            if (seen.Add(identity))
                candidates.Add(new Crypto(material));
        }

        private static byte[] FirstPhysicalAddress()
        {
            try
            {
                PhysicalAddress address = NetworkInterface.GetAllNetworkInterfaces()
                    .Select(nic => nic.GetPhysicalAddress()).FirstOrDefault();
                return address?.GetAddressBytes();
            }
            catch
            {
                return null;
            }
        }

        private static KeyMaterial CreateLegacyMaterial(byte[] address)
        {
            byte[] key = (byte[]) DefaultKey.Clone();
            byte[] iv = (byte[]) DefaultIV.Clone();
            if (address != null)
            {
                Array.Copy(address, 0, key, 0, Math.Min(address.Length, key.Length));
                Array.Copy(address, 0, iv, 0, Math.Min(address.Length, iv.Length));
            }

            return new KeyMaterial(key, iv);
        }

        private sealed class KeyMaterial
        {
            public KeyMaterial(byte[] key, byte[] iv)
            {
                Key = key;
                IV = iv;
            }

            public byte[] Key { get; }
            public byte[] IV { get; }
        }

        /// <summary>
        /// Release all resources used by the SymmetricAlgorithm class
        /// </summary>
        public void Dispose()
        {
            this.algorithm.Clear();
        }

        /// <summary>
        /// Set Binary Keys
        /// </summary>
        public void SetBinaryKeys(byte[] Key, byte[] IV)
        {
            this.algorithm.Key = Key;
            this.algorithm.IV = IV;
        }

        /// <summary>
        /// Extract Binary Keys
        /// </summary>
        public void ExtractBinaryKeys(out byte[] Key, out byte[] IV)
        {
            Key = this.algorithm.Key;
            IV = this.algorithm.IV;
        }

        /// <summary>
        /// Process the data with CryptoStream
        /// </summary>
        byte[] Process(byte[] data, int startIndex, int count, ICryptoTransform cryptor)
        {
            //
            // the memory stream granularity must match the block size
            // of the current cryptographic operation
            //
            int capacity = count;
            int mod = count%algorithm.BlockSize;
            if (mod > 0)
            {
                capacity += (algorithm.BlockSize - mod);
            }

            MemoryStream memoryStream = new MemoryStream(capacity);

            CryptoStream cryptoStream = new CryptoStream(
                memoryStream,
                cryptor,
                CryptoStreamMode.Write);

            cryptoStream.Write(data, startIndex, count);
            cryptoStream.FlushFinalBlock();

            cryptoStream.Close();
            cryptoStream = null;

            cryptor.Dispose();
            cryptor = null;

            return memoryStream.ToArray();
        }

        /// <summary>
        ///  Byte array encryption function
        /// </summary>
        /// <param name="cleanBuffer">input byte array</param>
        /// <returns>output encrypted byte array</returns>
        public byte[] EncryptBuffer(byte[] cleanBuffer)
        {
            byte[] output;

            // Encryptor object
            ICryptoTransform cryptoTransform = this.algorithm.CreateEncryptor();

            // Get the result
            output = this.Process(cleanBuffer, 0, cleanBuffer.Length, cryptoTransform);

            //clean
            cryptoTransform.Dispose();

            return output;
        }

        /// <summary>
        ///  Byte array decryption function
        /// </summary>
        /// <param name="cryptoBuffer">input chiper byte array</param>
        /// <returns>output decrypted byte array</returns>
        public byte[] DecryptBuffer(byte[] cryptoBuffer)
        {
            byte[] output;

            // Decryptor object
            ICryptoTransform cryptoTransform = this.algorithm.CreateDecryptor();

            // Get the result   
            output = this.Process(cryptoBuffer, 0, cryptoBuffer.Length, cryptoTransform);

            //clean
            cryptoTransform.Dispose();

            return output;
        }

        /// <summary>
        /// String encryption function
        /// </summary>
        /// <param name="plainText">clean text</param>
        /// <returns>base64 encrypted string</returns>
        public string EncryptString(string plainText)
        {
            return Convert.ToBase64String(EncryptBuffer(Encoding.UTF8.GetBytes(plainText)));
        }

        /// <summary>
        /// String decryption function
        /// </summary>
        /// <param name="encyptedText">base64 encrypted string</param>
        /// <returns>decrypted text</returns>
        public string DecryptString(string encyptedText)
        {
            return Encoding.UTF8.GetString(DecryptBuffer(Convert.FromBase64String(encyptedText)));
        }
    }
}
