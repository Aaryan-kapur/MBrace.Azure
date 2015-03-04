﻿namespace MBrace.Azure.Runtime.Resources

open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Common
open MBrace.Azure.Runtime.Resources
open Nessos.Vagabond
open System
open System.Runtime.Serialization
open MBrace.Runtime.Vagabond
open MBrace.Azure
open MBrace.Runtime

type AssemblyManager private (config : ConfigurationId, logger : ICloudLogger) = 
    
    let filename id = sprintf "%s-%s" id.FullName (Convert.toBase32String id.ImageHash)
    let prefix = "assemblies"
    let uploadPkg (pkg : AssemblyPackage) = 
        async { 
            let file =  filename pkg.Id
            let! exists = Blob<_>.Exists(config, prefix, file)
            if not exists then
                let imgSize = 
                    match pkg.Image with
                    | Some i -> sprintf "[%d bytes]" i.Length
                    | None -> String.Empty
                logger.Logf "Uploading file %s %s" pkg.FullName imgSize
                return! Blob.CreateIfNotExists(config, prefix, file, fun () -> pkg) |> Async.Ignore
        }
    
    let downloadPkg (id : AssemblyId) : Async<AssemblyPackage> = 
        async { 
            let file = filename id
            logger.Logf "Downloading file %s" id.FullName
            let blob = Blob.FromPath(config, prefix, file)
            return! blob.GetValue()
        }
    
    member __.UploadDependencies(ids : AssemblyId list) = 
        async { 
            let pkgs = VagabondRegistry.Instance.CreateAssemblyPackages(ids, includeAssemblyImage = true)
            do! pkgs
                |> Seq.map uploadPkg
                |> Async.Parallel
                |> Async.Ignore
        }
    
    member __.LoadDependencies(ids : AssemblyId list) = 
        async { 
            let publisher = 
                { new IRemoteAssemblyPublisher with
                      member __.GetRequiredAssemblyInfo() = async.Return ids
                      member __.PullAssemblies ids = 
                          async { 
                              let! pkgs = ids
                                          |> Seq.map downloadPkg
                                          |> Async.Parallel
                              return pkgs |> Seq.toList
                          } }
            do! VagabondRegistry.Instance.ReceiveDependencies publisher
        }
    
    member __.ComputeDependencies(graph : 'T) = 
        VagabondRegistry.Instance.ComputeObjectDependencies(graph, permitCompilation = true) 
        |> List.map Utilities.ComputeAssemblyId

    static member Create(config, logger) = 
        new AssemblyManager(config, logger)

    