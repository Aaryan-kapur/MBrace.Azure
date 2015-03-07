﻿namespace MBrace.Azure.Runtime.Common

    open System
    open System.Net
    open System.Management
    open System.Diagnostics
    open System.Collections.Generic

    type private PerfCounter = System.Diagnostics.PerformanceCounter

    /// Some node metrics, such as CPU, memory usage, etc
    type NodePerformanceInfo =
        {
            CpuUsage            : Nullable<double>
            TotalMemory         : Nullable<double>
            MemoryUsage         : Nullable<double>
            NetworkUsageUp      : Nullable<double>
            NetworkUsageDown    : Nullable<double>
        } 

    type private Counter = TotalCpu | TotalMemoryUsage 
    type private Message = Info of AsyncReplyChannel<NodePerformanceInfo> | Stop of AsyncReplyChannel<unit>

    /// Collects statistics on CPU, memory, network, etc.
    type PerformanceMonitor (?updateInterval : int, ?maxSamplesCount : int) =

        // Get a new counter value after 0.1 sec and keep the last 10 values
        let updateInterval = defaultArg updateInterval 100
        let maxSamplesCount = defaultArg maxSamplesCount 10
    
        let perfCounters = new List<PerfCounter>()

        // Performance counters 
        let cpuUsage =
            if PerformanceCounterCategory.Exists("Processor") then 
                let pc = new PerfCounter("Processor", "% Processor Time", "_Total",true)
                perfCounters.Add(pc)
                Some <| fun () -> pc.NextValue()
            else None
    
        let totalMemory = 
            use searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")
            use qObj = searcher.Get() 
                        |> Seq.cast<ManagementBaseObject> 
                        |> Seq.exactlyOne
            let totalBytes = qObj.["TotalPhysicalMemory"] :?> uint64
            let mb = totalBytes / uint64 (1 <<< 20) |> single // size in MB
            Some(fun () -> mb)
    
        let memoryUsage = 
            if PerformanceCounterCategory.Exists("Memory") 
            then 
                match totalMemory with
                | None -> None
                | Some(getNext) ->
                    let pc = new PerfCounter("Memory", "Available Mbytes",true)
                    perfCounters.Add(pc)
                    let totalMemory = getNext()
                    Some <| (fun () -> 100.f - 100.f * pc.NextValue() / totalMemory)
            else None
    
        let networkSentUsage =
            if PerformanceCounterCategory.Exists("Network Interface") then 
                let inst = (new PerformanceCounterCategory("Network Interface")).GetInstanceNames()
                let pc = 
                    inst |> Array.map (fun nic -> new PerfCounter("Network Interface", "Bytes Sent/sec", nic))
                Seq.iter perfCounters.Add pc
                Some(fun () -> pc |> Array.fold (fun sAcc s -> sAcc + 8.f * s.NextValue () / 1024.f) 0.f) // kbps
            else None
    
        let networkReceivedUsage =
            if PerformanceCounterCategory.Exists("Network Interface") then 
                let inst = (new PerformanceCounterCategory("Network Interface")).GetInstanceNames()
                let pc = 
                    inst |> Array.map (fun nic -> new PerfCounter("Network Interface", "Bytes Received/sec",nic))
                Seq.iter perfCounters.Add pc
                Some(fun () -> pc |> Array.fold (fun rAcc r -> rAcc + 8.f * r.NextValue () / 1024.f ) 0.f) // kbps
            else None
    
        let getPerfValue : (unit -> single) option -> Nullable<double> = function
            | None -> Nullable<_>()
            | Some(getNext) -> Nullable<_>(double <| getNext())
    
        let getAverage (values : Nullable<double> seq) =
            if values |> Seq.exists (fun v -> not v.HasValue) then Nullable<_>()
            else values |> Seq.map (function v -> v.Value)
                        |> Seq.average
                        |> fun v -> Nullable<_>(v)
    
        let cpuAvg = Queue<Nullable<double>>()
    
        let updateCpuQueue () =
            let newVal = getPerfValue cpuUsage
            if cpuAvg.Count < maxSamplesCount then cpuAvg.Enqueue newVal
            else cpuAvg.Dequeue() |> ignore; cpuAvg.Enqueue newVal
    
        let newNodePerformanceInfo () : NodePerformanceInfo =
            {
                CpuUsage            = cpuAvg                |> getAverage
                TotalMemory         = totalMemory           |> getPerfValue
                MemoryUsage         = memoryUsage           |> getPerfValue
                NetworkUsageUp      = networkSentUsage      |> getPerfValue
                NetworkUsageDown    = networkReceivedUsage  |> getPerfValue
            }

        let perfCounterActor = 
            new MailboxProcessor<Message>(fun inbox ->    
                let rec agentLoop () : Async<unit> = async {
                    updateCpuQueue ()
    
                    while inbox.CurrentQueueLength <> 0 do
                        let! msg = inbox.Receive()
                        match msg with
                        | Stop ch -> ch.Reply (); return ()
                        | Info ch -> newNodePerformanceInfo () |> ch.Reply
    
                    do! Async.Sleep updateInterval
    
                    return! agentLoop ()
                }
                agentLoop ())

        let monitored =
            let l = new List<string>()
            if cpuUsage.IsSome then l.Add("%Cpu")
            if totalMemory.IsSome then l.Add("Total Memory")
            if memoryUsage.IsSome then l.Add("%Memory")
            if networkSentUsage.IsSome then l.Add("Network (sent)")
            if networkReceivedUsage.IsSome then l.Add("Network (received)")
            l

        member this.GetCounters () : NodePerformanceInfo =
            perfCounterActor.PostAndReply(fun ch -> Info ch)

        member this.Start () =
            perfCounterActor.Start()
            this.GetCounters() |> ignore // first value always 0

        member this.MonitoredCategories : string seq = monitored :> _

        interface System.IDisposable with
            member this.Dispose () = 
                perfCounterActor.PostAndReply(fun ch -> Stop ch)
                perfCounters |> Seq.iter (fun c -> c.Dispose())  