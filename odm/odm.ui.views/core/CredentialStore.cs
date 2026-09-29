using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using utils;

namespace odm.ui.core
{
    /// <summary>
    /// Encrypted persistent storage for credential pairs.
    /// Uses DPAPI (ProtectedData) with CurrentUser scope — credentials are
    /// bound to the current Windows user and machine.
    /// </summary>
    public sealed class CredentialStore
    {
        static readonly CredentialStore _instance = new CredentialStore();
        public static CredentialStore Instance { get { return _instance; } }

        readonly string _storePath = AppDefaults.ConfigFolderPath + "credentials.dat";
        readonly string _legacyPath = AppDefaults.ConfigFolderPath + "account.def.xml";

        List<Account> _credentials;

        private CredentialStore()
        {
            _credentials = Load();
        }

        /// <summary>Returns a copy of all stored credentials.</summary>
        public IList<Account> GetAll()
        {
            return _credentials.AsReadOnly();
        }

        /// <summary>Safe log summary — never includes passwords.</summary>
        public string RedactedSummary() => $"{_credentials.Count} credential(s) stored";

        public void Add(Account account)
        {
            _credentials.Add(account);
            Save();
        }

        public void Remove(int index)
        {
            _credentials.RemoveAt(index);
            Save();
        }

        public void Update(int index, Account account)
        {
            _credentials[index] = account;
            Save();
        }

        public void SetAll(List<Account> credentials)
        {
            _credentials = new List<Account>(credentials);
            Save();
        }

        // ------------------------------------------------------------------ //
        // Serialization helpers                                               //
        // ------------------------------------------------------------------ //

        [XmlRootAttribute(ElementName = "Credentials", IsNullable = false)]
        public class CredentialList
        {
            public List<Account> Items { get; set; }
            public CredentialList() { Items = new List<Account>(); }
        }

        private List<Account> Load()
        {
            string tempPath = _storePath + ".tmp";

            // A .tmp file without a store shows a crash before the swap.
            // The .tmp file is complete, so use it as the store.
            if (!File.Exists(_storePath) && File.Exists(tempPath))
            {
                try
                {
                    File.Move(tempPath, _storePath);
                }
                catch (Exception err)
                {
                    dbg.Error(err);
                }
            }

            // Try encrypted store first
            if (File.Exists(_storePath))
            {
                try
                {
                    return ReadStore(_storePath);
                }
                catch (Exception err)
                {
                    dbg.Error(err);
                    // ODM cannot read the store (damaged file or lost DPAPI key). Move it aside
                    // so that Save cannot overwrite it. Then try the migration.
                    SetAside(_storePath);
                }
            }

            // Migrate from legacy plain-XML account.def.xml
            if (File.Exists(_legacyPath))
            {
                try
                {
                    Account legacy;
                    var legacySerializer = new XmlSerializer(typeof(Account));
                    using (var sr = File.OpenText(_legacyPath))
                    {
                        legacy = (Account)legacySerializer.Deserialize(sr);
                    }

                    var migrated = new List<Account>();
                    if (!legacy.IsAnonymous)
                        migrated.Add(legacy);

                    // Persist to new encrypted store before touching the old file
                    var credList = new CredentialList { Items = migrated };

                    // Only delete old file after successful write
                    if (SaveInternal(credList))
                        File.Delete(_legacyPath);

                    return migrated;
                }
                catch (Exception err)
                {
                    dbg.Error(err);
                }
            }

            return new List<Account>();
        }

        private void Save()
        {
            var credList = new CredentialList { Items = _credentials };
            SaveInternal(credList);
        }

        private static List<Account> ReadStore(string path)
        {
            byte[] cipherBytes = File.ReadAllBytes(path);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            string xml = Encoding.UTF8.GetString(plainBytes);

            var serializer = new XmlSerializer(typeof(CredentialList));
            using (var reader = new StringReader(xml))
            {
                var list = (CredentialList)serializer.Deserialize(reader);
                return list.Items ?? new List<Account>();
            }
        }

        private static void SetAside(string path)
        {
            try
            {
                File.Move(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }
            catch (Exception err)
            {
                dbg.Error(err);
            }
        }

        /// <returns>True if the store is on the disk.</returns>
        private bool SaveInternal(CredentialList credList)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(CredentialList));
                var sb = new StringBuilder();
                using (var writer = new StringWriter(sb))
                {
                    serializer.Serialize(writer, credList);
                }

                byte[] plainBytes = Encoding.UTF8.GetBytes(sb.ToString());
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

                // Write a temp file, then replace the store with File.Replace (atomic on NTFS).
                // If a crash occurs before the swap, Load() uses the complete .tmp file.
                string tempPath = _storePath + ".tmp";
                File.WriteAllBytes(tempPath, cipherBytes);
                if (File.Exists(_storePath))
                    File.Replace(tempPath, _storePath, null);
                else
                    File.Move(tempPath, _storePath);
                return true;
            }
            catch (Exception err)
            {
                dbg.Error(err);
                return false;
            }
        }
    }
}
