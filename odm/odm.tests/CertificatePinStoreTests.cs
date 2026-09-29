using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using utils;

namespace odm.tests
{
    /// <summary>Tests for CertificatePinStore (trust on first use for TLS certificates).
    /// Initialize() with a temp file resets the state for each test.</summary>
    [TestClass]
    public class CertificatePinStoreTests
    {
        const SslPolicyErrors SelfSigned = SslPolicyErrors.RemoteCertificateChainErrors;

        CertificatePinStore _store;
        string _tempDir;
        string _pinPath;

        static X509Certificate2 MakeCert(string cn)
        {
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=" + cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
            }
        }

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "odm.tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _pinPath = Path.Combine(_tempDir, "trusted-certs.txt");
            _store = CertificatePinStore.Instance;
            _store.Initialize(_pinPath);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _store.Initialize(null);
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [TestMethod]
        public void Validate_FirstUse_TrustsAndPins()
        {
            Assert.IsTrue(_store.Validate("10.0.0.1", 443, MakeCert("cam"), SelfSigned));
            Assert.AreEqual(1, _store.GetPins().Count);
            Assert.AreEqual("10.0.0.1:443", _store.GetPins()[0].Endpoint);
        }

        [TestMethod]
        public void Validate_SameCertificate_Trusted()
        {
            var cert = MakeCert("cam");
            _store.Validate("10.0.0.1", 443, cert, SelfSigned);
            Assert.IsTrue(_store.Validate("10.0.0.1", 443, cert, SelfSigned));
        }

        [TestMethod]
        public void Validate_ChangedCertificate_RefusedAndReportedOnce()
        {
            _store.Validate("10.0.0.1", 443, MakeCert("original"), SelfSigned);
            int raised = 0;
            EventHandler<CertificatePinStore.CertificateChangedEventArgs> handler = (s, e) => raised++;
            _store.CertificateChanged += handler;
            try
            {
                var attacker = MakeCert("attacker");
                Assert.IsFalse(_store.Validate("10.0.0.1", 443, attacker, SelfSigned));
                Assert.IsFalse(_store.Validate("10.0.0.1", 443, attacker, SelfSigned));
                Assert.AreEqual(1, raised, "the same changed certificate should prompt only once");
                Assert.IsNotNull(_store.GetMismatch("10.0.0.1", 443));
            }
            finally
            {
                _store.CertificateChanged -= handler;
            }
        }

        [TestMethod]
        public void Validate_PinsArePerPort()
        {
            _store.Validate("10.0.0.1", 443, MakeCert("a"), SelfSigned);
            Assert.IsTrue(_store.Validate("10.0.0.1", 8443, MakeCert("b"), SelfSigned));
            Assert.AreEqual(2, _store.GetPins().Count);
        }

        [TestMethod]
        public void Validate_CaValidCertificate_AcceptedWithoutPinning()
        {
            Assert.IsTrue(_store.Validate("cam.example.com", 443, MakeCert("cam.example.com"), SslPolicyErrors.None));
            Assert.AreEqual(0, _store.GetPins().Count);
        }

        [TestMethod]
        public void Validate_NullCertificate_Refused()
        {
            Assert.IsFalse(_store.Validate("10.0.0.1", 443, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        }

        [TestMethod]
        public void AcceptChange_OnlyTrustsTheReviewedCertificate()
        {
            _store.Validate("10.0.0.1", 443, MakeCert("original"), SelfSigned);
            var replacement = MakeCert("replacement");
            _store.Validate("10.0.0.1", 443, replacement, SelfSigned);

            _store.AcceptChange("10.0.0.1:443", "AA:BB");  // This is not the fingerprint that the camera sent.
            Assert.IsFalse(_store.Validate("10.0.0.1", 443, replacement, SelfSigned));

            _store.AcceptChange("10.0.0.1:443", CertificatePinStore.Fingerprint(replacement));
            Assert.IsTrue(_store.Validate("10.0.0.1", 443, replacement, SelfSigned));
            Assert.IsNull(_store.GetMismatch("10.0.0.1", 443));
        }

        [TestMethod]
        public void Remove_NextCertificateIsTrustedOnFirstUseAgain()
        {
            _store.Validate("10.0.0.1", 443, MakeCert("original"), SelfSigned);
            _store.Remove("10.0.0.1:443");
            Assert.IsTrue(_store.Validate("10.0.0.1", 443, MakeCert("new"), SelfSigned));
        }

        [TestMethod]
        public void Pins_PersistAcrossInitialize()
        {
            var cert = MakeCert("cam");
            _store.Validate("10.0.0.1", 443, cert, SelfSigned);

            _store.Initialize(_pinPath);  // Load again from the disk.

            Assert.AreEqual(1, _store.GetPins().Count);
            Assert.IsTrue(_store.Validate("10.0.0.1", 443, cert, SelfSigned));
            Assert.IsFalse(_store.Validate("10.0.0.1", 443, MakeCert("other"), SelfSigned));
        }
    }
}
