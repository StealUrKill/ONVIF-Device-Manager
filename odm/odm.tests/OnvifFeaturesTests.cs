using System;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using onvif.utils;

namespace odm.tests
{
    /// <summary>Tests for the OSD, privacy mask, video source mode and recording parsers.
    /// The XML is the same as the XML that real cameras sent.</summary>
    [TestClass]
    public class OnvifFeaturesTests
    {
        const string Ns = "xmlns:tr2=\"http://www.onvif.org/ver20/media/wsdl\" xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" " +
            "xmlns:tt=\"http://www.onvif.org/ver10/schema\" xmlns:tse=\"http://www.onvif.org/ver10/search/wsdl\"";
        static readonly XNamespace Tr2 = "http://www.onvif.org/ver20/media/wsdl";
        static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";

        static XElement Xml(string body) => XElement.Parse(body.Replace("NS", Ns));

        // A camera that sends Media1 (trt) reply elements to the Media2 GetOSDs operation.
        const string OsdsTrt =
            "<trt:GetOSDsResponse NS>" +
            "<trt:OSDs token=\"osd_token_0\"><tt:VideoSourceConfigurationToken>000</tt:VideoSourceConfigurationToken><tt:Type>Text</tt:Type>" +
            "<tt:Position><tt:Type>Custom</tt:Type><tt:Pos x=\"-1.000000\" y=\"1.000000\"/></tt:Position>" +
            "<tt:TextString><tt:Type>Plain</tt:Type><tt:FontSize>32</tt:FontSize><tt:FontColor><tt:Color X=\"16.000000\" Y=\"128.000000\" Z=\"128.000000\" " +
            "Colorspace=\"http://www.onvif.org/ver10/colorspace/YCbCr\"/></tt:FontColor><tt:PlainText></tt:PlainText>" +
            "<tt:Extension><tt:ChannelName>true</tt:ChannelName></tt:Extension></tt:TextString></trt:OSDs>" +
            "<trt:OSDs token=\"osd_token_1\"><tt:VideoSourceConfigurationToken>000</tt:VideoSourceConfigurationToken><tt:Type>Text</tt:Type>" +
            "<tt:Position><tt:Type>Custom</tt:Type><tt:Pos x=\"-1.000000\" y=\"1.000000\"/></tt:Position>" +
            "<tt:TextString><tt:Type>DateAndTime</tt:Type><tt:DateFormat>YYYY-mm-dd</tt:DateFormat><tt:TimeFormat>HH:mm:ss</tt:TimeFormat>" +
            "<tt:FontSize>32</tt:FontSize></tt:TextString></trt:OSDs>" +
            "</trt:GetOSDsResponse>";

        [TestMethod]
        public void Osd_Parse_ReadsMedia1ReplyElements()
        {
            var items = Xml(OsdsTrt).Elements().Select(Osd.parse).ToArray();
            Assert.AreEqual(2, items.Length);
            Assert.AreEqual("osd_token_0", items[0].Token);
            Assert.AreEqual("000", items[0].VideoSourceConfigurationToken);
            Assert.IsTrue(items[0].IsText);
            Assert.AreEqual("Plain", items[0].TextType);
            Assert.AreEqual("Custom", items[0].PositionType);
            Assert.AreEqual(-1.0, items[0].X);
            Assert.AreEqual(1.0, items[0].Y);
            Assert.AreEqual(32, items[0].FontSize);
            Assert.AreEqual("DateAndTime", items[1].TextType);
            Assert.AreEqual("YYYY-mm-dd", items[1].DateFormat);
            Assert.AreEqual("HH:mm:ss", items[1].TimeFormat);
        }

        [TestMethod]
        public void Osd_Build_KeepsCameraValuesThatOdmDoesNotShow()
        {
            var item = Osd.parse(Xml(OsdsTrt).Elements().First());
            item.PlainText = "Gate";
            var osd = Osd.build(item);
            Assert.AreEqual(Tr2 + "OSD", osd.Name);
            Assert.AreEqual("osd_token_0", (string)osd.Attribute("token"));
            var text = osd.Element(Tt + "TextString");
            Assert.AreEqual("Gate", (string)text.Element(Tt + "PlainText"));
            Assert.IsNotNull(text.Element(Tt + "FontColor"), "the font color of the camera must stay");
            Assert.AreEqual("true", (string)text.Element(Tt + "Extension").Element(Tt + "ChannelName"));
            // The schema order: Type, DateFormat, TimeFormat, FontSize, FontColor, BackgroundColor, PlainText, Extension.
            CollectionAssert.AreEqual(new[] { "Type", "FontSize", "FontColor", "PlainText", "Extension" },
                text.Elements().Select(e => e.Name.LocalName).ToArray());
        }

        [TestMethod]
        public void Osd_Build_NewDateAndTimeItem()
        {
            var item = new OsdItem {
                VideoSourceConfigurationToken = "cfg", TextType = "DateAndTime", DateFormat = "yyyy-MM-dd",
                TimeFormat = "HH:mm:ss", PositionType = "UpperLeft", PlainText = "ignored" };
            Assert.IsTrue(item.IsNew);
            var osd = Osd.build(item);
            Assert.AreEqual("", (string)osd.Attribute("token"));
            Assert.IsNull(osd.Element(Tt + "Position").Element(Tt + "Pos"), "a preset position has no coordinates");
            var text = osd.Element(Tt + "TextString");
            Assert.AreEqual("yyyy-MM-dd", (string)text.Element(Tt + "DateFormat"));
            Assert.AreEqual("HH:mm:ss", (string)text.Element(Tt + "TimeFormat"));
            Assert.IsNull(text.Element(Tt + "PlainText"), "only plain text items send PlainText");
        }

        [TestMethod]
        public void Osd_ParseOptions()
        {
            var opts = Osd.parseOptions(Xml(
                "<tr2:GetOSDOptionsResponse NS><tr2:OSDOptions><tt:MaximumNumberOfOSDs DateAndTime=\"1\" Time=\"1\" Date=\"1\" PlainText=\"7\" Total=\"8\"/>" +
                "<tt:Type>Text</tt:Type><tt:PositionOption>Custom</tt:PositionOption><tt:PositionOption>UpperLeft</tt:PositionOption>" +
                "<tt:TextOption><tt:Type>Plain</tt:Type><tt:Type>DateAndTime</tt:Type><tt:FontSizeRange><tt:Min>6</tt:Min><tt:Max>42</tt:Max></tt:FontSizeRange>" +
                "<tt:DateFormat>yyyy-MM-dd</tt:DateFormat><tt:DateFormat>MM/dd/yyyy</tt:DateFormat><tt:TimeFormat>HH:mm:ss</tt:TimeFormat></tt:TextOption>" +
                "</tr2:OSDOptions></tr2:GetOSDOptionsResponse>"));
            Assert.AreEqual(8, opts.MaxTotal);
            Assert.AreEqual(7, opts.MaxPlainText);
            CollectionAssert.AreEqual(new[] { "Custom", "UpperLeft" }, opts.PositionOptions);
            CollectionAssert.AreEqual(new[] { "Plain", "DateAndTime" }, opts.TextTypes);
            CollectionAssert.AreEqual(new[] { "yyyy-MM-dd", "MM/dd/yyyy" }, opts.DateFormats);
            Assert.AreEqual(6, opts.FontSizeMin);
            Assert.AreEqual(42, opts.FontSizeMax);
        }

        const string MasksTr2 =
            "<tr2:GetMasksResponse NS><tr2:Masks token=\"00000\"><tr2:ConfigurationToken>00000</tr2:ConfigurationToken><tr2:Polygon>" +
            "<tt:Point x=\"-0.853515625\" y=\"0.853515625\"/><tt:Point x=\"-0.560546875\" y=\"0.853515625\"/>" +
            "<tt:Point x=\"-0.560546875\" y=\"0.560546875\"/><tt:Point x=\"-0.853515625\" y=\"0.560546875\"/></tr2:Polygon>" +
            "<tr2:Type>Pixelated</tr2:Type><tr2:Enabled>true</tr2:Enabled></tr2:Masks></tr2:GetMasksResponse>";

        [TestMethod]
        public void Masks_Parse()
        {
            var m = Masks.parse(Xml(MasksTr2).Elements().First());
            Assert.AreEqual("00000", m.Token);
            Assert.AreEqual("00000", m.ConfigurationToken);
            Assert.AreEqual("Pixelated", m.MaskType);
            Assert.IsTrue(m.Enabled);
            Assert.AreEqual(4, m.Points.Length);
            Assert.AreEqual(-0.853515625, m.Points[0].X, 1e-9);
            Assert.AreEqual(0.560546875, m.Points[2].Y, 1e-9);
        }

        [TestMethod]
        public void Masks_ParseOptions_WithColorRange()
        {
            var opts = Masks.parseOptions(Xml(
                "<tr2:GetMaskOptionsResponse NS><tr2:Options RectangleOnly=\"false\" SingleColorOnly=\"true\"><tr2:MaxMasks>8</tr2:MaxMasks>" +
                "<tr2:MaxPoints>4</tr2:MaxPoints><tr2:Types>Color</tr2:Types><tr2:Types>Pixelated</tr2:Types><tr2:Color><tt:ColorspaceRange>" +
                "<tt:X><tt:Min>0.000000</tt:Min><tt:Max>0.000000</tt:Max></tt:X><tt:Y><tt:Min>0.000000</tt:Min><tt:Max>0.000000</tt:Max></tt:Y>" +
                "<tt:Z><tt:Min>0.000000</tt:Min><tt:Max>0.000000</tt:Max></tt:Z><Colorspace>http://www.onvif.org/ver10/colorspace/RGB</Colorspace>" +
                "</tt:ColorspaceRange></tr2:Color></tr2:Options></tr2:GetMaskOptionsResponse>"));
            Assert.AreEqual(8, opts.MaxMasks);
            Assert.AreEqual(4, opts.MaxPoints);
            Assert.IsFalse(opts.RectangleOnly);
            Assert.IsTrue(opts.SingleColorOnly);
            CollectionAssert.AreEqual(new[] { "Color", "Pixelated" }, opts.Types);
            Assert.IsNotNull(opts.DefaultColor);
            Assert.AreEqual("http://www.onvif.org/ver10/colorspace/RGB", (string)opts.DefaultColor.Attribute("Colorspace"));
        }

        [TestMethod]
        public void Masks_ParseOptions_Nil()
        {
            var opts = Masks.parseOptions(Xml(
                "<tr2:GetMaskOptionsResponse NS><tr2:Options xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/></tr2:GetMaskOptionsResponse>"));
            Assert.IsFalse(opts.MaxMasks.HasValue);
            Assert.AreEqual(0, opts.Types.Length);
            Assert.IsNull(opts.DefaultColor);
        }

        [TestMethod]
        public void Masks_Build_NewColorMask_UsesMedia2NamespaceAndDefaultColor()
        {
            var opts = new MaskOptions {
                DefaultColor = new XElement(Tr2 + "Color", new XAttribute("X", 0), new XAttribute("Y", 0), new XAttribute("Z", 0)) };
            var m = new MaskItem { ConfigurationToken = "cfg", MaskType = "Color", Points = Masks.rectangle(-0.5, 0.5, 0.5, -0.5) };
            var x = Masks.build(m, opts);
            Assert.AreEqual(Tr2 + "Mask", x.Name);
            CollectionAssert.AreEqual(new[] { "ConfigurationToken", "Polygon", "Type", "Color", "Enabled" },
                x.Elements().Select(e => e.Name.LocalName).ToArray());
            Assert.IsTrue(x.Elements().All(e => e.Name.Namespace == Tr2), "the Mask elements are in the tr2 namespace");
            var points = x.Element(Tr2 + "Polygon").Elements(Tt + "Point").ToArray();
            Assert.AreEqual(4, points.Length);
            Assert.AreEqual("-0.5", (string)points[0].Attribute("x"));
            Assert.AreEqual("0.5", (string)points[0].Attribute("y"));
        }

        [TestMethod]
        public void Masks_Build_PixelatedMask_HasNoColor()
        {
            var m = Masks.parse(Xml(MasksTr2).Elements().First());
            var x = Masks.build(m, null);
            Assert.IsNull(x.Element(Tr2 + "Color"));
            Assert.AreEqual("Pixelated", (string)x.Element(Tr2 + "Type"));
        }

        [TestMethod]
        public void VideoSourceModes_Parse()
        {
            var modes = Xml(
                "<tr2:GetVideoSourceModesResponse NS><tr2:VideoSourceModes Enabled=\"true\" token=\"0\"><tr2:MaxFramerate>30</tr2:MaxFramerate>" +
                "<tr2:MaxResolution><tt:Width>2688</tt:Width><tt:Height>1520</tt:Height></tr2:MaxResolution><tr2:Encodings>H264 JPEG H265</tr2:Encodings>" +
                "<tr2:Reboot>false</tr2:Reboot></tr2:VideoSourceModes>" +
                "<tr2:VideoSourceModes token=\"PAL\" Enabled=\"false\"><tr2:MaxFramerate>25</tr2:MaxFramerate><tr2:MaxResolution><tt:Width>3840</tt:Width>" +
                "<tt:Height>2160</tt:Height></tr2:MaxResolution><tr2:Encodings>H264 H265 </tr2:Encodings><tr2:Reboot>true</tr2:Reboot>" +
                "<tr2:Description>VideoSource Mode</tr2:Description></tr2:VideoSourceModes></tr2:GetVideoSourceModesResponse>")
                .Elements().Select(VideoSourceModes.parse).ToArray();
            Assert.AreEqual(2, modes.Length);
            Assert.IsTrue(modes[0].Enabled);
            Assert.AreEqual(2688, modes[0].MaxWidth);
            Assert.AreEqual(1520, modes[0].MaxHeight);
            Assert.AreEqual(30.0, modes[0].MaxFramerate);
            Assert.IsFalse(modes[0].Reboot);
            Assert.AreEqual("0: 2688x1520 30 fps", modes[0].ToString());
            Assert.IsTrue(modes[1].Reboot);
            Assert.AreEqual("PAL: 3840x2160 25 fps - VideoSource Mode", modes[1].ToString());
        }

        [TestMethod]
        public void Recordings_ParseSummary()
        {
            var sum = Recordings.parseSummary(Xml(
                "<tse:GetRecordingSummaryResponse NS><tse:Summary><tt:DataFrom>2026-09-15T21:31:14Z</tt:DataFrom>" +
                "<tt:DataUntil>2026-09-30T08:13:57Z</tt:DataUntil><tt:NumberRecordings>1</tt:NumberRecordings></tse:Summary></tse:GetRecordingSummaryResponse>"));
            Assert.AreEqual(1, sum.NumberRecordings);
            Assert.AreEqual(new DateTime(2026, 9, 15, 21, 31, 14, DateTimeKind.Utc), sum.DataFrom.Value);
            Assert.AreEqual(DateTimeKind.Utc, sum.DataUntil.Value.Kind);
        }

        [TestMethod]
        public void Recordings_ParseInformation()
        {
            var r = Recordings.parseInformation(Xml(
                "<tse:RecordingInformation NS><tt:RecordingToken>tokenRecording1</tt:RecordingToken>" +
                "<tt:Source><tt:SourceId>SourceId_1</tt:SourceId><tt:Name>IpCamera_1</tt:Name><tt:Location>Location</tt:Location>" +
                "<tt:Description>videoSource</tt:Description><tt:Address>http://www.onvif.org/ver10/schema/Profile</tt:Address></tt:Source>" +
                "<tt:EarliestRecording>2026-09-15T21:31:14Z</tt:EarliestRecording><tt:LatestRecording>2026-09-30T08:13:57Z</tt:LatestRecording>" +
                "<tt:Content>recordingContent</tt:Content>" +
                "<tt:Track><tt:TrackToken>VIDEO001</tt:TrackToken><tt:TrackType>Video</tt:TrackType><tt:Description>videoTrack</tt:Description>" +
                "<tt:DataFrom>2026-09-15T21:31:14Z</tt:DataFrom><tt:DataTo>2026-09-30T08:13:57Z</tt:DataTo></tt:Track>" +
                "<tt:RecordingStatus>Recording</tt:RecordingStatus></tse:RecordingInformation>"));
            Assert.AreEqual("tokenRecording1", r.Token);
            Assert.AreEqual("IpCamera_1", r.SourceName);
            Assert.AreEqual("Recording", r.Status);
            Assert.AreEqual(new DateTime(2026, 9, 30, 8, 13, 57, DateTimeKind.Utc), r.LatestRecording.Value);
            Assert.AreEqual(1, r.Tracks.Length);
            Assert.AreEqual("Video", r.Tracks[0].TrackType);
            Assert.AreEqual("VIDEO001", r.Tracks[0].Token);
        }
    }
}
