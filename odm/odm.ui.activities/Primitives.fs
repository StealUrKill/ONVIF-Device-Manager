namespace odm.ui.activities
    open System
    open System.IO
    open System.Linq
    open System.Collections.Generic
    open System.Collections.ObjectModel
    open System.Threading
    open System.Net
    open System.Windows
    open System.Windows.Threading

    open Microsoft.Win32
    open Microsoft.Practices.Unity

    open odm.onvif
    open odm.core
    open odm.infra
    //open odm.models
    open utils
    open utils.fsharp

    type Progress() = class
        static member Show(container:IUnityContainer, message:string) = async{
            let disp = Application.Current.Dispatcher
            //do! Async.Sleep(200)
            return! disp.InvokeAsync(fun ()->
                //if (!disp.IsDisposed) {
                let view = new ProgressView(message)
                //disp.Add(presenter.ShowView(view));
                let presenter = container.Resolve<IViewPresenter>()
                presenter.ShowView(view)
                //}
            )
        }
    end

//     type ErrorInfo() = class
//        static member Show(ctx:IUnityContainer, message:string) = async{
//            let disp = Application.Current.Dispatcher
//            //do! Async.Sleep(200)
//            return! disp.InvokeAsync(fun ()->
//                //if (!disp.IsDisposed) {
//                let presenter = ctx.Resolve<IViewPresenter>()
//                let view = new ProgressView(message)
//                //disp.Add(presenter.ShowView(view));
//                presenter.ShowView(view)
//                //}
//            )
//        }
//    end

    /// Get the encoder resolution of a profile. Try Media1, then Media2, then the video source bounds.
    /// Return null if no resolution is available.
    type EncoderResolution() = class
        static let valid (r:onvif.services.VideoResolution) =
            r |> NotNull && r.width > 0 && r.height > 0

        /// Return true if the Media1 encoder configuration has no resolution.
        static member IsMedia1Stub(vec:onvif.services.VideoEncoderConfiguration) =
            vec |> NotNull && not (valid vec.resolution)

        static member Resolve(session:INvtSession, profile:onvif.services.Profile) = async{
            let vec = if profile |> NotNull then profile.videoEncoderConfiguration else null
            if vec |> NotNull && valid vec.resolution then
                return vec.resolution
            else
                let! media2Cfgs = async{
                    if vec |> IsNull then
                        return [||]
                    else
                        try
                            return! session.GetVideoEncoderConfigurationsMedia2()
                        with err ->
                            dbg.Error(err)
                            return [||]
                }
                let fromMedia2 =
                    media2Cfgs |> Array.tryFind (fun c -> c |> NotNull && c.token = vec.token && valid c.resolution)
                match fromMedia2 with
                | Some cfg ->
                    return cfg.resolution
                | None ->
                    let vsc = if profile |> NotNull then profile.videoSourceConfiguration else null
                    if vsc |> NotNull && vsc.bounds |> NotNull && vsc.bounds.width > 0 && vsc.bounds.height > 0 then
                        return new onvif.services.VideoResolution(width = vsc.bounds.width, height = vsc.bounds.height)
                    else
                        return null
        }
    end
