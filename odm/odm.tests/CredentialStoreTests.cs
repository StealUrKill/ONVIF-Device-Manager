using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using odm.ui.core;

namespace odm.tests
{
    /// <summary>
    /// Tests for CredentialStore.
    ///
    /// Strategy: CredentialStore.Instance is a singleton whose _storePath is readonly.
    /// We redirect it to a per-test temp file via reflection so tests never touch the real
    /// credentials.dat, and reset _credentials to a known state before each test.
    /// </summary>
    [TestClass]
    public class CredentialStoreTests
    {
        CredentialStore _store;
        FieldInfo _credentialsField;
        FieldInfo _storePathField;
        string _tempDir;
        string _tempStorePath;

        [TestInitialize]
        public void Setup()
        {
            _store = CredentialStore.Instance;

            // Grab private fields via reflection
            var type = typeof(CredentialStore);
            _credentialsField = type.GetField("_credentials",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _storePathField = type.GetField("_storePath",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(_credentialsField, "_credentials field not found");
            Assert.IsNotNull(_storePathField,   "_storePath field not found");

            // Redirect store to a temp path so we don't corrupt the real store
            _tempDir = Path.Combine(Path.GetTempPath(), "odm.tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _tempStorePath = Path.Combine(_tempDir, "test_credentials.dat");
            _storePathField.SetValue(_store, _tempStorePath);

            // Start each test with an empty in-memory list (no disk read needed)
            _credentialsField.SetValue(_store, new List<Account>());
            type.GetField("_bindings", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(_store, new List<CredentialStore.DeviceBinding>());
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Remove temp files
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        // ------------------------------------------------------------------
        // GetAll
        // ------------------------------------------------------------------

        [TestMethod]
        public void GetAll_EmptyStore_ReturnsEmptyNotNull()
        {
            var result = _store.GetAll();
            Assert.IsNotNull(result, "GetAll must never return null");
            Assert.AreEqual(0, result.Count);
        }

        // ------------------------------------------------------------------
        // Add / GetAll round-trip
        // ------------------------------------------------------------------

        [TestMethod]
        public void Add_ThenGetAll_ReturnsAddedCredential()
        {
            var account = new Account { Name = "user1", Password = "secret" };
            _store.Add(account);

            var all = _store.GetAll();
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("user1", all[0].Name);
            Assert.AreEqual("secret", all[0].Password);
        }

        [TestMethod]
        public void Add_AllowsDuplicates()
        {
            // Per API: Add does NOT deduplicate — that is SetAll's job.
            var account = new Account { Name = "user1", Password = "secret" };
            _store.Add(account);
            _store.Add(account);

            Assert.AreEqual(2, _store.GetAll().Count);
        }

        // ------------------------------------------------------------------
        // SetAll
        // ------------------------------------------------------------------

        [TestMethod]
        public void SetAll_ReplacesExistingCredentials()
        {
            _store.Add(new Account { Name = "old", Password = "old" });

            var newList = new List<Account>
            {
                new Account { Name = "new1", Password = "p1" },
                new Account { Name = "new2", Password = "p2" }
            };
            _store.SetAll(newList);

            var all = _store.GetAll();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual("new1", all[0].Name);
            Assert.AreEqual("new2", all[1].Name);
        }

        [TestMethod]
        public void SetAll_WithDeduplicatedList_NoDuplicatesInResult()
        {
            // Caller is responsible for deduplication before calling SetAll.
            // Verify that SetAll stores exactly what it's given.
            var acct = new Account { Name = "u", Password = "p" };
            // Deduplicate externally: pass only distinct entries
            var distinct = new List<Account> { acct };
            _store.SetAll(distinct);

            Assert.AreEqual(1, _store.GetAll().Count);
        }

        [TestMethod]
        public void SetAll_EmptyList_ClearsStore()
        {
            _store.Add(new Account { Name = "u", Password = "p" });
            _store.SetAll(new List<Account>());

            Assert.AreEqual(0, _store.GetAll().Count);
        }

        // ------------------------------------------------------------------
        // DPAPI round-trip (Save → Load)
        // ------------------------------------------------------------------

        [TestMethod]
        public void DpapiRoundTrip_SavedDataSurvivesReload()
        {
            var account = new Account { Name = "roundtrip", Password = "dpapi_test" };
            _store.Add(account);  // triggers Save to _tempStorePath

            // Simulate reload: set _credentials to empty, then call Load via reflection
            _credentialsField.SetValue(_store, new List<Account>());

            // Use the private Load() method to reload from disk
            var loadMethod = typeof(CredentialStore).GetMethod("Load",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(loadMethod, "Load() method not found");

            var reloaded = (List<Account>)loadMethod.Invoke(_store, null);
            _credentialsField.SetValue(_store, reloaded);

            var all = _store.GetAll();
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("roundtrip", all[0].Name);
            Assert.AreEqual("dpapi_test", all[0].Password);
        }

        // ------------------------------------------------------------------
        // Remove
        // ------------------------------------------------------------------

        [TestMethod]
        public void Remove_ByIndex_RemovesCorrectEntry()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            _store.Add(new Account { Name = "b", Password = "2" });
            _store.Remove(0);  // remove "a"

            var all = _store.GetAll();
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("b", all[0].Name);
        }

        // ------------------------------------------------------------------
        // Update
        // ------------------------------------------------------------------

        [TestMethod]
        public void Update_ByIndex_ReplacesEntry()
        {
            _store.Add(new Account { Name = "original", Password = "oldpwd" });
            _store.Update(0, new Account { Name = "updated", Password = "newpwd" });

            var all = _store.GetAll();
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("updated", all[0].Name);
            Assert.AreEqual("newpwd", all[0].Password);
        }

        // Recovery tests: a crash before the swap, and a store that cannot be read.

        List<Account> InvokeLoad()
        {
            // Load() can read the legacy file. Use the temp folder for it,
            // so that the tests do not read or delete the real account.def.xml.
            var legacyField = typeof(CredentialStore).GetField("_legacyPath",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(legacyField, "_legacyPath field not found");
            var originalLegacy = legacyField.GetValue(_store);
            legacyField.SetValue(_store, Path.Combine(_tempDir, "account.def.xml"));
            try
            {
                var loadMethod = typeof(CredentialStore).GetMethod("Load",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return (List<Account>)loadMethod.Invoke(_store, null);
            }
            finally
            {
                legacyField.SetValue(_store, originalLegacy);
            }
        }

        [TestMethod]
        public void Load_LeftoverTmpWithoutStore_IsRecovered()
        {
            _store.Add(new Account { Name = "survivor", Password = "pw" });
            // Make the state of a crash after the temp write and before the swap.
            File.Move(_tempStorePath, _tempStorePath + ".tmp");

            var loaded = InvokeLoad();

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual("survivor", loaded[0].Name);
            Assert.IsTrue(File.Exists(_tempStorePath), "tmp should be promoted to the store");
        }

        [TestMethod]
        public void Load_UnreadableStore_IsSetAsideNotOverwritten()
        {
            File.WriteAllBytes(_tempStorePath, new byte[] { 1, 2, 3, 4 });

            var loaded = InvokeLoad();

            Assert.AreEqual(0, loaded.Count);
            Assert.IsFalse(File.Exists(_tempStorePath), "unreadable store should be moved aside");
            Assert.AreEqual(1, Directory.GetFiles(_tempDir, "test_credentials.dat.unreadable-*").Length);
        }

        [TestMethod]
        public void Notes_SurviveReload()
        {
            _store.Add(new Account { Name = "a", Password = "1", Notes = "Cameras on the second floor" });
            _credentialsField.SetValue(_store, new List<Account>());

            var loaded = InvokeLoad();

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual("Cameras on the second floor", loaded[0].Notes);
        }

        [TestMethod]
        public void Load_StoreWithoutNotes_GivesEmptyNotes()
        {
            // A store from a version before the notes has no Notes element.
            var xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><Credentials><Items><Account>" +
                      "<Password>pw</Password><Name>old</Name></Account></Items></Credentials>";
            var cipher = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(xml), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tempStorePath, cipher);

            var loaded = InvokeLoad();

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual("old", loaded[0].Name);
            Assert.AreEqual("pw", loaded[0].Password);
            Assert.AreEqual(string.Empty, loaded[0].Notes);
        }

        [TestMethod]
        public void Equals_IgnoresNotes()
        {
            var a = new Account { Name = "a", Password = "1", Notes = "first" };
            var b = new Account { Name = "a", Password = "1", Notes = "second" };
            Assert.AreEqual(a, b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [TestMethod]
        public void Add_GivesAnId()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            Assert.IsFalse(string.IsNullOrEmpty(_store.GetAll()[0].Id));
        }

        [TestMethod]
        public void Load_StoreWithoutIds_GivesIds()
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><Credentials><Items><Account>" +
                      "<Password>pw</Password><Name>old</Name></Account></Items></Credentials>";
            File.WriteAllBytes(_tempStorePath, System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(xml), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

            var loaded = InvokeLoad();

            Assert.IsFalse(string.IsNullOrEmpty(loaded[0].Id));
        }

        [TestMethod]
        public void DeviceAccount_SurvivesReload()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            _store.Add(new Account { Name = "b", Password = "2" });
            var id = _store.GetAll()[1].Id;
            _store.SetAccountFor("10.10.10.5", id);

            _credentialsField.SetValue(_store, InvokeLoad());

            var account = _store.GetAccountFor("10.10.10.5");
            Assert.IsTrue(account.HasValue);
            Assert.AreEqual("b", account.Value.Name);
            Assert.IsFalse(_store.GetAccountFor("10.10.10.6").HasValue);
        }

        [TestMethod]
        public void DeviceAccount_EmptyId_UsesAllAccounts()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            _store.SetAccountFor("10.10.10.5", _store.GetAll()[0].Id);
            _store.SetAccountFor("10.10.10.5", null);
            Assert.IsFalse(_store.GetAccountFor("10.10.10.5").HasValue);
        }

        [TestMethod]
        public void DeviceAccount_DeletedAccount_IsRemoved()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            _store.Add(new Account { Name = "b", Password = "2" });
            var all = _store.GetAll();
            _store.SetAccountFor("10.10.10.5", all[1].Id);

            // The credential window sets the list without "b".
            _store.SetAll(new List<Account> { all[0] });

            Assert.IsFalse(_store.GetAccountFor("10.10.10.5").HasValue);
        }

        [TestMethod]
        public void DeviceAccount_EditedAccount_IsKept()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            var id = _store.GetAll()[0].Id;
            _store.SetAccountFor("10.10.10.5", id);

            // The credential window keeps the id when the user changes the password.
            _store.SetAll(new List<Account> { new Account { Id = id, Name = "a", Password = "new" } });

            Assert.AreEqual("new", _store.GetAccountFor("10.10.10.5").Value.Password);
        }

        [TestMethod]
        public void Save_ExistingStore_IsReplacedAndNoTmpLeft()
        {
            _store.Add(new Account { Name = "a", Password = "1" });
            _store.Add(new Account { Name = "b", Password = "2" });

            Assert.IsFalse(File.Exists(_tempStorePath + ".tmp"));
            _credentialsField.SetValue(_store, new List<Account>());
            Assert.AreEqual(2, InvokeLoad().Count);
        }
    }
}
