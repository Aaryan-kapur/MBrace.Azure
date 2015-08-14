﻿namespace MBrace.Azure.Runtime

open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Azure
open System
open MBrace.Store.Internals
open MBrace.Azure.Store

type RuntimeId = 
    private { Id : string } with

    interface IRuntimeId with
        member x.Id : string = x.Id

    override x.ToString() = x.Id

    static member FromConfigurationId(config : ConfigurationId) =
        { Id = Convert.ToBase64String(Config.Pickler.Pickle(config)) }

[<AutoSerializable(false)>]
type RuntimeManager private (config : ConfigurationId, uuid : string, logger : ISystemLogger, resources : ResourceRegistry) =
    let runtimeId     = RuntimeId.FromConfigurationId(config)
    do logger.LogInfo "Creating worker manager"
    let workerManager = WorkerManager.Create(config, logger)
    do logger.LogInfo "Creating job manager"
    let jobManager    = JobManager.Create(config, logger)
    do logger.LogInfo "Creating task manager"
    let taskManager   = TaskManager.Create(config, logger)
    do logger.LogInfo "Creating assembly manager"
    let assemblyManager =
        let store = resources.Resolve<CloudFileStoreConfiguration>()
        let serializer = resources.Resolve<ISerializer>()
        StoreAssemblyManager.Create(store, serializer, "vagabond", logger)

    let cancellationEntryFactory = CancellationTokenFactory.Create(config)
    let int32CounterFactory = Int32CounterFactory.Create(config)
    let resultAggregatorFactory = ResultAggregatorFactory.Create(config)

    member this.RuntimeManagerId = uuid
    member this.Resources = resources
    member this.ConfigurationId = config

    member this.ResetCluster(deleteQueues, deleteState, deleteLogs, deleteUserData, force) =
        async {
            if not force then
                let! workers = workerManager.GetAllWorkers()
                if  workers.Length > 0 then
                    let exc = RuntimeException(sprintf "Found %d active workers. Shutdown workers first or 'force' reset." workers.Length)
                    logger.LogError exc.Message
                    return! Async.Raise exc
             
            
            if deleteQueues then 
                logger.LogWarning "Deleting Queues."
                do! Config.DeleteRuntimeQueues(config)
            
            if deleteState then 
                logger.LogWarning "Deleting Container and Table."
                do! Config.DeleteRuntimeState(config)
            
            if deleteLogs then 
                logger.LogWarning "Deleting Logs."
                do! Config.DeleteRuntimeLogs(config)

            if deleteUserData then 
                logger.LogWarning "Deleting UserData."
                do! Config.DeleteUserData(config)
            
            logger.LogInfo "Reactivating configuration."
            let rec loop retryCount = async {
                logger.LogInfof "RetryCount %d." retryCount
                let! step2 = Async.Catch <| Config.ReactivateAsync(config)
                match step2 with
                | Choice1Of2 _ -> 
                    logger.LogInfo "Done."
                | Choice2Of2 ex ->
                    logger.LogWarningf "Failed with %A\nWaiting." ex
                    do! Async.Sleep 10000
                    return! loop (retryCount + 1)
            }
            do! loop 0

            return ()
        }

    member private this.SetLocalWorkerId(workerId : IWorkerId) =
        jobManager.SetLocalWorkerId(workerId)

    interface IRuntimeManager with
        member this.Id                       = runtimeId :> _
        member this.Serializer               = Config.Pickler :> _
        member this.WorkerManager            = workerManager :> _
        member this.TaskManager              = taskManager :> _
        member this.JobQueue                 = jobManager :> _
        member this.AssemblyManager          = assemblyManager :> _
        member this.SystemLogger             = logger
        member this.CancellationEntryFactory = cancellationEntryFactory
        member this.CounterFactory           = int32CounterFactory
        member this.ResetClusterState()      = this.ResetCluster(true, true, true, false, false)
        member this.ResourceRegistry         = resources
        member this.ResultAggregatorFactory  = resultAggregatorFactory
        member this.GetCloudLogger(worker : IWorkerId, job : CloudJob) = 
            let cloudLogger = CloudStorageLogger(config, worker, job.TaskEntry.Id)
            let consoleLogger = new ConsoleLogger(showDate = true)
            
            { new ICloudLogger with
                  member x.Log(entry : string) : unit = 
                      consoleLogger.Log LogLevel.None entry
                      (cloudLogger :> ICloudLogger).Log(entry) }

    static member private GetDefaultResources(config : Configuration, customResources : ResourceRegistry, includeCache : bool) =
        let storeConfig = CloudFileStoreConfiguration.Create(BlobStore.Create(config.StorageConnectionString), config.UserDataContainer)
        let atomConfig = CloudAtomConfiguration.Create(AtomProvider.Create(config.StorageConnectionString), config.UserDataTable)
        let dictionaryConfig = CloudDictionaryProvider.Create(config.StorageConnectionString)
        let channelConfig = CloudChannelConfiguration.Create(ChannelProvider.Create(config.ServiceBusConnectionString))

        resource {
            yield storeConfig
            yield atomConfig
            yield dictionaryConfig
            yield channelConfig
            yield Config.Serializer
            if includeCache then 
                match customResources.TryResolve<Func<IObjectCache>>() with
                | None -> yield MBrace.Runtime.Store.InMemoryCache.Create()
                | Some factory -> yield factory.Invoke()
            yield! customResources
        }

    static member CreateForWorker(config : Configuration, workerId : IWorkerId, customLogger : ISystemLogger, customResources) =
        customLogger.LogInfof "Activating configuration with Id %A" config.Id
        let config = config.WithAppendedId
        Config.Activate(config, true)
        customLogger.LogInfof "Creating resources"
        let resources = RuntimeManager.GetDefaultResources(config, customResources, true)
        customLogger.LogInfof "Creating RuntimeManager for Worker %A" workerId
        let runtime = new RuntimeManager(config.ConfigurationId, workerId.Id, customLogger, resources)
        runtime.SetLocalWorkerId(workerId)
        runtime

    static member CreateForAppDomain(config : Configuration, workerId : IWorkerId, customLogger : ISystemLogger, customResources) =
        customLogger.LogInfof "Activating configuration with Id %A" config.Id
        let config = config.WithAppendedId
        Config.Activate(config, false)
        customLogger.LogInfof "Creating resources"
        let resources = RuntimeManager.GetDefaultResources(config, customResources, true)
        customLogger.LogInfof "Creating RuntimeManager for AppDomain %A" AppDomain.CurrentDomain.FriendlyName
        let runtime = new RuntimeManager(config.ConfigurationId, workerId.Id, customLogger, resources)
        runtime

    static member CreateForClient(config : Configuration, clientId : string, customLogger : ISystemLogger, customResources) =
        customLogger.LogInfof "Activating configuration with Id %A" config.Id
        let config = config.WithAppendedId
        Config.Activate(config, true)
        customLogger.LogInfof "Creating resources"        
        let resources = RuntimeManager.GetDefaultResources(config, customResources, false)
        customLogger.LogInfof "Creating RuntimeManager for Client %A" clientId
        let runtime = new RuntimeManager(config.ConfigurationId, clientId, customLogger, resources)
        runtime