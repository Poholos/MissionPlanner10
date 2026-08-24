using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.Mavlink
{
    public class MAVAuthKeys
    {
        private static readonly ILog log =
    LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object Sync = new object();
        private static readonly string KeyFile =
            Path.Combine(Settings.GetUserDataDirectory(), "authkeys.xml");
        private static readonly string MaterialFile =
            Path.Combine(Settings.GetUserDataDirectory(), "authkeys.key");
        private static readonly MavAuthKeyStore Store = new MavAuthKeyStore(KeyFile, MaterialFile);

        public static AuthKeys Keys = new AuthKeys();

        public static Exception LoadFailure { get; private set; }

        public static bool IsAvailable => LoadFailure == null;

        //https://msdn.microsoft.com/en-us/library/aa347850(v=vs.110).aspx

        [CollectionDataContract(ItemName = "AuthKeys", Namespace = "")]
        public class AuthKeys : Dictionary<string, AuthKey>
        {
        }

        [DataContract(Name = "AuthKey", Namespace = "")]
        public struct AuthKey
        {
            [DataMember()]
            public string Name;
            [DataMember()]
            public byte[] Key;
        }

        static MAVAuthKeys()
        {
            Load();
        }

        public static void AddKey(string name, string seed)
        {
            lock (Sync)
            {
                EnsureAvailable();
                // sha the user input string
                using (SHA256CryptoServiceProvider signit = new SHA256CryptoServiceProvider())
                {
                    var shauser = signit.ComputeHash(Encoding.UTF8.GetBytes(seed));
                    Array.Resize(ref shauser, 32);

                    Keys[name] = new AuthKey() {Key = shauser, Name = name};
                }
            }
        }

        public static void Save()
        {
            lock (Sync)
            {
                EnsureAvailable();
                Store.Save(Keys);
            }
        }

        internal static void Load()
        {
            lock (Sync)
            {
                try
                {
                    Keys = Store.Load();
                    LoadFailure = null;
                }
                catch (Exception ex)
                {
                    // Never replace an unreadable file with an empty collection. Save/Add are
                    // disabled until a later process can decrypt it or the user restores its key.
                    Keys = new AuthKeys();
                    LoadFailure = ex;
                    log.Error("MAVLink signing keys could not be loaded; preserving the existing file.", ex);
                }
            }
        }

        private static void EnsureAvailable()
        {
            if (LoadFailure != null)
                throw new InvalidOperationException(
                    "The existing MAVLink signing-key file could not be loaded and was left unchanged.",
                    LoadFailure);
        }
    }
}
