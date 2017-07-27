namespace Metrics

module MetricInterface = 

    type MeasurementUnit = 
        | Bytes
        | KBytes
        | MBytes
        | Calls
        | Commands
        | Errors
        | Events
        | Items
        | Percent
        | Requests
        | Results
        | Threads

    type TimeUnit = 
        | NanoSeconds
        | MicroSeconds
        | MilliSeconds
        | Seconds
        | Minutes
        | Hours
        | Days   

    type MetricName  = MetricName  of string

    type CounterId   = CounterId   of string
    type MeterId     = MeterId     of string
    type TimerId     = TimerId     of string

    type Amount      = Amount      of int64
    type ContextName = ContextName of string    
    type Item        = Item        of string option

    type IMetricsCounter = 
        abstract member Decrement : unit          -> unit
        abstract member Decrement : Amount        -> unit
        abstract member Decrement : Amount * Item -> unit
        abstract member Increment : unit          -> unit
        abstract member Increment : Amount        -> unit
        abstract member Increment : Amount * Item -> unit

    type IMetricsMeter =
        abstract member Mark : Amount        -> unit
        abstract member Mark : Item          -> unit
        abstract member Mark : Amount * Item -> unit

    type IMetricsTimer = 
        abstract member Measure : Amount        -> unit
        abstract member Measure : Amount * Item -> unit

    type IMetricsAdapter = 
        abstract member CreateCounter : MetricName * MeasurementUnit                       -> IMetricsCounter
        abstract member CreateCounter : MetricName * MeasurementUnit * bool * bool * bool  -> IMetricsCounter
        abstract member CreateMeter   : MetricName * MeasurementUnit                       -> IMetricsMeter
        abstract member CreateMeter   : MetricName * MeasurementUnit * TimeUnit            -> IMetricsMeter
        abstract member CreateTimer   : MetricName * MeasurementUnit                       -> IMetricsTimer
        abstract member CreateTimer   : MetricName * MeasurementUnit * TimeUnit * TimeUnit -> IMetricsTimer

module MetricEvents = 

    open MetricInterface

    type DecrementCounterCommand = 
        { CounterId       : CounterId
          DecrementAmount : Amount
          Item            : Item }

    type IncrementCounterCommand = 
        { CounterId       : CounterId
          IncrementAmount : Amount
          Item            : Item }

    type MarkMeterCommand = 
        { MeterId : MeterId
          Amount  : Amount
          Item    : Item }

    type MeasureTimeCommand = 
        { TimerId : TimerId
          Time    : Amount
          Item    : Item }

    type CreateCounterCommand = 
        { CounterId             : CounterId
          Context               : ContextName
          Name                  : MetricName
          MeasurementUnit       : MeasurementUnit
          ReportItemPercentages : bool
          ReportSetItems        : bool
          ResetOnReporting      : bool }

    type CreateMeterCommand = 
        { MeterId         : MeterId
          Context         : ContextName
          Name            : MetricName
          MeasurementUnit : MeasurementUnit
          RateUnit        : TimeUnit }

    type CreateTimerCommand = 
        { TimerId         : TimerId
          Context         : ContextName
          Name            : MetricName
          MeasurementUnit : MeasurementUnit
          DurationUnit    : TimeUnit
          RateUnit        : TimeUnit }

    type MetricsMessage =
        | DecrementCounter of DecrementCounterCommand
        | IncrementCounter of IncrementCounterCommand
        | MarkMeter        of MarkMeterCommand
        | MeasureTime      of MeasureTimeCommand
        | CreateCounter    of CreateCounterCommand
        | CreateMeter      of CreateMeterCommand
        | CreateTimer      of CreateTimerCommand

module Metrics = 

    open MetricEvents
    open MetricInterface
    open Akka.Event

    let private createCounter (evtStream: EventStream) counterId =
        { new IMetricsCounter with
            
            member this.Decrement() = 
                this.Decrement (Amount 1L)

            member this.Decrement amount = 
                this.Decrement (amount, Item None)

            member this.Decrement (amount, item) = 
                evtStream.Publish <| DecrementCounter { CounterId = counterId; DecrementAmount = amount; Item = item }

            member this.Increment() = 
                this.Increment (Amount 1L)

            member this.Increment amount = 
                this.Increment (amount, Item None)

            member this.Increment (amount, item) = 
                evtStream.Publish <| IncrementCounter { CounterId = counterId; IncrementAmount = amount; Item = item }
        }

    let private createMeter (evtStream: EventStream) meterId = 
        { new IMetricsMeter with

              member this.Mark amount = 
                  this.Mark (amount, Item None)

              member this.Mark item = 
                  this.Mark (Amount 1L, item)

              member this.Mark (amount, item) = 
                  evtStream.Publish <| MarkMeter { MeterId = meterId; Amount = amount; Item = item }
        }
            
    let private createTimer (evtStream: EventStream) timerId =
        { new IMetricsTimer with

              member this.Measure time = 
                  this.Measure (time, Item None)

              member this.Measure (time, item) = 
                  evtStream.Publish <| MeasureTime { TimerId = timerId; Time = time; Item = item }
        }

    let createAdapter (evtStream: EventStream) context =
        let (ContextName contextName) = context
        let contextTrimmed = contextName.Trim()

        let toId (MetricName name) =
            let nameTrimmed = name.Trim()
            sprintf "%s/%s" contextTrimmed nameTrimmed

        { new IMetricsAdapter with

            member this.CreateCounter (name, measureUnit) =
                this.CreateCounter (name, measureUnit, true, true, false)

            member this.CreateCounter (name, measureUnit, reportItemPercentages, reportSetItems, resetOnReporting) = 
                let cmd = 
                    { CounterId             = CounterId (toId name)
                      Context               = context
                      Name                  = name
                      MeasurementUnit       = measureUnit
                      ReportItemPercentages = reportItemPercentages
                      ReportSetItems        = reportSetItems
                      ResetOnReporting      = resetOnReporting }
                evtStream.Publish <| CreateCounter cmd
                createCounter evtStream cmd.CounterId

            member this.CreateMeter (name, measureUnit) =
                this.CreateMeter (name, measureUnit, Minutes)

            member this.CreateMeter (name, measureUnit, rateUnit) = 
                let cmd = 
                    { MeterId         = MeterId (toId name)
                      Context         = context
                      Name            = name
                      MeasurementUnit = measureUnit
                      RateUnit        = rateUnit }
                evtStream.Publish <| CreateMeter cmd
                createMeter evtStream cmd.MeterId

            member this.CreateTimer (name, measureUnit) =
                this.CreateTimer (name, measureUnit, MilliSeconds, Seconds)

            member this.CreateTimer (name, measureUnit, durationUnit, rateUnit) = 
                let cmd = 
                    { TimerId         = TimerId (toId name)
                      Context         = context
                      Name            = name
                      MeasurementUnit = measureUnit
                      DurationUnit    = durationUnit
                      RateUnit        = rateUnit }
                evtStream.Publish <| CreateTimer cmd
                createTimer evtStream cmd.TimerId
        }
        
module MetricExtensions = 
    open Metrics
    open Akka.Actor

    type IActorContext with
        member x.GetMetricsProducer context = 
            createAdapter x.System.EventStream context