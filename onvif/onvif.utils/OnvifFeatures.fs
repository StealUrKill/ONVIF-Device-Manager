namespace onvif.utils
    open System
    open System.Globalization
    open System.Xml.Linq

    open onvif.services
    open odm.core
    open global.utils

    /// The ONVIF namespaces that the features below use.
    module OnvifNs =
        [<Literal>]
        let Media2 = "http://www.onvif.org/ver20/media/wsdl"
        [<Literal>]
        let Schema = "http://www.onvif.org/ver10/schema"
        [<Literal>]
        let Search = "http://www.onvif.org/ver10/search/wsdl"
        [<Literal>]
        let Recording = "http://www.onvif.org/ver10/recording/wsdl"
        [<Literal>]
        let Replay = "http://www.onvif.org/ver10/replay/wsdl"

    /// Small LINQ to XML helpers. They accept null, so a missing element gives a default value.
    module internal Xml =
        let tr2 = XNamespace.Get(OnvifNs.Media2)
        let tt = XNamespace.Get(OnvifNs.Schema)
        let tse = XNamespace.Get(OnvifNs.Search)
        let inv = CultureInfo.InvariantCulture

        let isNil (x:obj) = Object.ReferenceEquals(x, null)
        // Cameras do not agree on the namespaces of the reply elements (tt, tr2, trt or none).
        // So the helpers match the local name only.
        let children (e:XElement) (name:string) : XElement[] =
            if isNil e then [||] else e.Elements() |> Seq.filter (fun c -> c.Name.LocalName = name) |> Seq.toArray
        let child (e:XElement) (name:string) : XElement =
            if isNil e then null else
                match e.Elements() |> Seq.tryFind (fun c -> c.Name.LocalName = name) with
                | Some c -> c
                | None -> null
        let text (e:XElement) = if isNil e then "" else e.Value.Trim()
        let attr (e:XElement) (name:string) =
            if isNil e then "" else
                let a = e.Attribute(XName.Get(name))
                if isNil a then "" else a.Value.Trim()
        let toInt (s:string) =
            let mutable v = 0.0
            if Double.TryParse(s, NumberStyles.Float, inv, &v) then Nullable(int (Math.Round v)) else Nullable()
        let toFloat (s:string) =
            let mutable v = 0.0
            if Double.TryParse(s, NumberStyles.Float, inv, &v) then Nullable(v) else Nullable()
        let toBool (s:string) = String.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s = "1"
        let fmt (v:float) = v.ToString("0.######", inv)
        let words (s:string) = s.Split([|' '; '\t'; '\r'; '\n'|], StringSplitOptions.RemoveEmptyEntries)
        /// A copy of an element, or null. A copy can go into a new document.
        let copy (e:XElement) = if isNil e then null else XElement(e)
        let texts (e:XElement) (name:string) = children e name |> Array.map text
        let minMax (e:XElement) =
            let mn = toInt (text (child e "Min"))
            let mx = toInt (text (child e "Max"))
            mn, mx

    // ------------------------------------------------------------------ OSD

    /// One OSD (on-screen display) item of a video source configuration.
    [<AllowNullLiteral>]
    type OsdItem() =
        member val Token = "" with get, set
        member val VideoSourceConfigurationToken = "" with get, set
        /// "Text", "Image" or "Extended". ODM changes only text items.
        member val OsdType = "Text" with get, set
        /// "Plain", "Date", "Time" or "DateAndTime".
        member val TextType = "Plain" with get, set
        member val PlainText = "" with get, set
        member val DateFormat = "" with get, set
        member val TimeFormat = "" with get, set
        member val FontSize = Nullable<int>() with get, set
        /// "UpperLeft", "UpperRight", "LowerLeft", "LowerRight" or "Custom".
        member val PositionType = "Custom" with get, set
        /// Custom position, -1 (left or bottom) to 1 (right or top).
        member val X = 0.0 with get, set
        member val Y = 0.0 with get, set
        /// The XML from the camera. ODM keeps the values that it does not show (colors, extensions).
        member val Source : XElement = null with get, set
        member this.IsText = this.OsdType = "Text"
        member this.IsNew = String.IsNullOrEmpty(this.Token)

    [<AllowNullLiteral>]
    type OsdOptions() =
        member val MaxTotal = Nullable<int>() with get, set
        member val MaxPlainText = Nullable<int>() with get, set
        member val MaxDate = Nullable<int>() with get, set
        member val MaxTime = Nullable<int>() with get, set
        member val MaxDateAndTime = Nullable<int>() with get, set
        member val PositionOptions : string[] = [||] with get, set
        member val TextTypes : string[] = [||] with get, set
        member val DateFormats : string[] = [||] with get, set
        member val TimeFormats : string[] = [||] with get, set
        member val FontSizeMin = Nullable<int>() with get, set
        member val FontSizeMax = Nullable<int>() with get, set

    module Osd =
        open Xml

        let parse (e:XElement) =
            let pos = child e "Position"
            let p = child pos "Pos"
            let ts = child e "TextString"
            let item = OsdItem()
            item.Token <- attr e "token"
            item.VideoSourceConfigurationToken <- text (child e "VideoSourceConfigurationToken")
            item.OsdType <- (let t = text (child e "Type") in if t = "" then "Text" else t)
            item.PositionType <- (let t = text (child pos "Type") in if t = "" then "Custom" else t)
            item.X <- (let v = toFloat (attr p "x") in if v.HasValue then v.Value else 0.0)
            item.Y <- (let v = toFloat (attr p "y") in if v.HasValue then v.Value else 0.0)
            item.TextType <- (let t = text (child ts "Type") in if t = "" then "Plain" else t)
            item.PlainText <- text (child ts "PlainText")
            item.DateFormat <- text (child ts "DateFormat")
            item.TimeFormat <- text (child ts "TimeFormat")
            item.FontSize <- toInt (text (child ts "FontSize"))
            item.Source <- e
            item

        let parseOptions (resp:XElement) =
            let o = child resp "OSDOptions"
            let max = child o "MaximumNumberOfOSDs"
            let text' = child o "TextOption"
            let fmin, fmax = minMax (child text' "FontSizeRange")
            let opts = OsdOptions()
            opts.MaxTotal <- toInt (attr max "Total")
            opts.MaxPlainText <- toInt (attr max "PlainText")
            opts.MaxDate <- toInt (attr max "Date")
            opts.MaxTime <- toInt (attr max "Time")
            opts.MaxDateAndTime <- toInt (attr max "DateAndTime")
            opts.PositionOptions <- texts o "PositionOption"
            opts.TextTypes <- texts text' "Type"
            opts.DateFormats <- texts text' "DateFormat"
            opts.TimeFormats <- texts text' "TimeFormat"
            opts.FontSizeMin <- fmin
            opts.FontSizeMax <- fmax
            opts

        /// The OSD element for SetOSD or CreateOSD. Values that ODM does not show come from the camera XML.
        let build (item:OsdItem) =
            let src = item.Source
            let srcText = child src "TextString"
            let showDate = item.TextType = "Date" || item.TextType = "DateAndTime"
            let showTime = item.TextType = "Time" || item.TextType = "DateAndTime"
            XElement(tr2 + "OSD",
                XAttribute(XName.Get("token"), item.Token),
                XElement(tt + "VideoSourceConfigurationToken", item.VideoSourceConfigurationToken),
                XElement(tt + "Type", "Text"),
                XElement(tt + "Position",
                    XElement(tt + "Type", item.PositionType),
                    (if item.PositionType = "Custom" then
                        XElement(tt + "Pos", XAttribute(XName.Get("x"), fmt item.X), XAttribute(XName.Get("y"), fmt item.Y))
                     else null)),
                XElement(tt + "TextString",
                    XElement(tt + "Type", item.TextType),
                    (if showDate && item.DateFormat <> "" then XElement(tt + "DateFormat", item.DateFormat) else null),
                    (if showTime && item.TimeFormat <> "" then XElement(tt + "TimeFormat", item.TimeFormat) else null),
                    (if item.FontSize.HasValue then XElement(tt + "FontSize", item.FontSize.Value) else null),
                    copy (child srcText "FontColor"),
                    copy (child srcText "BackgroundColor"),
                    (if item.TextType = "Plain" then XElement(tt + "PlainText", item.PlainText) else null),
                    copy (child srcText "Extension")),
                copy (child src "Extension"))

        let load (session:INvtSession) (configToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Media2, "GetOSDs",
                    XElement(tr2 + "GetOSDs", XElement(tr2 + "ConfigurationToken", configToken)))
            // Some cameras send the OSDs of all configurations. Show only the items of this one.
            return
                children resp "OSDs"
                |> Array.map parse
                |> Array.filter (fun x -> x.VideoSourceConfigurationToken = "" || x.VideoSourceConfigurationToken = configToken)
        }

        let loadOptions (session:INvtSession) (configToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Media2, "GetOSDOptions",
                    XElement(tr2 + "GetOSDOptions", XElement(tr2 + "ConfigurationToken", configToken)))
            return parseOptions resp
        }

        let save (session:INvtSession) (item:OsdItem) = async{
            if item.IsNew then
                let! resp = session.InvokeServiceRaw(OnvifNs.Media2, "CreateOSD", XElement(tr2 + "CreateOSD", build item))
                item.Token <- text (child resp "OSDToken")
            else
                do! session.InvokeServiceRaw(OnvifNs.Media2, "SetOSD", XElement(tr2 + "SetOSD", build item)) |> Async.Ignore
        }

        let delete (session:INvtSession) (token:string) =
            session.InvokeServiceRaw(OnvifNs.Media2, "DeleteOSD",
                XElement(tr2 + "DeleteOSD", XElement(tr2 + "OSDToken", token))) |> Async.Ignore

    // ------------------------------------------------------------------ privacy masks

    /// One privacy mask. The points are normalized, -1 to 1, with y up.
    [<AllowNullLiteral>]
    type MaskItem() =
        member val Token = "" with get, set
        member val ConfigurationToken = "" with get, set
        member val Points : System.Windows.Point[] = [||] with get, set
        /// "Color", "Pixelated" or "Blurred".
        member val MaskType = "Color" with get, set
        member val Enabled = true with get, set
        member val Source : XElement = null with get, set
        member this.IsNew = String.IsNullOrEmpty(this.Token)

    [<AllowNullLiteral>]
    type MaskOptions() =
        member val MaxMasks = Nullable<int>() with get, set
        member val MaxPoints = Nullable<int>() with get, set
        member val Types : string[] = [||] with get, set
        member val RectangleOnly = false with get, set
        member val SingleColorOnly = false with get, set
        /// A color for new "Color" masks, from the lowest values that the camera accepts. Null if the camera gives none.
        member val DefaultColor : XElement = null with get, set

    module Masks =
        open Xml

        let parse (e:XElement) =
            let m = MaskItem()
            m.Token <- attr e "token"
            m.ConfigurationToken <- text (child e "ConfigurationToken")
            m.Points <-
                children (child e "Polygon") "Point"
                |> Array.map (fun p ->
                    let x = toFloat (attr p "x")
                    let y = toFloat (attr p "y")
                    System.Windows.Point((if x.HasValue then x.Value else 0.0), (if y.HasValue then y.Value else 0.0)))
            m.MaskType <- (let t = text (child e "Type") in if t = "" then "Color" else t)
            m.Enabled <- (let t = text (child e "Enabled") in t = "" || toBool t)
            m.Source <- e
            m

        let parseOptions (resp:XElement) =
            let o = child resp "Options"
            let opts = MaskOptions()
            if not (isNil o) && o.HasElements then
                opts.MaxMasks <- toInt (text (child o "MaxMasks"))
                opts.MaxPoints <- toInt (text (child o "MaxPoints"))
                opts.Types <- texts o "Types"
                opts.RectangleOnly <- toBool (attr o "RectangleOnly")
                opts.SingleColorOnly <- toBool (attr o "SingleColorOnly")
                let range = child (child o "Color") "ColorspaceRange"
                if not (isNil range) then
                    let lo name = let mn, _ = minMax (child range name) in (if mn.HasValue then mn.Value else 0)
                    opts.DefaultColor <-
                        XElement(tr2 + "Color",
                            XAttribute(XName.Get("X"), lo "X"), XAttribute(XName.Get("Y"), lo "Y"), XAttribute(XName.Get("Z"), lo "Z"),
                            XAttribute(XName.Get("Colorspace"), text (child range "Colorspace")))
            opts

        let build (m:MaskItem) (options:MaskOptions) =
            let srcColor = child m.Source "Color"
            let color =
                let c =
                    if m.MaskType <> "Color" then null
                    elif not (isNil srcColor) then copy srcColor
                    elif not (isNil options) then copy options.DefaultColor
                    else null
                if not (isNil c) then c.Name <- tr2 + "Color"
                c
            // The Mask type is in the media2 schema, so its elements are in the tr2 namespace.
            XElement(tr2 + "Mask",
                XAttribute(XName.Get("token"), m.Token),
                XElement(tr2 + "ConfigurationToken", m.ConfigurationToken),
                XElement(tr2 + "Polygon",
                    m.Points |> Array.map (fun p ->
                        XElement(tt + "Point", XAttribute(XName.Get("x"), fmt p.X), XAttribute(XName.Get("y"), fmt p.Y)))),
                XElement(tr2 + "Type", m.MaskType),
                color,
                XElement(tr2 + "Enabled", (if m.Enabled then "true" else "false")))

        /// A rectangle as four points, clockwise from the upper left corner.
        let rectangle (left:float) (top:float) (right:float) (bottom:float) =
            [| System.Windows.Point(left, top); System.Windows.Point(right, top)
               System.Windows.Point(right, bottom); System.Windows.Point(left, bottom) |]

        let load (session:INvtSession) (configToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Media2, "GetMasks",
                    XElement(tr2 + "GetMasks", XElement(tr2 + "ConfigurationToken", configToken)))
            return
                children resp "Masks"
                |> Array.map parse
                |> Array.filter (fun x -> x.ConfigurationToken = "" || x.ConfigurationToken = configToken)
        }

        let loadOptions (session:INvtSession) (configToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Media2, "GetMaskOptions",
                    XElement(tr2 + "GetMaskOptions", XElement(tr2 + "ConfigurationToken", configToken)))
            return parseOptions resp
        }

        let save (session:INvtSession) (m:MaskItem) (options:MaskOptions) = async{
            if m.IsNew then
                let! resp = session.InvokeServiceRaw(OnvifNs.Media2, "CreateMask", XElement(tr2 + "CreateMask", build m options))
                m.Token <- text (child resp "Token")
            else
                do! session.InvokeServiceRaw(OnvifNs.Media2, "SetMask", XElement(tr2 + "SetMask", build m options)) |> Async.Ignore
        }

        let delete (session:INvtSession) (token:string) =
            session.InvokeServiceRaw(OnvifNs.Media2, "DeleteMask",
                XElement(tr2 + "DeleteMask", XElement(tr2 + "Token", token))) |> Async.Ignore

    // ------------------------------------------------------------------ video source modes

    [<AllowNullLiteral>]
    type VideoSourceModeItem() =
        member val Token = "" with get, set
        member val Enabled = false with get, set
        member val MaxFramerate = Nullable<float>() with get, set
        member val MaxWidth = Nullable<int>() with get, set
        member val MaxHeight = Nullable<int>() with get, set
        member val Encodings = "" with get, set
        /// True if the camera reboots when this mode is set.
        member val Reboot = false with get, set
        member val Description = "" with get, set
        override this.ToString() =
            let res = if this.MaxWidth.HasValue && this.MaxHeight.HasValue then sprintf "%dx%d" this.MaxWidth.Value this.MaxHeight.Value else ""
            let fps = if this.MaxFramerate.HasValue then sprintf " %s fps" (Xml.fmt this.MaxFramerate.Value) else ""
            let desc = if this.Description <> "" && this.Description <> this.Token then " - " + this.Description else ""
            sprintf "%s: %s%s%s" this.Token res fps desc

    module VideoSourceModes =
        open Xml

        let parse (e:XElement) =
            let m = VideoSourceModeItem()
            let res = child e "MaxResolution"
            m.Token <- attr e "token"
            m.Enabled <- toBool (attr e "Enabled")
            m.MaxFramerate <- toFloat (text (child e "MaxFramerate"))
            m.MaxWidth <- toInt (text (child res "Width"))
            m.MaxHeight <- toInt (text (child res "Height"))
            m.Encodings <- text (child e "Encodings")
            m.Reboot <- toBool (text (child e "Reboot"))
            m.Description <- text (child e "Description")
            m

        let load (session:INvtSession) (sourceToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Media2, "GetVideoSourceModes",
                    XElement(tr2 + "GetVideoSourceModes", XElement(tr2 + "VideoSourceToken", sourceToken)))
            return children resp "VideoSourceModes" |> Array.map parse
        }

        /// Returns true if the camera reboots to use the new mode.
        let set (session:INvtSession) (sourceToken:string) (modeToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Media2, "SetVideoSourceMode",
                    XElement(tr2 + "SetVideoSourceMode",
                        XElement(tr2 + "VideoSourceToken", sourceToken),
                        XElement(tr2 + "VideoSourceModeToken", modeToken)))
            return toBool (text (child resp "Reboot"))
        }

    // ------------------------------------------------------------------ recordings

    [<AllowNullLiteral>]
    type RecordingTrackItem() =
        member val Token = "" with get, set
        /// "Video", "Audio" or "Metadata".
        member val TrackType = "" with get, set
        member val Description = "" with get, set
        member val DataFrom = Nullable<System.DateTime>() with get, set
        member val DataTo = Nullable<System.DateTime>() with get, set

    [<AllowNullLiteral>]
    type RecordingItem() =
        member val Token = "" with get, set
        member val SourceName = "" with get, set
        member val SourceLocation = "" with get, set
        member val Content = "" with get, set
        /// The first and last recorded data, in UTC.
        member val EarliestRecording = Nullable<System.DateTime>() with get, set
        member val LatestRecording = Nullable<System.DateTime>() with get, set
        /// "Initiated", "Recording", "Stopped", "Removing", "Removed" or "Unknown".
        member val Status = "" with get, set
        member val Tracks : RecordingTrackItem[] = [||] with get, set

    [<AllowNullLiteral>]
    type RecordingSummaryInfo() =
        member val DataFrom = Nullable<System.DateTime>() with get, set
        member val DataUntil = Nullable<System.DateTime>() with get, set
        member val NumberRecordings = Nullable<int>() with get, set

    module Recordings =
        open Xml

        let toTime (s:string) =
            let mutable v = System.DateTime.MinValue
            if DateTime.TryParse(s, inv, DateTimeStyles.AdjustToUniversal ||| DateTimeStyles.AssumeUniversal, &v) then Nullable(v) else Nullable()

        /// Parses GetRecordingInformation or one RecordingInformation element of a search result.
        let parseInformation (info:XElement) =
            let src = child info "Source"
            let r = RecordingItem()
            r.Token <- text (child info "RecordingToken")
            r.SourceName <- text (child src "Name")
            r.SourceLocation <- text (child src "Location")
            r.Content <- text (child info "Content")
            r.EarliestRecording <- toTime (text (child info "EarliestRecording"))
            r.LatestRecording <- toTime (text (child info "LatestRecording"))
            r.Status <- text (child info "RecordingStatus")
            r.Tracks <-
                children info "Track" |> Array.map (fun t ->
                    let track = RecordingTrackItem()
                    track.Token <- text (child t "TrackToken")
                    track.TrackType <- text (child t "TrackType")
                    track.Description <- text (child t "Description")
                    track.DataFrom <- toTime (text (child t "DataFrom"))
                    track.DataTo <- toTime (text (child t "DataTo"))
                    track)
            r

        let parseSummary (resp:XElement) =
            let s = child resp "Summary"
            let sum = RecordingSummaryInfo()
            sum.DataFrom <- toTime (text (child s "DataFrom"))
            sum.DataUntil <- toTime (text (child s "DataUntil"))
            sum.NumberRecordings <- toInt (text (child s "NumberRecordings"))
            sum

        let loadSummary (session:INvtSession) = async{
            let! resp = session.InvokeServiceRaw(OnvifNs.Search, "GetRecordingSummary", XElement(tse + "GetRecordingSummary"))
            return parseSummary resp
        }

        let loadInformation (session:INvtSession) (recordingToken:string) = async{
            let! resp =
                session.InvokeServiceRaw(OnvifNs.Search, "GetRecordingInformation",
                    XElement(tse + "GetRecordingInformation", XElement(tse + "RecordingToken", recordingToken)))
            return parseInformation (child resp "RecordingInformation")
        }

        /// The recordings of the device, from the recording service, with the times from the search service.
        let load (session:INvtSession) = async{
            let rec_ = session :> obj :?> odm.onvif.IRecordingAsync
            let! items = rec_.GetRecordings()
            let items = if isNil items then [||] else items
            let! infos =
                items
                |> Array.map (fun it -> async{
                    try
                        let! info = loadInformation session it.recordingToken
                        return info
                    with err ->
                        // Without the search service, show what the recording service gives.
                        dbg.Error(err)
                        let r = RecordingItem()
                        r.Token <- it.recordingToken
                        let src = if isNil it.configuration then null else it.configuration.source
                        if not (isNil src) then
                            r.SourceName <- src.name
                            r.SourceLocation <- src.location
                        if not (isNil it.configuration) then r.Content <- it.configuration.content
                        r.Tracks <-
                            (if isNil it.tracks || isNil it.tracks.track then [||] else it.tracks.track) |> Array.map (fun t ->
                                let track = RecordingTrackItem()
                                track.Token <- t.trackToken
                                if not (isNil t.configuration) then
                                    track.TrackType <- t.configuration.trackType.ToString()
                                    track.Description <- t.configuration.description
                                track)
                        return r
                })
                |> Async.Parallel
            return infos
        }

        /// The RTSP URI to play a recording.
        let getReplayUri (session:INvtSession) (recordingToken:string) =
            let setup =
                new StreamSetup(
                    stream = StreamType.rtpUnicast,
                    transport = new Transport(protocol = TransportProtocol.rtsp))
            session.GetReplayUri(recordingToken, setup)

    // ------------------------------------------------------------------ page data

    /// Everything that the OSD page shows for one video source configuration.
    [<AllowNullLiteral>]
    type OsdPageData(configToken:string, items:OsdItem[], options:OsdOptions) =
        member this.ConfigToken = configToken
        member this.Items = items
        member this.Options = options

    [<AllowNullLiteral>]
    type MaskPageData(configToken:string, items:MaskItem[], options:MaskOptions) =
        member this.ConfigToken = configToken
        member this.Items = items
        member this.Options = options

    /// The video source modes (Media2) and the rotation (Media1) of one video source.
    /// ModesError is empty when the modes loaded; the page shows the error in place of the modes.
    [<AllowNullLiteral>]
    type VideoSourcePageData(sourceToken:string, configuration:VideoSourceConfiguration, modes:VideoSourceModeItem[], modesError:string, rotateOptions:RotateOptions) =
        member this.SourceToken = sourceToken
        member this.Configuration = configuration
        member this.Modes = modes
        member this.ModesError = modesError
        member this.RotateOptions = rotateOptions

    [<AllowNullLiteral>]
    type RecordingsPageData(summary:RecordingSummaryInfo, recordings:RecordingItem[]) =
        member this.Summary = summary
        member this.Recordings = recordings

    module FeaturePages =
        let private videoSourceConfiguration (session:INvtSession) (profileToken:string) = async{
            let! profile = session.GetProfile(profileToken)
            let vsc = if Xml.isNil profile then null else profile.videoSourceConfiguration
            if Xml.isNil vsc then
                return failwithf "the profile '%s' has no video source configuration" profileToken
            else
                return vsc
        }

        let private orDefault (f:unit -> 'T) (comp:Async<'T>) = async{
            try
                return! comp
            with err ->
                dbg.Error(err)
                return f()
        }

        let loadOsd (session:INvtSession) (profileToken:string) = async{
            let! vsc = videoSourceConfiguration session profileToken
            let! items = Osd.load session vsc.token
            // Without options the page still works, with free text input.
            let! options = Osd.loadOptions session vsc.token |> orDefault (fun () -> OsdOptions())
            return OsdPageData(vsc.token, items, options)
        }

        let loadMasks (session:INvtSession) (profileToken:string) = async{
            let! vsc = videoSourceConfiguration session profileToken
            let! items = Masks.load session vsc.token
            let! options = Masks.loadOptions session vsc.token |> orDefault (fun () -> MaskOptions())
            return MaskPageData(vsc.token, items, options)
        }

        let loadVideoSource (session:INvtSession) (profileToken:string) = async{
            let! vsc = videoSourceConfiguration session profileToken
            let! modes, modesError = async{
                try
                    let! modes = VideoSourceModes.load session vsc.sourceToken
                    return modes, ""
                with err ->
                    dbg.Error(err)
                    let rec inner (e:exn) = if Xml.isNil e.InnerException then e else inner e.InnerException
                    return [||], (inner err).Message
            }
            // The configuration from GetVideoSourceConfiguration has the rotation; the profile copy can be old.
            let! cfg = session.GetVideoSourceConfiguration(vsc.token) |> orDefault (fun () -> vsc)
            let! options = session.GetVideoSourceConfigurationOptions(vsc.token, profileToken) |> orDefault (fun () -> null)
            let rotate =
                if Xml.isNil options || Xml.isNil options.extension then null else options.extension.rotate
            return VideoSourcePageData(vsc.sourceToken, cfg, modes, modesError, rotate)
        }

        /// Sets the rotation and writes the configuration. The camera keeps the change after a reboot.
        let setRotation (session:INvtSession) (cfg:VideoSourceConfiguration) (mode:RotateMode) (degree:Nullable<int>) = async{
            if Xml.isNil cfg.extension then
                cfg.extension <- new VideoSourceConfigurationExtension()
            let rotate = new Rotate()
            rotate.mode <- mode
            if degree.HasValue then
                rotate.degree <- degree.Value
                rotate.degreeSpecified <- true
            cfg.extension.rotate <- rotate
            do! session.SetVideoSourceConfiguration(cfg, true)
        }

        let loadRecordings (session:INvtSession) = async{
            let! summary = Recordings.loadSummary session |> orDefault (fun () -> RecordingSummaryInfo())
            let! recordings = Recordings.load session
            return RecordingsPageData(summary, recordings)
        }
