namespace Metrics

module MetricApi = 
    
    open System.Web.Http
    open App.Metrics

    type public MetricController(metrics: IMetrics) = 
        inherit ApiController()

        [<HttpGet>]
        [<Route("metrics")>]
        member __.GetMetrics() =
            __.Ok(metrics.Snapshot.Get())

module MetricActors =
    open Akka.FSharp
    open Metrics.MetricEvents
    open Metrics.MetricInterface
    open App.Metrics.Counter.Abstractions
    open App.Metrics
    open App.Metrics.Core.Options
    open App.Metrics.Meter.Abstractions
    open App.Metrics.Timer.Abstractions
    open System.Collections.Generic
    open Microsoft.Owin.Hosting
    open Owin
    open System.Web.Http
    open App.Metrics.Formatters.Json.Converters
    open System
    
    let createRecorder (metrics: IMetrics) (mailbox: Actor<_>) = 
        let self = mailbox.Self

        let counters = new Dictionary<CounterId, ICounter>()
        let meters   = new Dictionary<MeterId,   IMeter>()
        let timers   = new Dictionary<TimerId,   ITimer * TimeUnit>()

        let toUnit = function
            | Bytes    -> Unit.Bytes
            | KBytes   -> Unit.KiloBytes
            | MBytes   -> Unit.MegaBytes
            | Calls    -> Unit.Calls
            | Commands -> Unit.Commands
            | Errors   -> Unit.Errors
            | Events   -> Unit.Events
            | Items    -> Unit.Items
            | Percent  -> Unit.Percent
            | Requests -> Unit.Requests
            | Results  -> Unit.Results
            | Threads  -> Unit.Threads

        let toTimeUnit = function
        | NanoSeconds  -> App.Metrics.TimeUnit.Nanoseconds
        | MicroSeconds -> App.Metrics.TimeUnit.Microseconds
        | MilliSeconds -> App.Metrics.TimeUnit.Milliseconds
        | Seconds      -> App.Metrics.TimeUnit.Seconds
        | Minutes      -> App.Metrics.TimeUnit.Minutes
        | Hours        -> App.Metrics.TimeUnit.Hours
        | Days         -> App.Metrics.TimeUnit.Days

        let handle = function
            | DecrementCounter evt ->
                match counters.TryGetValue evt.CounterId with
                | (false, _) -> ()
                | (true,  c) ->
                    let (Amount am) = evt.DecrementAmount
                    match evt.Item with
                    | Item (Some i) -> c.Decrement (i, am)
                    | Item None     -> c.Decrement (am)

            | IncrementCounter evt ->
                match counters.TryGetValue evt.CounterId with
                | (false, _) -> ()
                | (true,  c) ->
                    let (Amount am) = evt.IncrementAmount
                    match evt.Item with
                    | Item (Some i) -> c.Increment (i, am)
                    | Item None     -> c.Increment (am)

            | MarkMeter evt ->
                match meters.TryGetValue evt.MeterId with
                | (false, _) -> ()
                | (true,  m) ->
                    let (Amount am) = evt.Amount
                    match evt.Item with
                    | Item (Some i) -> m.Mark (i, am)
                    | Item None     -> m.Mark (am)

            | MeasureTime evt ->
                match timers.TryGetValue evt.TimerId with
                | (false, _)      -> ()
                | (true,  (t, u)) ->
                    let (Amount am) = evt.Time
                    match evt.Item with
                    | Item (Some i) -> t.Record (am, u, i)
                    | Item None     -> t.Record (am, u)

            | CreateCounter cmd ->
                match counters.TryGetValue cmd.CounterId with
                | (false, _) ->
                    let (ContextName ctxName) = cmd.Context
                    let (MetricName name)     = cmd.Name
                    let options = new CounterOptions(
                                        Context               = ctxName, 
                                        MeasurementUnit       = toUnit cmd.MeasurementUnit, 
                                        Name                  = name,
                                        ReportItemPercentages = cmd.ReportItemPercentages,
                                        ReportSetItems        = cmd.ReportSetItems,
                                        ResetOnReporting      = cmd.ResetOnReporting)
                    let c = metrics.Provider.Counter.Instance options
                    counters.Add(cmd.CounterId, c)
                | _ -> ()

            | CreateMeter cmd ->
                match meters.TryGetValue cmd.MeterId with
                | (false, _) ->
                    let (ContextName ctxName) = cmd.Context
                    let (MetricName name)     = cmd.Name
                    let options = new MeterOptions(
                                        Context         = ctxName, 
                                        MeasurementUnit = toUnit cmd.MeasurementUnit, 
                                        Name            = name,
                                        RateUnit        = toTimeUnit cmd.RateUnit)
                    let m = metrics.Provider.Meter.Instance options
                    meters.Add(cmd.MeterId, m)
                | _ -> ()

            | CreateTimer cmd ->
                match timers.TryGetValue cmd.TimerId     with
                | (false, _) ->
                    let (ContextName ctxName) = cmd.Context
                    let (MetricName name)     = cmd.Name
                    let options = new TimerOptions(
                                        Context         = ctxName, 
                                        DurationUnit    = toTimeUnit cmd.DurationUnit,
                                        MeasurementUnit = toUnit cmd.MeasurementUnit, 
                                        Name            = name,
                                        RateUnit        = toTimeUnit cmd.RateUnit)
                    let t = metrics.Provider.Timer.Instance options
                    timers.Add(cmd.TimerId, (t, options.DurationUnit))
                | _ -> ()

        subscribe typedefof<MetricsMessage> self mailbox.Context.System.EventStream |> ignore

        let rec loop() = actor {
            let! msg = mailbox.Receive()
            handle msg
            return! loop()
        }
        loop()

    type IMetricApiConfig = 
        abstract member Host: string
        abstract member Port: int
    
    type ApiMessage = ReStartApiMessage

    let createReader (config: IMetricApiConfig) resolver (mailbox: Actor<_>) =
        let startUp (app: IAppBuilder) = 
            let httpConfig = new HttpConfiguration(DependencyResolver = resolver)
            httpConfig.Formatters.JsonFormatter.SerializerSettings.Converters.Add(new MetricDataConverter())
            httpConfig.Formatters.JsonFormatter.Indent <- true
            httpConfig.MapHttpAttributeRoutes()
            httpConfig.EnsureInitialized()
            app.UseWebApi(httpConfig) |> ignore

        let uri = sprintf "http://%s:%d" config.Host config.Port
        let mutable api = {new IDisposable with member this.Dispose() = ()}

        let handleMsg (ReStartApiMessage) = 
            api.Dispose()
            api <- WebApp.Start(uri, startUp)

        mailbox.Defer api.Dispose
        mailbox.Self <! ReStartApiMessage

        let rec loop() = actor {
            let! msg = mailbox.Receive()
            handleMsg msg
            return! loop()
        }
        loop()