//module VideoSettingsActivity
namespace odm.ui.activities
    open System
    open System.Linq
    //open System.Disposables
    open System.Collections.Generic
    open System.Collections.ObjectModel
    open System.Threading
    open System.Net
    open System.Windows
    open System.Windows.Threading
    
    open Microsoft.Practices.Unity
    //open Microsoft.Practices.Prism.Commands
    //open Microsoft.Practices.Prism.Events

    open onvif.services
    open onvif.utils

    open odm.onvif
    open odm.core
    open odm.infra
    open utils
    //open odm.models
    open utils.fsharp
    open odm.ui
//    open odm.ui.core
//    open odm.ui.controls
//    open odm.ui.views
//    open odm.ui.dialogs


    /// Media2 support for the video streaming page. Media2 can show H265 and complete encoder data.
    /// ODM converts the Media2 options to the Media1 options that the view uses.
    type Media2VideoSettings() = class
        static member private IntRangeOf(lo:int, hi:int) = new IntRange(min = lo, max = hi)

        static member private FrameRateRange(o:Media2EncoderOptions) =
            if o.FrameRates.Length = 0 then null
            else Media2VideoSettings.IntRangeOf(int (Math.Floor(Array.min o.FrameRates)), int (Math.Ceiling(Array.max o.FrameRates)))

        static member private GovRange(o:Media2EncoderOptions) =
            match o.GovLengthRange with
            | Some (lo, hi) -> Media2VideoSettings.IntRangeOf(lo, hi)
            | None -> null

        /// Media2 has no encoding interval. A fixed range of 1 to 1 disables the control.
        static member private NoEncodingInterval() = Media2VideoSettings.IntRangeOf(1, 1)

        static member ToMedia1Options(opts:Media2EncoderOptions[]) =
            let options = new VideoEncoderConfigurationOptions()
            let qualities = opts |> Array.choose (fun o -> o.QualityRange)
            if qualities.Length > 0 then
                options.qualityRange <- Media2VideoSettings.IntRangeOf(
                    qualities |> Array.map (fun (lo, _) -> int (Math.Floor(float lo))) |> Array.min,
                    qualities |> Array.map (fun (_, hi) -> int (Math.Ceiling(float hi))) |> Array.max)
            for o in opts do
                match o.Encoding with
                | Some VideoEncoding.h264 ->
                    options.h264 <- new H264Options(
                        resolutionsAvailable = o.Resolutions,
                        govLengthRange = Media2VideoSettings.GovRange(o),
                        frameRateRange = Media2VideoSettings.FrameRateRange(o),
                        encodingIntervalRange = Media2VideoSettings.NoEncodingInterval())
                | Some VideoEncoding.h265 ->
                    options.h265 <- new H265Options(
                        resolutionsAvailable = o.Resolutions,
                        govLengthRange = Media2VideoSettings.GovRange(o),
                        frameRateRange = Media2VideoSettings.FrameRateRange(o),
                        encodingIntervalRange = Media2VideoSettings.NoEncodingInterval())
                | Some VideoEncoding.jpeg ->
                    options.jpeg <- new JpegOptions(
                        resolutionsAvailable = o.Resolutions,
                        frameRateRange = Media2VideoSettings.FrameRateRange(o),
                        encodingIntervalRange = Media2VideoSettings.NoEncodingInterval())
                | _ -> ()
            options

        static member BitrateRange(opts:Media2EncoderOptions[]) =
            let ranges = opts |> Array.choose (fun o -> o.BitrateRange)
            if ranges.Length = 0 then None
            else Some (ranges |> Array.map fst |> Array.min, ranges |> Array.map snd |> Array.max)

        static member GovLengthOf(cfg:VideoEncoderConfiguration) =
            if cfg.encoding = VideoEncoding.h265 && cfg.h265 |> NotNull then cfg.h265.govLength
            elif cfg.encoding = VideoEncoding.h264 && cfg.h264 |> NotNull then cfg.h264.govLength
            else -1

        /// Make the Media2 change for the model. Return None if the camera does not support the settings.
        /// Values stay in the camera ranges. ODM uses the nearest supported frame rate.
        static member BuildChange(model:VideoSettingsView.Model, current:VideoEncoderConfiguration, opts:Media2EncoderOptions[]) =
            match opts |> Array.tryFind (fun o -> o.Encoding = Some model.encoder) with
            | None -> None
            | Some o when not (o.Resolutions |> Array.exists (fun r -> r = model.resolution)) -> None
            | Some o ->
                let frameRate =
                    if o.FrameRates.Length = 0 then Some model.frameRate
                    else Some (o.FrameRates |> Array.minBy (fun r -> abs (r - model.frameRate)))
                let bitrate =
                    let v = int model.bitrate
                    match o.BitrateRange with
                    | Some (lo, hi) -> Some (v |> Math.Coerce lo hi)
                    | None -> if v > 0 then Some v else None
                let quality =
                    match o.QualityRange with
                    | Some (lo, hi) -> Some (model.quality |> Math.Coerce lo hi)
                    | None -> Some model.quality
                let govLength =
                    if model.encoder = VideoEncoding.jpeg || model.govLength <= 0 then None
                    else
                        match o.GovLengthRange with
                        | Some (lo, hi) -> Some (model.govLength |> Math.Coerce lo hi)
                        | None -> Some model.govLength
                // H.264 and H.265 have different profiles. If the encoding changes, select a supported profile.
                // If not, keep the current profile of the camera.
                let profile =
                    if model.encoder = current.encoding || o.ProfilesSupported.Length = 0 then None
                    elif o.ProfilesSupported |> Array.exists ((=) "Main") then Some "Main"
                    else Some o.ProfilesSupported.[0]
                Some {
                    NewEncoding = model.encoder
                    NewResolution = model.resolution
                    NewFrameRateLimit = frameRate
                    NewBitrateLimit = bitrate
                    NewGovLength = govLength
                    NewQuality = quality
                    NewProfile = profile
                }
    end

    type VideoSettingsActivity(ctx:IUnityContainer, profToken:string) = class
        do if profToken |> IsNull then raise( new ArgumentNullException("profToken") )
        let session = ctx.Resolve<INvtSession>()
        let facade = new OdmSession(session)
        
        let show_error(err:Exception) = async{
            dbg.Error(err)
            do! ErrorView.Show(ctx, err) |> Async.Ignore
        }

        let load() = async{
            //let origin = model.origin

            let! profile = session.GetProfile(profToken)
            //let! profiles = session.GetProfiles()
            //let profile = profiles |> Seq.find (fun p-> p.token = profileToken)
            
            //TODO: show modal dialog to chose VSC, in case if the profile doesn't have one
            //TODO: show modal dialog to chose VEC, in case if the profile doesn't have one
            
            let vec  = profile.videoEncoderConfiguration
            if vec |> IsNull then
                failwith "the profile has no video encoder configuration"
            let! media1Options = async{
                try
                    let! o = session.GetVideoEncoderConfigurationOptions(vec.token, profile.token)
                    return Choice1Of2 o
                with err ->
                    return Choice2Of2 err
            }

            // Fix: infer effective H265 encoding using the same multi-case logic as
            // ProfileDescription.GetVecDetails (see comments there for full rationale).
            // Also checks vec.any for cameras whose H265 sub-element lacks the ONVIF namespace.
            let anyH265 =
                NotNull(vec.any) &&
                vec.any |> Array.exists (fun (e:System.Xml.XmlElement) -> e.LocalName = "H265")
            let media1Encoding =
                if vec.encoding = VideoEncoding.h264 && (NotNull(vec.h265) || anyH265) then
                    VideoEncoding.h265
                else
                    vec.encoding

            // Override with Media2 result for cameras that advertise ver20/media/wsdl but
            // still report Encoding=H264 via Media1 (e.g. the confirmed case at 10.102.10.7).
            let! media2Cfgs =
                async{
                    try return! session.GetVideoEncoderConfigurationsMedia2()
                    with _ -> return [||]
                }
            let m2match = media2Cfgs |> Array.tryFind (fun c -> NotNull(c) && c.token = vec.token && c.resolution |> NotNull)
            let! m2opts = async{
                match m2match with
                | Some _ ->
                    try return! session.GetVideoEncoderConfigurationOptionsMedia2(vec.token, profile.token)
                    with err ->
                        dbg.Error(err)
                        return [||]
                | None -> return [||]
            }
            // Use Media2 if it is available, because Media2 can show H265 and complete encoder data.
            // If not, use Media1.
            let m2cfg = if m2opts.Length > 0 then m2match else None

            let options =
                match m2cfg, media1Options with
                | Some _, _ -> Media2VideoSettings.ToMedia1Options(m2opts)
                | None, Choice1Of2 o -> o
                | None, Choice2Of2 err -> raise err

            // Some cameras send a Media1 encoder configuration without encoding or resolution.
            // In this condition, use the values from Media2.
            let isStubVec = EncoderResolution.IsMedia1Stub(vec)
            let m2stub = if isStubVec then m2match else None

            let effectiveEncoding =
                match m2cfg, m2match with
                | Some c, _ -> c.encoding
                | None, Some c when c.encoding <> VideoEncoding.h264 || isStubVec -> c.encoding
                | _ -> media1Encoding

            let resolution =
                match m2cfg, m2stub with
                | Some c, _ -> c.resolution
                | None, Some c when c.resolution |> NotNull -> c.resolution
                | _ -> vec.resolution
            let rateControl =
                match m2cfg, m2stub with
                | Some c, _ when c.rateControl |> NotNull -> c.rateControl
                | None, Some c when vec.rateControl |> IsNull && c.rateControl |> NotNull -> c.rateControl
                | _ -> vec.rateControl
            let framerate =
                if rateControl |> NotNull then
                    rateControl.frameRateLimit
                else
                    -1
            let encodingInterval =
                if m2cfg.IsSome then 1  // Media2 has no encoding interval
                elif vec.rateControl |> NotNull then
                    vec.rateControl.encodingInterval
                else
                    -1
            let bitrate =
                if rateControl |> NotNull then
                    rateControl.bitrateLimit
                else
                    -1

//            let resolutions = Set.ofSeq (seq{
//                yield vec.Resolution
//                if options.H264 <> null then
//                    yield! options.H264.ResolutionsAvailable
//                if options.JPEG <> null then
//                    yield! options.JPEG.ResolutionsAvailable
//                if options.MPEG4 <> null then
//                    yield! options.MPEG4.ResolutionsAvailable
//            })
            
//            let encoders = Set.ofSeq (seq{
//                yield vec.Encoding
//                if options.H264 <> null then
//                    yield VideoEncoding.H264
//                if options.JPEG <> null then
//                    yield VideoEncoding.JPEG
//                if options.MPEG4 <> null then
//                    yield VideoEncoding.MPEG4
//            })

            let frameRateRanges = Seq.toList(seq{
                if options.h264 |> NotNull then
                    yield options.h264.frameRateRange
                if options.jpeg |> NotNull then
                    yield options.jpeg.frameRateRange
                if options.mpeg4 |> NotNull then
                    yield options.mpeg4.frameRateRange
                if options.h265 |> NotNull && options.h265.frameRateRange |> NotNull then
                    yield options.h265.frameRateRange
            })

            let encIntervalRanges = Seq.toList(seq{
                if options.h264 |> NotNull then
                    yield options.h264.encodingIntervalRange
                if options.jpeg |> NotNull then
                    yield options.jpeg.encodingIntervalRange
                if options.mpeg4 |> NotNull then
                    yield options.mpeg4.encodingIntervalRange
                if options.h265 |> NotNull && options.h265.encodingIntervalRange |> NotNull then
                    yield options.h265.encodingIntervalRange
            })
            
            let govLengthRanges = Seq.toList(seq{
                if options.h264 |> NotNull then
                    yield options.h264.govLengthRange
                if options.mpeg4 |> NotNull then
                    yield options.mpeg4.govLengthRange
                if options.h265 |> NotNull && options.h265.govLengthRange |> NotNull then
                    yield options.h265.govLengthRange
            })
            let govLength =
                if m2cfg.IsSome then
                    Media2VideoSettings.GovLengthOf(m2cfg.Value)
                elif effectiveEncoding = VideoEncoding.h264 && NotNull(vec.h264) then
                    vec.h264.govLength
                elif effectiveEncoding = VideoEncoding.mpeg4 && NotNull(vec.mpeg4) then
                    vec.mpeg4.govLength
                elif effectiveEncoding = VideoEncoding.h265 && NotNull(vec.h265) then
                    vec.h265.govLength
                else
                    -1

            let bitrateRanges = Seq.toList(seq{
                if m2cfg.IsSome then
                    match Media2VideoSettings.BitrateRange(m2opts) with
                    | Some (lo, hi) -> yield new IntRange(min = lo, max = hi)
                    | None -> ()
                elif NotNull(options.extension) && NotNull(options.extension.any) then
                    let tt = @"http://www.onvif.org/ver10/schema"
                    for x in options.extension.any |> Seq.filter (fun x->x.NamespaceURI = tt) do
                        if x.Name = @"JPEG" then
                            yield x.Deserialize<JpegOptions2>().bitrateRange
                        elif x.Name = @"MPEG4" then
                            yield x.Deserialize<Mpeg4Options2>().bitrateRange
                        elif x.Name = @"H264" then
                            yield x.Deserialize<H264Options2>().bitrateRange
                        elif x.Name = @"H265" then
                            yield x.Deserialize<H265Options2>().bitrateRange
            })
            
            let quality =
                match m2cfg, m2stub with
                | Some c, _ -> c.quality
                | None, Some c -> c.quality
                | _ -> vec.quality
            let qualityRange = options.qualityRange |> SuppressNull (new IntRange(min = -1, max= -1))
            
            let (minFrameRate, maxFrameRate) = 
                if frameRateRanges.Length > 0 then
                    let min = frameRateRanges |> Seq.map (fun x->x.min) |> Seq.min
                    let max = frameRateRanges |> Seq.map (fun x->x.max) |> Seq.max
                    (min, max)
                else
                    (framerate, framerate)

            let (minEncodingInterval, maxEncodingInterval) = 
                if encIntervalRanges.Length > 0 then
                    let min = encIntervalRanges |> Seq.map (fun x->x.min) |> Seq.min
                    let max = encIntervalRanges |> Seq.map (fun x->x.max) |> Seq.max
                    (min, max)
                else
                    (encodingInterval, encodingInterval)

            let (minBitrate, maxBitrate) = 
                if bitrateRanges.Length > 0 then
                    let min = bitrateRanges |> Seq.map (fun x->x.min) |> Seq.min
                    let max = bitrateRanges |> Seq.map (fun x->x.max) |> Seq.max
                    (min, max)
                else
                    (0, Int32.MaxValue)

            let (minGovLength, maxGovLength) = 
                if govLengthRanges.Length > 0 then
                    let min = govLengthRanges |> Seq.map (fun x->x.min) |> Seq.min
                    let max = govLengthRanges |> Seq.map (fun x->x.max) |> Seq.max
                    (min, max)
                else
                    (govLength, govLength)
            
            let model = new VideoSettingsView.Model(
                minQuality = qualityRange.min,
                maxQuality = qualityRange.max,
                minBitrate = minBitrate,
                maxBitrate = maxBitrate,
                minEncodingInterval = minEncodingInterval,
                maxEncodingInterval = maxEncodingInterval,
                minFrameRate = minFrameRate,
                maxFrameRate = maxFrameRate,
                minGovLength = minGovLength,
                maxGovLength = maxGovLength,
                //encoders = (encoders |> Set.toArray),
                //resolutions = (resolutions |> Set.toArray),
                encoderOptions = options,
                profToken = profToken
            )
            
            model.encoder <- effectiveEncoding
            model.resolution <- resolution
            model.frameRate <- float(framerate)
            model.govLength <- govLength
            model.encodingInterval <- encodingInterval
            model.quality <- quality
            model.bitrate <- float(bitrate)

            model.AcceptChanges()
            return model
        }

        let apply_changes_media1(model:VideoSettingsView.Model) = async{

            //let! profiles = session.GetProfiles()
            //let profile = profiles |> Seq.find (fun p-> p.token = profToken)
            let! profile = session.GetProfile(profToken)
            let vec = profile.videoEncoderConfiguration
            // This Media1 configuration has no encoding or resolution. Do not write it back,
            // because the write can set JPEG (the default value) or an incomplete configuration.
            if EncoderResolution.IsMedia1Stub(vec) then
                failwith "This camera reports its video encoder settings only through ONVIF Media2, and the Media2 service did not respond. Use the camera's web interface to change encoder settings."
//
//            do! session.RemoveVideoEncoderConfiguration(profile.token)
//            profile.VideoEncoderConfiguration <- null

            //let! vecs = session.GetCompatibleVideoEncoderConfigurations(profile.token)

            let! options = session.GetVideoEncoderConfigurationOptions(vec.token, null)
            //let quality = Math.Min(qualityMax, Math.Max(model.quality, qualityMin))
            let quality =
                if options.qualityRange |> NotNull then
                    model.quality |> Math.Coerce (float32 options.qualityRange.min) (float32 options.qualityRange.max)
                else
                    model.quality

            // Media1 shows H264, but load() found H265 through Media2 and the user kept it. Keep the
            // Media1 encoding and do not set the H265 GOV length. If the user selected H265, send H265.
            let isMedia2OnlyH265 =
                model.encoder = VideoEncoding.h265 &&
                model.origin.encoder = VideoEncoding.h265 &&
                vec.encoding = VideoEncoding.h264

            vec.encoding <- if isMedia2OnlyH265 then vec.encoding else model.encoder
            vec.quality <- quality
            vec.resolution <- model.resolution
                
            let inline CoerceGovLength (options:^TOpt) = 
                let range = (^TOpt: (member govLengthRange:IntRange)(options))
                if range |> NotNull then
                    Math.Coerce (range.min) (range.max)
                else
                    (fun(v)->v)

            let coerceRange (range:IntRange) (v:int) =
                if range |> NotNull then v |> Math.Coerce range.min range.max else v

            let inline validateConfig(opts:^TOpt) =
                if opts |> NotNull then
                    let resolutions = (^TOpt: (member resolutionsAvailable:VideoResolution[])(opts))
                    if resolutions |> NotNull && resolutions |> Array.exists (fun x->x=model.resolution) then
                        if vec.rateControl |> IsNull then
                            vec.rateControl <- new VideoRateControl()
                        let frameRateRange = (^TOpt: (member frameRateRange:IntRange)(opts))
                        let frameRate = int(model.frameRate) |> coerceRange frameRateRange
                        vec.rateControl.frameRateLimit <- frameRate
                        vec.rateControl.bitrateLimit <- int(model.bitrate)

                        let encodingIntervalRange = (^TOpt: (member encodingIntervalRange:IntRange)(opts))
                        let encodingInterval = model.encodingInterval |> coerceRange encodingIntervalRange
                        vec.rateControl.encodingInterval <- encodingInterval
                        // Set the GOV length for the encoding that is sent (vec.encoding).
                        // Media1 cannot set the H265 GOV length for H265 that is only in Media2.
                        if not isMedia2OnlyH265 then
                            if vec.encoding = VideoEncoding.h264 && options.h264 |> NotNull then
                                if vec.h264 |> IsNull then vec.h264 <- new H264Configuration()
                                vec.h264.govLength <- model.govLength |> CoerceGovLength(options.h264)
                            elif vec.encoding = VideoEncoding.mpeg4 && options.mpeg4 |> NotNull then
                                if vec.mpeg4 |> IsNull then vec.mpeg4 <- new Mpeg4Configuration()
                                vec.mpeg4.govLength <- model.govLength |> CoerceGovLength(options.mpeg4)
                            elif vec.encoding = VideoEncoding.h265 && options.h265 |> NotNull then
                                if vec.h265 |> IsNull then vec.h265 <- new H265Configuration()
                                vec.h265.govLength <- model.govLength |> CoerceGovLength(options.h265)
                        true
                    else
                        false
                else
                    false
            

            let isVecConfigured =
                match model.encoder with
                |VideoEncoding.h264 -> validateConfig(options.h264)
                |VideoEncoding.jpeg -> validateConfig(options.jpeg)
                |VideoEncoding.mpeg4 -> validateConfig(options.mpeg4)
                |VideoEncoding.h265 ->
                    if isMedia2OnlyH265 then
                        // Media1 doesn't understand H265 config — apply h264 rate settings
                        // instead. H265-specific govLength cannot be set via Media1.
                        validateConfig(options.h264)
                    else
                        validateConfig(options.h265)
                |_ -> raise (new ArgumentException(LocalVideoSettings.instance.errorEncoder))
            
            if isVecConfigured then 
                do! session.SetVideoEncoderConfiguration(vec, true)
                model.AcceptChanges()

            return isVecConfigured
        }

        /// Apply the changes through Media2. Return None if Media2 is not available,
        /// so that the caller can use Media1.
        let apply_changes_media2(model:VideoSettingsView.Model) = async{
            let! profile = session.GetProfile(profToken)
            let vec = profile.videoEncoderConfiguration
            if vec |> IsNull then
                return None
            else
                let! cfgs = async{
                    try return! session.GetVideoEncoderConfigurationsMedia2()
                    with _ -> return [||]
                }
                match cfgs |> Array.tryFind (fun c -> NotNull(c) && c.token = vec.token && c.resolution |> NotNull) with
                | None -> return None
                | Some current ->
                    let! opts = async{
                        try return! session.GetVideoEncoderConfigurationOptionsMedia2(vec.token, profile.token)
                        with err ->
                            dbg.Error(err)
                            return [||]
                    }
                    if opts.Length = 0 then
                        return None
                    else
                        match Media2VideoSettings.BuildChange(model, current, opts) with
                        | None ->
                            // The camera does not support this encoding or resolution.
                            return Some false
                        | Some change ->
                            do! session.UpdateVideoEncoderConfigurationMedia2(vec.token, change)
                            model.AcceptChanges()
                            return Some true
        }

        let apply_changes(model:VideoSettingsView.Model) = async{
            let! viaMedia2 = apply_changes_media2(model)
            match viaMedia2 with
            | Some configured -> return configured
            | None -> return! apply_changes_media1(model)
        }

        member private this.Main() = async{
            let! cont = async{
                try
                    let! model = async{
                        use! progress = Progress.Show(ctx, LocalDevice.instance.loading)
                        return! load()
                    }
                    return this.ShowForm(model)
                with err -> 
                    do! show_error(err)
                    return this.Main()
            }
            return! cont
        }

        member private this.ShowForm(model) = async{
            let! cont = async{
                try
                    let! res = VideoSettingsView.Show(ctx, model)
                    return res.Handle(
                        apply = (fun model-> 
                            if model.isModified then 
                                this.Apply(model) 
                            else 
                                this.Main()
                        ),
                        none = (fun ()-> this.Complete())
                    )
                with err -> 
                    do! show_error(err)
                    return this.ShowForm(model)
            }
            return! cont
        }

        member private this.Apply(model) = async{
            let! cont = async{
                try
                    let! vecWasConfigured = async{
                        use! progress = Progress.Show(ctx, LocalDevice.instance.applying)
                        return! apply_changes(model)
                    }
                    if vecWasConfigured then
                        return this.Main()
                    else
                        do! show_error(new Exception(LocalVideoSettings.instance.errorSupportResolution))
                        return this.Main()
                with err ->
                    do! show_error(err)
                    return this.Main()
            }
            return! cont
        }

        member private this.Complete(result) = async{
            return result
        }

        static member Run(ctx, profileToken) = 
            let act = new VideoSettingsActivity(ctx,profileToken)
            act.Main()
    end
