using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using odm.ui.core;
using onvif.utils;

namespace odm.tests
{
    /// <summary>Tests for the parts of the recording download that do not need a camera,
    /// and for the clips that ODM makes from the recording history events.</summary>
    [TestClass]
    public class ReplayDownloaderTests
    {
        // The SDP of a camera that uses a session URL with a query for ONVIF replay.
        const string ReplaySdp =
            "v=0\r\no=- 1790760258648377 1 IN IP4 10.0.0.1\r\ns=Recording\r\nt=0 0\r\n" +
            "a=control:rtsp://10.0.0.1:554/Recording?replaymode=onvifreplay\r\n" +
            "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1;profile-level-id=640033;sprop-parameter-sets=Z2QAM6wVFKA=,aO48sA==\r\n" +
            "a=control:track1\r\n" +
            "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=control:track2\r\n";

        [TestMethod]
        public void Sdp_ParsesVideoTrackAndParameterSets()
        {
            var sdp = SdpVideo.Parse(ReplaySdp);
            Assert.AreEqual("H264", sdp.Codec);
            Assert.AreEqual(96, sdp.PayloadType);
            Assert.AreEqual("track1", sdp.Control);
            Assert.AreEqual("rtsp://10.0.0.1:554/Recording?replaymode=onvifreplay", sdp.SessionControl);
            Assert.AreEqual(2, sdp.ParameterSets.Count);
            Assert.AreEqual(7, sdp.ParameterSets[0][0] & 0x1F, "the first parameter set is the SPS");
            Assert.AreEqual(8, sdp.ParameterSets[1][0] & 0x1F, "the second parameter set is the PPS");
        }

        [TestMethod]
        public void Sdp_H265Track()
        {
            var sdp = SdpVideo.Parse("v=0\r\nm=video 0 RTP/AVP 98\r\na=rtpmap:98 H265/90000\r\n" +
                "a=fmtp:98 sprop-vps=QAEMAf//;sprop-sps=QgEB;sprop-pps=RAHA\r\na=control:trackID=1\r\n");
            Assert.AreEqual("H265", sdp.Codec);
            Assert.AreEqual(98, sdp.PayloadType);
            Assert.AreEqual(3, sdp.ParameterSets.Count);
            Assert.IsNull(sdp.SessionControl);
        }

        [TestMethod]
        public void Resolve_IgnoresTheQueryOfTheBase()
        {
            Assert.AreEqual("rtsp://h/Recording/track1", SdpVideo.Resolve("rtsp://h/Recording?replaymode=onvifreplay", "track1"));
            Assert.AreEqual("rtsp://h/Recording/track1", SdpVideo.Resolve("rtsp://h/Recording/", "track1"));
            Assert.AreEqual("rtsp://other/x", SdpVideo.Resolve("rtsp://h/Recording/", "rtsp://other/x"));
            Assert.AreEqual("rtsp://h/Recording/", SdpVideo.Resolve("rtsp://h/Recording/", "*"));
        }

        [TestMethod]
        public void H264_SingleStapAndFragments()
        {
            var d = new RtpDepacketizer(false);
            // A single NAL unit (SPS).
            d.Add(new byte[] { 0x67, 1, 2 }, 0, 3);
            // STAP-A with a PPS and an SEI.
            d.Add(new byte[] { 0x18, 0, 2, 0x68, 9, 0, 2, 0x06, 7 }, 0, 9);
            // FU-A: an IDR slice in three fragments (start, middle, end).
            d.Add(new byte[] { 0x7C, 0x85, 0xA0 }, 0, 3);
            d.Add(new byte[] { 0x7C, 0x05, 0xA1 }, 0, 3);
            d.Add(new byte[] { 0x7C, 0x45, 0xA2 }, 0, 3);
            var nals = d.TakeNals();
            Assert.AreEqual(4, nals.Count);
            CollectionAssert.AreEqual(new byte[] { 0x67, 1, 2 }, nals[0]);
            CollectionAssert.AreEqual(new byte[] { 0x68, 9 }, nals[1]);
            CollectionAssert.AreEqual(new byte[] { 0x06, 7 }, nals[2]);
            CollectionAssert.AreEqual(new byte[] { 0x65, 0xA0, 0xA1, 0xA2 }, nals[3], "the NAL header comes from the FU indicator and header");
            Assert.IsTrue(d.IsKey(nals[3]));
            Assert.IsTrue(d.IsParameterSet(nals[0]));
            Assert.IsFalse(d.HasData);
        }

        [TestMethod]
        public void H264_FragmentWithoutStartIsDropped()
        {
            var d = new RtpDepacketizer(false);
            d.Add(new byte[] { 0x7C, 0x05, 0xA1 }, 0, 3);
            d.Add(new byte[] { 0x7C, 0x45, 0xA2 }, 0, 3);
            Assert.IsFalse(d.HasData, "a lost first fragment makes the NAL unit unusable");
        }

        [TestMethod]
        public void H265_Fragments()
        {
            var d = new RtpDepacketizer(true);
            // FU (type 49) of an IDR_W_RADL (type 19): payload header 0x62 0x01, FU header S+type.
            d.Add(new byte[] { 0x62, 0x01, 0x93, 0xB0 }, 0, 4);
            d.Add(new byte[] { 0x62, 0x01, 0x53, 0xB1 }, 0, 4);
            var nals = d.TakeNals();
            Assert.AreEqual(1, nals.Count);
            CollectionAssert.AreEqual(new byte[] { 0x26, 0x01, 0xB0, 0xB1 }, nals[0]);
            Assert.IsTrue(d.IsKey(nals[0]));
        }

        [TestMethod]
        public void AnnexB_AddsStartCodes()
        {
            var data = RtpDepacketizer.AnnexB(new[] { new byte[] { 0x67 }, new byte[] { 0x68, 1 } });
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x68, 1 }, data);
        }

        [TestMethod]
        public void Digest_MatchesRfc2617Example()
        {
            // RFC 2617 section 3.5 without qop gives the same HA1 and HA2 steps.
            var r = ReplayDownloader.DigestResponse("Mufasa", "Circle Of Life", "testrealm@host.com",
                "dcd98b7102dd2f0e8b11d0f600bfb0c093", "GET", "/dir/index.html");
            Assert.AreEqual("670fd8c2df070c60b045671b8b24ff02", r);
        }

        static RecordingEvent Ev(int second, string kind, bool on, string track = "")
        {
            return new RecordingEvent(new DateTime(2026, 9, 15, 21, 31, second, DateTimeKind.Utc), kind, track, on);
        }

        [TestMethod]
        public void Clips_FromRecordingState()
        {
            var events = new[] {
                Ev(10, "Recording", true), Ev(10, "Track", true, "VIDEO001"),
                Ev(20, "Recording", false), Ev(20, "Track", false, "VIDEO001"),
                Ev(30, "Recording", true),
            };
            var until = new DateTime(2026, 9, 15, 21, 31, 50, DateTimeKind.Utc);
            var clips = Recordings.buildClips(events, "VIDEO001", until);
            Assert.AreEqual(2, clips.Length);
            Assert.AreEqual(TimeSpan.FromSeconds(10), clips[0].Duration);
            Assert.IsFalse(clips[0].IsOpen);
            Assert.AreEqual(until, clips[1].End, "a clip without an end continues to the end of the search");
            Assert.IsTrue(clips[1].IsOpen);
        }

        [TestMethod]
        public void Clips_FromTrackStateWhenNoRecordingState()
        {
            var events = new[] {
                Ev(10, "Track", true, "AUDIO001"), Ev(12, "Track", true, "VIDEO001"),
                Ev(15, "Track", false, "AUDIO001"), Ev(18, "Track", false, "VIDEO001"),
            };
            var clips = Recordings.buildClips(events, "VIDEO001", DateTime.UtcNow);
            Assert.AreEqual(1, clips.Length);
            Assert.AreEqual(TimeSpan.FromSeconds(6), clips[0].Duration, "the clip uses the video track");
        }
    }
}
