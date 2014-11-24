﻿namespace Nessos.MBrace.Azure.Runtime

// Provides facility for the execution of tasks.
// In this context, a task denotes a single work item to be sent
// to a worker node for execution. Tasks may span multiple threads
// but are bound to a single process. A cloud workflow that has
// been passed continuations is a typical example of such a task.

open System
open System.Threading.Tasks

open Nessos.Vagrant

open Nessos.MBrace
open Nessos.MBrace.Runtime
open Nessos.MBrace.Azure.Runtime.Common
open Nessos.MBrace.Runtime.Serialization
open Nessos.MBrace.Azure.Runtime.Resources
open Nessos.MBrace.Azure.Runtime.Common.Storage

// Tasks are cloud workflows that have been attached to continuations.
// In that sense they are 'closed' multi-threaded computations that
// are difficult to reason about from a worker node's point of view.
// TaskExecutionMonitor provides a way to cooperatively track execution
// of such 'closed' computations.

/// Provides a mechanism for cooperative task execution monitoring.
[<AutoSerializable(false)>]
type TaskExecutionMonitor () =
    let tcs = TaskCompletionSource<unit> ()
    static let fromContext (ctx : ExecutionContext) = ctx.Resources.Resolve<TaskExecutionMonitor> ()

    member __.Task = tcs.Task
    member __.TriggerFault (e : exn) = tcs.TrySetException e |> ignore
    member __.TriggerCompletion () = tcs.TrySetResult () |> ignore

    /// Runs a single threaded, synchronous computation,
    /// triggering the contextual TaskExecutionMonitor on uncaught exception
    static member ProtectSync ctx (f : unit -> unit) : unit =
        let tem = fromContext ctx
        try f () with e -> tem.TriggerFault e |> ignore

    /// Runs an asynchronous computation,
    /// triggering the contextual TaskExecutionMonitor on uncaught exception
    static member ProtectAsync ctx (f : Async<unit>) : unit =
        let tem = fromContext ctx
        Async.StartWithContinuations(f, ignore, tem.TriggerFault, ignore)   

    /// Triggers task completion on the contextual TaskExecutionMonitor
    static member TriggerCompletion ctx =
        let tem = fromContext ctx in tem.TriggerCompletion () |> ignore

    /// Triggers task fault on the contextual TaskExecutionMonitor
    static member TriggerFault (ctx, e) =
        let tem = fromContext ctx in tem.TriggerFault e |> ignore

    /// Asynchronously await completion of provided TaskExecutionMonitor
    static member AwaitCompletion (tem : TaskExecutionMonitor) = async {
        try
            return! Async.AwaitTask tem.Task
        with :? System.AggregateException as e when e.InnerException <> null ->
            return! Async.Raise e.InnerException
    }

/// Defines a task to be executed in a worker node
type Task = 
    {
        /// Return type of the defining cloud workflow.
        Type : Type
        /// Cloud process unique identifier
        ProcessId : string
        /// Task unique identifier
        TaskId : string
        /// Triggers task execution with worker-provided execution context
        StartTask : ExecutionContext -> unit
        /// Distributed cancellation token source bound to task
        CancellationTokenSource : DistributedCancellationTokenSource
    }
with
    /// <summary>
    ///     Asynchronously executes task in the local process.
    /// </summary>
    /// <param name="runtimeProvider">Local scheduler implementation.</param>
    /// <param name="dependencies">Task dependent assemblies.</param>
    /// <param name="task">Task to be executed.</param>
    static member RunAsync (runtimeProvider : IRuntimeProvider) (dependencies : AssemblyId list) (task : Task) = async {
        let tem = new TaskExecutionMonitor()
        let ctx =
            {
                Resources = resource { yield runtimeProvider ; yield tem ; yield task.CancellationTokenSource ; yield dependencies }
                CancellationToken = task.CancellationTokenSource.GetLocalCancellationToken()
            }

        do task.StartTask ctx
        return! TaskExecutionMonitor.AwaitCompletion tem
    }

/// Defines a handle to the state of a runtime instance
/// All information pertaining to the runtime execution state
/// is contained in a single process -- the initializing client.
type RuntimeState =
    {
        /// Reference to the global task queue employed by the runtime
        /// Queue contains pickled task and its vagrant dependency manifest
        TaskQueue : Queue<Pickle<Task> * (*ProcessId*) string * AssemblyId list>
        /// Reference to a Vagrant assembly exporting actor.
        AssemblyExporter : AssemblyExporter
        /// Reference to the runtime resource manager
        /// Used for generating latches, cancellation tokens and result cells.
        ResourceFactory : ResourceFactory
    }
with
    /// Initialize a new runtime state in the local process
    static member FromConfiguration (config : Configuration) =
        {
            TaskQueue = Queue<_>.Init (config.DefaultQueue) |> Async.RunSynchronously
            AssemblyExporter = AssemblyExporter.Init(config.DefaultTableOrContainer) 
            ResourceFactory = ResourceFactory.Init(config) 
        }

    /// <summary>
    ///     Enqueue a cloud workflow with supplied continuations to the runtime task queue.
    /// </summary>
    /// <param name="dependencies">Vagrant dependency manifest.</param>
    /// <param name="cts">Distributed cancellation token source.</param>
    /// <param name="sc">Success continuation</param>
    /// <param name="ec">Exception continuation</param>
    /// <param name="cc">Cancellation continuation</param>
    /// <param name="wf">Workflow</param>
    member rt.EnqueueTask procId dependencies cts sc ec cc (wf : Cloud<'T>) =
        let taskId = guid()
        let startTask ctx =
            let cont = { Success = sc ; Exception = ec ; Cancellation = cc }
            Cloud.StartWithContinuations(wf, cont, ctx)

        let task = 
            { 
                Type = typeof<'T>
                ProcessId = procId
                TaskId = taskId
                StartTask = startTask
                CancellationTokenSource = cts
            }

        let taskp = Pickle.pickle task
        rt.TaskQueue.Enqueue((taskp, procId, dependencies))

    /// <summary>
    ///     Enqueue a batch of cloud workflows with supplied continuations to the runtime task queue.
    ///     Used for Parallel and Choice combinators
    /// </summary>
    /// <param name="dependencies">Vagrant dependency manifest.</param>
    /// <param name="cts">Distributed cancellation token source.</param>
    /// <param name="scFactory">Success continuation factory.</param>
    /// <param name="ec">Exception continuation.</param>
    /// <param name="cc">Cancellation continuation.</param>
    /// <param name="wfs">Workflows</param>
    member rt.EnqueueTaskBatch procId dependencies cts scFactory ec cc (wfs : Cloud<'T> []) =
        let tasks = Array.zeroCreate wfs.Length
        for i = 0 to wfs.Length - 1 do
            let taskId = guid()
            let startTask ctx =
                let cont = { Success = scFactory i ; Exception = ec ; Cancellation = cc }
                Cloud.StartWithContinuations(wfs.[i], cont, ctx)
            let task = 
                { 
                    Type = typeof<'T>
                    ProcessId = procId
                    TaskId = taskId
                    StartTask = startTask
                    CancellationTokenSource = cts
                }

            let taskp = Pickle.pickle task
            tasks.[i] <- (taskp, procId, dependencies)
        rt.TaskQueue.EnqueueBatch(tasks)



    /// <summary>
    ///     Schedules a cloud workflow as a distributed result cell.
    ///     Used for child tasks.
    /// </summary>
    /// <param name="dependencies">Declared workflow dependencies.</param>
    /// <param name="cts">Cancellation token source bound to task.</param>
    /// <param name="wf">Input workflow.</param>
    member rt.StartAsCell procId dependencies cts (wf : Cloud<'T>) = async {
        let! resultCell = rt.ResourceFactory.RequestResultCell<'T>(processIdToStorageId procId)
        let setResult ctx r = 
            async {
                let! success = resultCell.SetResult r
                TaskExecutionMonitor.TriggerCompletion ctx
            } |> TaskExecutionMonitor.ProtectAsync ctx

        let scont ctx t = setResult ctx (Completed t)
        let econt ctx e = setResult ctx (Exception e)
        let ccont ctx c = setResult ctx (Cancelled c)
        do! rt.EnqueueTask procId dependencies cts scont econt ccont wf
        return resultCell
    }

    /// <summary>
    ///     Schedules a cloud workflow as a distributed result cell.
    ///     Used for root-level workflows.
    /// </summary>
    /// <param name="dependencies">Declared workflow dependencies.</param>
    /// <param name="cts">Cancellation token source bound to task.</param>
    /// <param name="wf">Input workflow.</param>
    /// <param name="name">Process name.</param>
    /// <param name="procId">Process id.</param>
    member rt.StartAsProcess procId name dependencies cts (wf : Cloud<'T>) = async {
        let! resultCell = rt.ResourceFactory.RequestResultCell<'T>(processIdToStorageId procId)
        let! _ = rt.ResourceFactory.ProcessMonitor
                   .CreateRecord(
                        procId, name, typeof<'T>,
                        string (cts :> IResource).Uri, 
                        string (resultCell :> IResource).Uri)
        let setResult ctx r = 
            async {
                let! success = resultCell.SetResult r
                let pmon = rt.ResourceFactory.ProcessMonitor
                match r with
                | Completed _ 
                | Exception _ -> do! pmon.SetCompleted(procId)
                | Cancelled _ -> do! pmon.SetKilled(procId)
                TaskExecutionMonitor.TriggerCompletion ctx
            } |> TaskExecutionMonitor.ProtectAsync ctx

        let scont ctx t = setResult ctx (Completed t)
        let econt ctx e = setResult ctx (Exception e)
        let ccont ctx c = setResult ctx (Cancelled c)
        do! rt.EnqueueTask procId dependencies cts scont econt ccont wf
        return resultCell
    }

    
//    member rt.DequeueBatch(count : int) = async {
//        let! items = rt.TaskQueue.ReceiveBatch(count)
//        let ys = Array.zeroCreate items.Length
//        for i = 0 to items.Length - 1 do
//            let (tp, procId, deps) = items.[i]
//            do! rt.AssemblyExporter.LoadDependencies deps
//            let task = Pickle.unpickle tp
//            ys.[i] <- task, procId, deps
//        return ys
//    }

    /// Attempt to dequeue a task from the runtime task queue
    member rt.TryDequeue () = async {
        let! item = rt.TaskQueue.TryDequeue()
        match item with
        | None -> return None
        | Some msg -> 
            let! (tp, procId, deps) = msg.GetPayloadAsync()
            do! rt.AssemblyExporter.LoadDependencies deps
            let task = Pickle.unpickle tp
            return Some (msg, task, procId, deps)
    }