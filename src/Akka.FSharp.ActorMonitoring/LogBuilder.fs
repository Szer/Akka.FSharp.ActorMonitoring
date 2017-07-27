module LogBuilder

open Akka.Event
open System
open System.Collections.Generic
open System.Diagnostics

type Fragment =
    | OperationName     of string
    | OperationDuration of TimeSpan
    | TotalDuration     of TimeSpan
    | ReceivedOn        of DateTimeOffset
    | MessageType       of Type
    | Exception         of exn

type private LoggerStatus = 
    | Active
    | Supressed

type ILogBuilder = 
    abstract OnOperationBegin:     unit     -> unit
    abstract OnOperationCompleted: unit     -> unit
    abstract Set:                  LogLevel -> unit
    abstract Set:                  Fragment -> unit
    abstract Fail:                 exn      -> unit
    abstract Supress:              unit     -> unit
    abstract TryGet:               Fragment -> Fragment option

type LogBuilder(logger: ILoggingAdapter) = 
    let logFragments = new Dictionary<System.Type, Fragment>()
    let stopwatch    = new Stopwatch()
    let mutable logLevel = LogLevel.DebugLevel
    let mutable status   = Active

    let set fragment = 
        logFragments.[fragment.GetType()] <- fragment

    let tryGet (fragment: Fragment) =
        match logFragments.TryGetValue (fragment.GetType()) with
        | (true, result) -> Some result
        | _              -> None

    let message() = 
        let seq =  
            logFragments
            |> Seq.map (fun kv ->
                match kv.Value with
                | OperationName     x -> (kv.Key, x)
                | OperationDuration x -> (kv.Key, x.TotalMilliseconds.ToString())
                | TotalDuration     x -> (kv.Key, x.TotalMilliseconds.ToString())
                | Exception         x -> (kv.Key, x.Message)
                | MessageType       x -> (kv.Key, x.Name)
                | x                   -> (kv.Key, x.ToString()))
            |> Seq.map (fun (t,v) -> sprintf "%s: %s" t.Name v)
        String.Join (", ", seq)
        
    interface ILogBuilder with
        member this.TryGet fragment = 
            tryGet fragment

        member x.OnOperationBegin() =   
            stopwatch.Start()

        member this.OnOperationCompleted() = 
            stopwatch.Stop()
            set <| OperationDuration stopwatch.Elapsed

            match tryGet <| ReceivedOn DateTimeOffset.MinValue with
            | Some (ReceivedOn date) -> set <| TotalDuration (DateTimeOffset.UtcNow - date)
            | _ -> ()

            match status with
            | Active ->
                match (logLevel) with
                | LogLevel.DebugLevel   -> logger.Debug(message())
                | LogLevel.InfoLevel    -> logger.Info(message())
                | LogLevel.WarningLevel -> logger.Warning(message())
                | LogLevel.ErrorLevel   -> logger.Error(message())
                | x                     -> failwith(sprintf "Log level %s is not supported" <| string x)
            | Supressed -> ()

        member this.Fail e = 
            logLevel <- LogLevel.ErrorLevel
            set <| Exception e

        member this.Set fragment = 
            set fragment

        member this.Set logLvl = 
            logLevel <- logLvl

        member this.Supress(): unit = 
            status <- Supressed