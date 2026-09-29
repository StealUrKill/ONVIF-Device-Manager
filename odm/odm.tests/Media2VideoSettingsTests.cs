using System;
using System.Linq;
using System.ServiceModel;
using System.Text;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using odm.core;
using odm.ui.activities;
using onvif.services;

namespace odm.tests
{
    /// <summary>Tests for the Media2 video settings, OnvifFault and Utf8Sanitizer.
    /// The option values are the same as the values that real cameras sent.</summary>
    [TestClass]
    public class Media2VideoSettingsTests
    {
        static VideoResolution Res(int w, int h) => new VideoResolution { width = w, height = h };

        static Media2EncoderOptions Opts(VideoEncoding? enc, string name, VideoResolution[] res,
            string[] profiles, (int, int)? gov = null)
        {
            return new Media2EncoderOptions(
                enc.HasValue ? FSharpOption<VideoEncoding>.Some(enc.Value) : FSharpOption<VideoEncoding>.None,
                name,
                res,
                FSharpOption<Tuple<float, float>>.Some(Tuple.Create(1f, 6f)),
                FSharpOption<Tuple<int, int>>.Some(Tuple.Create(1536, 12544)),
                Enumerable.Range(1, 30).Select(x => (double)x).ToArray(),
                gov.HasValue ? FSharpOption<Tuple<int, int>>.Some(Tuple.Create(gov.Value.Item1, gov.Value.Item2)) : FSharpOption<Tuple<int, int>>.None,
                profiles);
        }

        static readonly Media2EncoderOptions[] CameraOptions = {
            Opts(VideoEncoding.h264, "H264", new[] { Res(3840, 2160), Res(1920, 1080) }, new[] { "High", "Main", "Baseline" }, (1, 150)),
            Opts(VideoEncoding.h265, "H265", new[] { Res(3840, 2160), Res(1920, 1080) }, new[] { "Main" }, (1, 150)),
            Opts(VideoEncoding.jpeg, "JPEG", new[] { Res(1920, 1080) }, new string[0]),
        };

        static VideoEncoderConfiguration Current(VideoEncoding enc) => new VideoEncoderConfiguration {
            token = "00000", encoding = enc, resolution = Res(3840, 2160),
            rateControl = new VideoRateControl { frameRateLimit = 30, bitrateLimit = 5120 }, quality = 3
        };

        static VideoSettingsView.Model Model(VideoEncoding enc, VideoResolution res, double fps, double kbps, int gov = 60)
        {
            var m = new VideoSettingsView.Model("prof", 30, 1, 1, 1, 6, 1, 12544, 1536, 150, 1,
                Media2VideoSettings.ToMedia1Options(CameraOptions));
            m.encoder = enc;
            m.resolution = res;
            m.frameRate = fps;
            m.bitrate = kbps;
            m.quality = 3;
            m.govLength = gov;
            m.encodingInterval = 1;
            return m;
        }

        // Options conversion

        [TestMethod]
        public void ToMedia1Options_MapsEachEncoding()
        {
            var o = Media2VideoSettings.ToMedia1Options(CameraOptions);
            Assert.AreEqual(2, o.h264.resolutionsAvailable.Length);
            Assert.AreEqual(2, o.h265.resolutionsAvailable.Length);
            Assert.AreEqual(1, o.jpeg.resolutionsAvailable.Length);
            Assert.AreEqual(1, o.h265.frameRateRange.min);
            Assert.AreEqual(30, o.h265.frameRateRange.max);
            Assert.AreEqual(150, o.h264.govLengthRange.max);
            Assert.AreEqual(1, o.qualityRange.min);
            Assert.AreEqual(6, o.qualityRange.max);
            // Media2 has no encoding interval. A fixed range of 1 to 1 disables the control.
            Assert.AreEqual(1, o.h264.encodingIntervalRange.min);
            Assert.AreEqual(1, o.h264.encodingIntervalRange.max);
        }

        [TestMethod]
        public void ToMedia1Options_SkipsUnknownEncodings()
        {
            var o = Media2VideoSettings.ToMedia1Options(new[] { Opts(null, "AV1", new[] { Res(1, 1) }, new string[0]) });
            Assert.IsNull(o.h264);
            Assert.IsNull(o.h265);
            Assert.IsNull(o.jpeg);
        }

        // Apply logic

        [TestMethod]
        public void BuildChange_SameEncoding_KeepsCameraProfile()
        {
            var change = Media2VideoSettings.BuildChange(Model(VideoEncoding.h265, Res(3840, 2160), 30, 4096), Current(VideoEncoding.h265), CameraOptions);
            Assert.IsNotNull(change);
            Assert.AreEqual(VideoEncoding.h265, change.Value.NewEncoding);
            Assert.AreEqual(4096, change.Value.NewBitrateLimit.Value);
            Assert.IsNull(change.Value.NewProfile, "profile untouched when the encoding does not change");
        }

        [TestMethod]
        public void BuildChange_SwitchEncoding_PicksSupportedProfile()
        {
            // H265 to H264: H264 supports "Main" and ODM prefers it.
            var change = Media2VideoSettings.BuildChange(Model(VideoEncoding.h264, Res(1920, 1080), 30, 4096), Current(VideoEncoding.h265), CameraOptions);
            Assert.AreEqual(VideoEncoding.h264, change.Value.NewEncoding);
            Assert.AreEqual("Main", change.Value.NewProfile.Value);
        }

        [TestMethod]
        public void BuildChange_UnsupportedResolution_ReturnsNone()
        {
            var change = Media2VideoSettings.BuildChange(Model(VideoEncoding.jpeg, Res(3840, 2160), 5, 4096), Current(VideoEncoding.h265), CameraOptions);
            Assert.IsNull(change, "JPEG does not offer 3840x2160");
        }

        [TestMethod]
        public void BuildChange_UnsupportedEncoding_ReturnsNone()
        {
            var h264Only = CameraOptions.Take(1).ToArray();
            var change = Media2VideoSettings.BuildChange(Model(VideoEncoding.h265, Res(3840, 2160), 30, 4096), Current(VideoEncoding.h264), h264Only);
            Assert.IsNull(change);
        }

        [TestMethod]
        public void BuildChange_CoercesToCameraRanges()
        {
            var change = Media2VideoSettings.BuildChange(Model(VideoEncoding.h265, Res(3840, 2160), 29.6, 99999, gov: 500), Current(VideoEncoding.h265), CameraOptions);
            Assert.AreEqual(30.0, change.Value.NewFrameRateLimit.Value, "snapped to the nearest supported frame rate");
            Assert.AreEqual(12544, change.Value.NewBitrateLimit.Value, "bitrate clamped to the advertised maximum");
            Assert.AreEqual(150, change.Value.NewGovLength.Value, "GOP clamped to the advertised maximum");
        }

        [TestMethod]
        public void BuildChange_Jpeg_SendsNoGovLength()
        {
            var change = Media2VideoSettings.BuildChange(Model(VideoEncoding.jpeg, Res(1920, 1080), 5, 4096), Current(VideoEncoding.jpeg), CameraOptions);
            Assert.IsNull(change.Value.NewGovLength);
        }

        // Fault classification

        static FaultException Fault(string reason, params string[] subcodes)
        {
            FaultCode code = null;
            for (int i = subcodes.Length - 1; i >= 0; i--)
                code = new FaultCode(subcodes[i], "http://www.onvif.org/ver10/error", code);
            return new FaultException(new FaultReason(reason), new FaultCode("Sender", code));
        }

        [TestMethod]
        public void IsNotSupported_RecognisesOnvifSubcodes()
        {
            Assert.IsTrue(OnvifFault.IsNotSupported(Fault("x", "ActionNotSupported", "NotImplemented")));
        }

        [TestMethod]
        public void IsNotSupported_RecognisesVendorReasonTexts()
        {
            // Reason texts that cameras sent.
            Assert.IsTrue(OnvifFault.IsNotSupported(Fault("This optional method is not implemented")));
            Assert.IsTrue(OnvifFault.IsNotSupported(Fault("Optional Action Not Implemented")));
            Assert.IsTrue(OnvifFault.IsNotSupported(Fault("The requested WSDL service category is not supported by the device!")));
        }

        [TestMethod]
        public void IsNotSupported_FindsInnerFault()
        {
            Assert.IsTrue(OnvifFault.IsNotSupported(new Exception("wrapper", Fault("Optional Action Not Implemented"))));
        }

        [TestMethod]
        public void IsNotSupported_OtherFaultsAndErrors_False()
        {
            Assert.IsFalse(OnvifFault.IsNotSupported(Fault("Sender not authorized", "NotAuthorized")));
            Assert.IsFalse(OnvifFault.IsNotSupported(new TimeoutException("timed out")));
            Assert.IsFalse(OnvifFault.IsNotSupported(null));
        }

        // Incorrect UTF-8

        [TestMethod]
        public void Utf8Sanitizer_ValidUtf8_Untouched()
        {
            var bytes = Encoding.UTF8.GetBytes("<tt:Name>Caméra</tt:Name>");
            Assert.IsNull(Utf8Sanitizer.sanitize(bytes, 0, bytes.Length));
        }

        [TestMethod]
        public void Utf8Sanitizer_InvalidBytes_ReplacedAndParseable()
        {
            // A Media2 reply in which the Profile attribute has random bytes.
            var prefix = Encoding.ASCII.GetBytes("<c Profile=\"4");
            var garbage = new byte[] { 0xE7, 0xC9, 0x98 };
            var suffix = Encoding.ASCII.GetBytes("\" token=\"000\"/>");
            var bytes = prefix.Concat(garbage).Concat(suffix).ToArray();

            var fixedBytes = Utf8Sanitizer.sanitize(bytes, 0, bytes.Length);
            Assert.IsNotNull(fixedBytes);
            var xml = System.Xml.Linq.XElement.Parse(Encoding.UTF8.GetString(fixedBytes.Value));
            Assert.AreEqual("000", xml.Attribute("token").Value);
            Assert.IsTrue(xml.Attribute("Profile").Value.Contains('�'));
        }

        [TestMethod]
        public void Utf8Sanitizer_AppliesOnlyToUtf8Text()
        {
            Assert.IsTrue(Utf8Sanitizer.appliesTo("application/soap+xml; charset=utf-8"));
            Assert.IsTrue(Utf8Sanitizer.appliesTo("application/soap+xml"));
            Assert.IsFalse(Utf8Sanitizer.appliesTo("multipart/related; type=\"application/xop+xml\""));
            Assert.IsFalse(Utf8Sanitizer.appliesTo("text/xml; charset=utf-16"));
        }
    }
}
