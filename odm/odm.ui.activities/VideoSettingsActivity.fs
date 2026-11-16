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


    type VideoSettingsActivity(ctx:IUnityContainer, profToken:string) = class
        do if profToken |> IsNull then raise( new ArgumentNullException("profToken") )
        let session = ctx.Resolve<INvtSession>()
        let facade = new OdmSession(session)
        
        let show_error(err:Exception) = async{
            dbg.Error(err)
            do! ErrorView.Show(ctx, err) |> Async.Ignore
        }

        /// True when the page was loaded from Media2, so Apply must use Media2 too.
        let usingMedia2 = ref false

        /// The profile's encoder seen through Media2 (which, unlike Media1, can say H.265):
        /// Some(client, configuration, options per encoding), or None to use Media1 - also
        /// when the camera's Media2 is missing or misbehaves, so this page never gets worse.
        let getMedia2(vecToken:string) = async{
            try
                let! m2 = session.GetMedia2()
                if m2 |> IsNull then
                    return None
                else
                    do! Async.SwitchToThreadPool()
                    let cfg =
                        let byToken =
                            try m2.GetVideoEncoderConfigurations(vecToken, null) |> Array.tryFind (fun c -> NotNull(c) && c.token = vecToken)
                            with err -> dbg.Error(err); None
                        match byToken with
                        | Some _ -> byToken
                        | None -> m2.GetVideoEncoderConfigurations(null, profToken) |> Array.tryFind NotNull
                    match cfg with
                    | Some cfg when cfg.encoding.HasValue && NotNull(cfg.resolution) ->
                        let opts =
                            try m2.GetVideoEncoderConfigurationOptions(cfg.token, profToken)
                            with err -> dbg.Error(err); m2.GetVideoEncoderConfigurationOptions(cfg.token, null)
                        let opts = opts |> Array.filter (fun o -> NotNull(o) && o.encoding.HasValue && o.resolutions.Length > 0)
                        if opts.Length = 0 then
                            Media2Client.Log(sprintf "%s: no usable Media2 encoder options -> Media1; reply: %s" session.deviceUri.Host m2.LastReply)
                            return None
                        else
                            Media2Client.Log(sprintf "%s: using Media2: %s %dx%d, options: %s" session.deviceUri.Host cfg.encodingName cfg.resolution.width cfg.resolution.height (String.Join(" ", opts |> Seq.map (fun o -> o.encodingName))))
                            return Some(m2, cfg, opts)
                    | _ ->
                        Media2Client.Log(sprintf "%s: no usable Media2 encoder configuration for token %s -> Media1; reply: %s" session.deviceUri.Host vecToken m2.LastReply)
                        return None
            with err ->
                dbg.Error(err)
                Media2Client.Log(sprintf "%s: Media2 failed -> Media1: %s: %s" session.deviceUri.Host (err.GetType().Name) err.Message)
                return None
        }

        let loadMedia2(cfg:Media2VideoEncoderConfiguration, opts:Media2VideoEncoderOptions[]) =
            let span (values:seq<'a>) (fallback:'a*'a) =
                if Seq.isEmpty values then fallback else (Seq.min values, Seq.max values)
            let ranges (pick:Media2VideoEncoderOptions->IntRange) =
                opts |> Seq.map pick |> Seq.filter NotNull |> Seq.toList

            let frameRate = float(cfg.frameRateLimit)
            let (minFrameRate, maxFrameRate) =
                let (lo, hi) = span (opts |> Seq.collect (fun o -> o.frameRates) |> Seq.map float) (frameRate, frameRate)
                (int(Math.Floor(lo)), int(Math.Ceiling(hi)))
            let (minBitrate, maxBitrate) =
                match ranges (fun o -> o.bitrateRange) with
                | [] -> (0, Int32.MaxValue)
                | rs -> (rs |> Seq.map (fun r -> r.min) |> Seq.min, rs |> Seq.map (fun r -> r.max) |> Seq.max)
            let (minGovLength, maxGovLength) =
                match ranges (fun o -> o.govLengthRange) with
                | [] -> (cfg.govLength, cfg.govLength)
                | rs -> (rs |> Seq.map (fun r -> r.min) |> Seq.min, rs |> Seq.map (fun r -> r.max) |> Seq.max)
            let (minQuality, maxQuality) =
                let qs = opts |> Seq.filter (fun o -> o.qualityMin >= 0.0f && o.qualityMax >= o.qualityMin)
                if Seq.isEmpty qs then (-1, -1)
                else (int(qs |> Seq.map (fun o -> o.qualityMin) |> Seq.min), int(Math.Ceiling(float(qs |> Seq.map (fun o -> o.qualityMax) |> Seq.max))))

            let model = new VideoSettingsView.Model(
                minQuality = minQuality,
                maxQuality = maxQuality,
                minBitrate = minBitrate,
                maxBitrate = maxBitrate,
                // Media2 has no encoding interval: every frame is encoded.
                minEncodingInterval = 1,
                maxEncodingInterval = 1,
                minFrameRate = minFrameRate,
                maxFrameRate = maxFrameRate,
                minGovLength = minGovLength,
                maxGovLength = maxGovLength,
                encoderOptions = null,
                profToken = profToken
            )
            model.media2EncoderResolutions <-
                opts |> Array.collect (fun o -> o.resolutions |> Array.map (fun r -> Tuple.Create(o.encoding.Value, r)))

            model.encoder <- cfg.encoding.Value
            model.resolution <- cfg.resolution
            model.frameRate <- frameRate
            model.govLength <- cfg.govLength
            model.encodingInterval <- 1
            model.quality <- cfg.quality
            model.bitrate <- float(cfg.bitrateLimit)

            model.AcceptChanges()
            model

        let apply_media2(model:VideoSettingsView.Model) = async{
            let! profile = session.GetProfile(profToken)
            let! m2 = getMedia2(profile.videoEncoderConfiguration |> IfNotNull (fun x -> x.token))
            match m2 with
            | None ->
                return raise (new Exception("The camera's Media2 service did not answer, so the settings were not applied."))
            | Some(client, cfg, opts) ->
                let opt =
                    opts |> Array.tryFind (fun o ->
                        o.encoding.Value = model.encoder && (o.resolutions |> Array.exists (fun r -> r.Equals(model.resolution)))
                    )
                match opt with
                | None ->
                    return false
                | Some o ->
                    let frameRate =
                        if o.frameRates.Length > 0 then
                            // only listed rates are allowed: take the nearest
                            o.frameRates |> Array.minBy (fun f -> abs(float(f) - model.frameRate))
                        else
                            float32(model.frameRate)
                    let quality =
                        if o.qualityMin >= 0.0f && o.qualityMax >= o.qualityMin then
                            model.quality |> Math.Coerce o.qualityMin o.qualityMax
                        else
                            model.quality
                    let bitrate =
                        if o.bitrateRange |> NotNull then
                            int(model.bitrate) |> Math.Coerce o.bitrateRange.min o.bitrateRange.max
                        else
                            int(model.bitrate)
                    let govLength =
                        if o.govLengthRange |> NotNull then
                            model.govLength |> Math.Coerce o.govLengthRange.min o.govLengthRange.max
                        else
                            -1
                    do! Async.SwitchToThreadPool()
                    client.SetVideoEncoderConfiguration(cfg, o, model.resolution, quality, frameRate, bitrate, govLength)
                    Media2Client.Log(sprintf "%s: set %s %dx%d, %g fps, %d kbps, quality %g, GOV %d" session.deviceUri.Host o.encodingName model.resolution.width model.resolution.height frameRate bitrate quality govLength)
                    model.AcceptChanges()
                    return true
        }

        let loadMedia1(profile:Profile) = async{
            //let! profiles = session.GetProfiles()
            //let profile = profiles |> Seq.find (fun p-> p.token = profileToken)
            
            //TODO: show modal dialog to chose VSC, in case if the profile doesn't have one
            //TODO: show modal dialog to chose VEC, in case if the profile doesn't have one
            
            let vec  = profile.videoEncoderConfiguration
            let! options = session.GetVideoEncoderConfigurationOptions(vec.token, profile.token)
            
            let resolution = vec.resolution
            let framerate = 
                if vec.rateControl |> NotNull then
                    vec.rateControl.frameRateLimit
                else
                    -1
            let encodingInterval = 
                if vec.rateControl |> NotNull then
                    vec.rateControl.encodingInterval
                else
                    -1
            let bitrate = 
                if vec.rateControl |> NotNull then
                    vec.rateControl.bitrateLimit
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
            })

            let encIntervalRanges = Seq.toList(seq{
                if options.h264 |> NotNull then
                    yield options.h264.encodingIntervalRange
                if options.jpeg |> NotNull then
                    yield options.jpeg.encodingIntervalRange
                if options.mpeg4 |> NotNull then
                    yield options.mpeg4.encodingIntervalRange
            })
            
            let govLengthRanges = Seq.toList(seq{
                if options.h264 |> NotNull then
                    yield options.h264.govLengthRange
                if options.mpeg4 |> NotNull then
                    yield options.mpeg4.govLengthRange
            })
            let govLength = 
                if vec.encoding = VideoEncoding.h264 && NotNull(vec.h264) then
                    vec.h264.govLength
                elif vec.encoding = VideoEncoding.mpeg4 && NotNull(vec.mpeg4) then
                    vec.mpeg4.govLength
                else
                    -1

            let bitrateRanges = Seq.toList(seq{
                if NotNull(options.extension) && NotNull(options.extension.any) then
                    let tt = @"http://www.onvif.org/ver10/schema"
                    for x in options.extension.any |> Seq.filter (fun x->x.NamespaceURI = tt) do
                        if x.Name = @"JPEG" then
                            yield x.Deserialize<JpegOptions2>().bitrateRange
                        elif x.Name = @"MPEG4" then
                            yield x.Deserialize<Mpeg4Options2>().bitrateRange
                        elif x.Name = @"H264" then
                            yield x.Deserialize<H264Options2>().bitrateRange
            })
            
            let quality = vec.quality
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
            
            model.encoder <- vec.encoding
            model.resolution <- resolution
            model.frameRate <- float(framerate)
            model.govLength <- govLength
            model.encodingInterval <- encodingInterval
            model.quality <- quality
            model.bitrate <- float(bitrate)

            model.AcceptChanges()
            return model
        }

        let load() = async{
            let! profile = session.GetProfile(profToken)
            let! m2 = getMedia2(profile.videoEncoderConfiguration |> IfNotNull (fun x -> x.token))
            match m2 with
            | Some(_, cfg, opts) ->
                usingMedia2 := true
                return loadMedia2(cfg, opts)
            | None ->
                usingMedia2 := false
                return! loadMedia1(profile)
        }

        let apply_media1(model:VideoSettingsView.Model) = async{
            
            //let! profiles = session.GetProfiles()
            //let profile = profiles |> Seq.find (fun p-> p.token = profToken)
            let! profile = session.GetProfile(profToken)
            let vec = profile.videoEncoderConfiguration
//            
//            do! session.RemoveVideoEncoderConfiguration(profile.token)
//            profile.VideoEncoderConfiguration <- null

            //let! vecs = session.GetCompatibleVideoEncoderConfigurations(profile.token)
            
            let! options = session.GetVideoEncoderConfigurationOptions(vec.token, null)
            let qualityMin = float32(options.qualityRange.min)
            let qualityMax = float32(options.qualityRange.max)
            //let quality = Math.Min(qualityMax, Math.Max(model.quality, qualityMin))
            let quality = model.quality |> Math.Coerce qualityMin qualityMax
            
            vec.encoding <- model.encoder
            vec.quality <- quality
            vec.resolution <- model.resolution
                
            let inline CoerceGovLength (options:^TOpt) = 
                let range = (^TOpt: (member govLengthRange:IntRange)(options))
                if range |> NotNull then
                    Math.Coerce (range.min) (range.max)
                else
                    (fun(v)->v)

            let inline validateConfig(opts:^TOpt) = 
                if options |> NotNull then
                    let resolutions = (^TOpt: (member resolutionsAvailable:VideoResolution[])(opts))
                    if resolutions |> Array.exists (fun x->x=model.resolution) then
                        if vec.rateControl |> IsNull then
                            vec.rateControl <- new VideoRateControl()
                        let frameRateRange = (^TOpt: (member frameRateRange:IntRange)(opts))
                        let frameRateMin = frameRateRange.min
                        let frameRateMax = frameRateRange.max
                        let frameRate = int(model.frameRate) |> Math.Coerce frameRateMin frameRateMax 
                        vec.rateControl.frameRateLimit <- frameRate
                        vec.rateControl.bitrateLimit <- int(model.bitrate)

                        let encodingIntervalRange = (^TOpt: (member encodingIntervalRange:IntRange)(opts))
                        let encodingIntervalMin = encodingIntervalRange.min
                        let encodingIntervalMax = encodingIntervalRange.max
                        let encodingInterval = model.encodingInterval |> Math.Coerce encodingIntervalMin encodingIntervalMax 
                        vec.rateControl.encodingInterval <- encodingInterval
                        if model.encoder = VideoEncoding.h264 then
                            if vec.h264 |> IsNull then vec.h264 <- new H264Configuration()
                            vec.h264.govLength <- model.govLength |> CoerceGovLength(options.h264)
                        elif model.encoder = VideoEncoding.mpeg4 then
                            if vec.mpeg4 |> IsNull then vec.mpeg4 <- new Mpeg4Configuration()
                            vec.mpeg4.govLength <- model.govLength |> CoerceGovLength(options.mpeg4)
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
                |_ -> raise (new ArgumentException(LocalVideoSettings.instance.errorEncoder))
            
            if isVecConfigured then 
                do! session.SetVideoEncoderConfiguration(vec, true)
                model.AcceptChanges()

            return isVecConfigured
        }

        let apply_changes(model:VideoSettingsView.Model) =
            if !usingMedia2 then apply_media2(model) else apply_media1(model)
        
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
