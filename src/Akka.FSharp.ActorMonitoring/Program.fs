open Akka.FSharp
open SimpleInjector
open App.Metrics;
open Microsoft.Extensions.DependencyInjection
open SimpleInjector.Integration.WebApi
open System.Reflection
open System
open Metrics.MetricActors
open ExampleActors

let createSystem = 
    let configStr = System.IO.File.ReadAllText("system.json")
    System.create "system-for-metrics" (Configuration.parse(configStr))

let createMetricActors system container = 
    let dependencyResolver = new SimpleInjectorWebApiDependencyResolver(container)
    let apiConfig = 
        { new IMetricApiConfig with
            member x.Host = "localhost"
            member x.Port = 10001 }
    
    let metricsReaderSpawner = createReader apiConfig dependencyResolver
    let metricsReader = spawn system "metrics-reader" metricsReaderSpawner

    let metricsRecorderSpawner = createRecorder (container.GetInstance<IMetrics>())
    let metricsRecorder = spawn system "metrics-recorder" metricsRecorderSpawner
    ()

type Container with
    member x.AddMetrics() = 
        let serviceCollection  = new ServiceCollection()
        let entryAssemblyName  = Assembly.GetEntryAssembly().GetName()
        let metricsHostBuilder = serviceCollection.AddMetrics(entryAssemblyName)

        serviceCollection.AddLogging() |> ignore
        let provider = serviceCollection.BuildServiceProvider()

        x.Register(fun () -> provider.GetRequiredService<IMetrics>())

[<EntryPoint>]
let main argv = 
    let container = new Container()
    let system = createSystem

    container.RegisterSingleton system
    container.AddMetrics()
    container.Verify()

    createMetricActors system container

    let waitWorker1      = spawn system "worker-wait1"  <| spawnWaitWorker()
    let waitWorker2      = spawn system "worker-wait2"  <| spawnWaitWorker()
    let waitWorker3      = spawn system "worker-wait3"  <| spawnWaitWorker()
    let waitWorker4      = spawn system "worker-wait4"  <| spawnWaitWorker()

    let failWorker       = spawn system "worker-fail"   <| spawnFailWorker()
    let waitOrStopWorker = spawn system "worker-silent" <| spawnWaitOrStopWorker()
    let failOrStopWorker = spawn system "worker-vocal"  <| spawnFailOrStopWorker()

    waitWorker1 <! Wait 1000
    waitWorker2 <! Wait 500
    waitWorker3 <! Wait 5000
    waitWorker4 <! Wait 8000

    failWorker  <! Wait 5000

    waitOrStopWorker <! Wait 1000
    waitOrStopWorker <! Wait 500
    waitOrStopWorker <! Stop
    waitOrStopWorker <! Wait 500

    failOrStopWorker <! Wait 1000
    failOrStopWorker <! Wait 500
    failOrStopWorker <! Stop
    failOrStopWorker <! Wait 500

    Console.ReadKey() |> ignore

    0