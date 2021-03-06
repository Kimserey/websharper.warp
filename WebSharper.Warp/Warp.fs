﻿namespace WebSharper

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading.Tasks
open WebSharper.Sitelets
open global.Owin
open Microsoft.Owin.Hosting
open Microsoft.Owin.StaticFiles
open Microsoft.Owin.FileSystems
open WebSharper
open WebSharper.Owin
open WebSharper.Html.Server

[<AutoOpen>]
module Extensions =
    open WebSharper.Html.Server

    type Sitelets.Page with
        member this.WithStyleSheet (href: string) =
            let css = Link [Rel "stylesheet"; HRef href]
            { this with
                Head = Seq.append this.Head [css]
            }

        member this.WithJavaScript (href: string) =
            let css = Script [Type "text/javascript"; Src href]
            { this with
                Head = Seq.append this.Head [css]
            }

module SPA =
    type EndPoint =
        | [<EndPoint "GET /">] Home
    
module internal Compilation =

    module PC = WebSharper.PathConventions

    open System.Reflection
    open IntelliFactory.Core
    module FE = WebSharper.Compiler.FrontEnd

    type CompiledAssembly =
        {
            ReadableJavaScript : string
            CompressedJavaScript : string
            Info : WebSharper.Core.Metadata.Info
            References : list<WebSharper.Compiler.Assembly>
        }

    let websharperDir = lazy (Path.GetDirectoryName typeof<Sitelet<_>>.Assembly.Location)

    let getRefs (loader: Compiler.Loader) =
        [
            for dll in Directory.EnumerateFiles(websharperDir.Value, "*.dll") do
                if Path.GetFileName(dll) <> "FSharp.Core.dll" then
                    yield dll
            let dontRef (n: string) =
                [
                    "FSharp.Compiler.Interactive.Settings,"
                    "FSharp.Compiler.Service,"
                    "FSharp.Core,"
                    "FSharp.Data.TypeProviders,"
                    "Mono.Cecil"
                    "mscorlib,"
                    "System."
                    "System,"
                ] |> List.exists n.StartsWith
            let rec loadRefs (asms: Assembly[]) (loaded: Map<string, Assembly>) =
                let refs =
                    asms
                    |> Seq.collect (fun asm -> asm.GetReferencedAssemblies())
                    |> Seq.map (fun n -> n.FullName)
                    |> Seq.distinct
                    |> Seq.filter (fun n -> not (dontRef n || Map.containsKey n loaded))
                    |> Seq.choose (fun n ->
                        try Some (AppDomain.CurrentDomain.Load n)
                        with _ -> None)
                    |> Array.ofSeq
                if Array.isEmpty refs then
                    loaded
                else
                    (loaded, refs)
                    ||> Array.fold (fun loaded r -> Map.add r.FullName r loaded)
                    |> loadRefs refs
            let asms =
                AppDomain.CurrentDomain.GetAssemblies()
                |> Array.append (AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies())
                |> Array.filter (fun a -> not (dontRef a.FullName))
            yield! asms
            |> Array.map (fun asm -> asm.FullName, asm)
            |> Map.ofArray
            |> loadRefs asms
            |> Seq.choose (fun (KeyValue(n, asm)) ->
                try Some asm.Location
                with :? NotSupportedException ->
                    // The dynamic assembly does not support `.Location`.
                    // No problem, if it's from the dynamic assembly then
                    // it doesn't incur a dependency anyway.
                    None)
        ]
        |> Seq.distinctBy Path.GetFileName
        |> Seq.map loader.LoadFile
        |> Seq.toList

    let getLoader() =
        let localDir = Directory.GetCurrentDirectory()
        let fsharpDir = Path.GetDirectoryName typeof<option<_>>.Assembly.Location
        let loadPaths =
            [
                localDir
                websharperDir.Value
                fsharpDir
            ]
        let aR =
            AssemblyResolver.Create()
                .SearchDirectories(loadPaths)
                .WithBaseDirectory(fsharpDir)
        FE.Loader.Create aR stderr.WriteLine

    let compile (asm: System.Reflection.Assembly) =
        let loader = getLoader()
        let refs = getRefs loader
        let opts = { FE.Options.Default with References = refs }
        let compiler = FE.Prepare opts (eprintfn "%O")
        compiler.Compile(asm)
        |> Option.map (fun asm ->
            {
                ReadableJavaScript = asm.ReadableJavaScript
                CompressedJavaScript = asm.CompressedJavaScript
                Info = asm.Info
                References = refs
            }
        )

    let getCompiled (asm: System.Reflection.Assembly) =
        let loader = getLoader()
        let refs = getRefs loader
        refs
        |> List.tryFind (fun r -> r.FullName = asm.FullName)
        |> Option.bind (fun r ->
            match r.ReadableJavaScript, r.CompressedJavaScript with
            | Some readable, Some compressed ->
                let opts = { FE.Options.Default with References = refs }
                let compiler = FE.Prepare opts (eprintfn "%O")
                Some {
                    ReadableJavaScript = readable
                    CompressedJavaScript = compressed
                    Info = compiler.GetInfo()
                    References = refs
                }
            | _ ->
                eprintfn "Assembly not compiled. When using Scripted = false, the application must be precompiled."
                None)

    let outputFiles root (refs: Compiler.Assembly list) =
        let pc = PC.PathUtility.FileSystem(root)
        let writeTextFile path contents =
            Directory.CreateDirectory (Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, contents)
        let writeBinaryFile path contents =
            Directory.CreateDirectory (Path.GetDirectoryName path) |> ignore
            File.WriteAllBytes(path, contents)
        let emit text path =
            match text with
            | Some text -> writeTextFile path text
            | None -> ()
        let script = PC.ResourceKind.Script
        let content = PC.ResourceKind.Content
        for a in refs do
            let aid = PC.AssemblyId.Create(a.FullName)
            emit a.ReadableJavaScript (pc.JavaScriptPath aid)
            emit a.CompressedJavaScript (pc.MinifiedJavaScriptPath aid)
            let writeText k fn c =
                let p = pc.EmbeddedPath(PC.EmbeddedResource.Create(k, aid, fn))
                writeTextFile p c
            let writeBinary k fn c =
                let p = pc.EmbeddedPath(PC.EmbeddedResource.Create(k, aid, fn))
                writeBinaryFile p c
            for r in a.GetScripts() do
                writeText script r.FileName r.Content
            for r in a.GetContents() do
                writeBinary content r.FileName (r.GetContentData())

    let (+/) x y = Path.Combine(x, y)

    let outputFile root (asm: CompiledAssembly) =
        let dir = root +/ "Scripts" +/ "WebSharper"
        Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(dir +/ "WebSharper.EntryPoint.js", asm.ReadableJavaScript)
        File.WriteAllText(dir +/ "WebSharper.EntryPoint.min.js", asm.CompressedJavaScript)

type WarpApplication<'EndPoint when 'EndPoint : equality> = Sitelet<'EndPoint>

[<Extension>]
module Owin =

    type WarpOptions<'EndPoint when 'EndPoint : equality>(assembly, ?debug, ?rootDir, ?scripted) =
        let debug = defaultArg debug false
        let rootDir = defaultArg rootDir (Directory.GetCurrentDirectory())
        let scripted = defaultArg scripted true

        member this.Assembly = assembly
        member this.Debug = debug
        member this.RootDir = rootDir
        member this.Scripted = scripted

    type WarpMiddleware<'EndPoint when 'EndPoint : equality>(next: Owin.AppFunc, sitelet: WarpApplication<'EndPoint>, options: WarpOptions<'EndPoint>) =
        let exec =
            // Compile only when the app starts and not on every request.
            let compile =
                if options.Scripted then
                    Compilation.compile
                else
                    Compilation.getCompiled
            match compile options.Assembly with
            | None -> failwith "Failed to compile with WebSharper"
            | Some asm ->
                Compilation.outputFiles options.RootDir asm.References
                Compilation.outputFile options.RootDir asm
                // OWIN middleware form a Russian doll, innermost -> outermost
                let siteletMw =
                    WebSharper.Owin.SiteletMiddleware(
                        next, 
                        Options.Create(asm.Info)
                            .WithServerRootDirectory(options.RootDir)
                            .WithDebug(options.Debug),
                        sitelet)
                let staticFilesMw =
                    Microsoft.Owin.StaticFiles.StaticFileMiddleware(
                        AppFunc siteletMw.Invoke,
                        StaticFileOptions(
                            FileSystem = PhysicalFileSystem(options.RootDir)))
                staticFilesMw.Invoke

        member this.Invoke(env: IDictionary<string, obj>) = exec env

    /// Adds the Warp middleware to an Owin.IAppBuilder pipeline.
    [<Extension>]
    let UseWarp(appB: Owin.IAppBuilder, sitelet: WarpApplication<'EndPoint>, options) =
        Owin.MidFunc(fun next ->
            let mw = WarpMiddleware<_>(next, sitelet, options)
            Owin.AppFunc mw.Invoke)
        |> appB.Use

#if NO_UINEXT
#else
open WebSharper.UI.Next
open WebSharper.UI.Next.Server
#endif

[<Extension>]
type Warp internal (urls: list<string>, stop: unit -> unit) =

    member this.Urls = urls

    member this.Stop() = stop()

    interface System.IDisposable with
        member this.Dispose() = stop()

    [<Extension>]
    static member Run(sitelet: WarpApplication<'EndPoint>, ?debug, ?urls, ?rootDir, ?scripted, ?assembly) =
        let assembly =
            match assembly with
            | Some a -> a
            | None -> Assembly.GetCallingAssembly()
        let urls = defaultArg urls ["http://localhost:9000/"]
        let urls = urls |> List.map (fun url -> if not (url.EndsWith "/") then url + "/" else url)
        let options = Owin.WarpOptions<_>(assembly, ?debug = debug, ?rootDir = rootDir, ?scripted = scripted)
        try
            let startOptions = new StartOptions()
            urls |> List.iter startOptions.Urls.Add
            let server = WebApp.Start(startOptions, fun appB ->
                Owin.UseWarp(appB, sitelet, options)
                    .Use(fun ctx next ->
                        Task.Factory.StartNew(fun () ->
                            let s : Stream =
                                ctx .Set("owin.ResponseStatusCode", 404)
                                    .Set("owin.ResponseReasonPhrase", "Not Found")
                                    .Get("owin.ResponseBody")
                            use w = new StreamWriter(s)
                            w.Write("Page not found")))
                |> ignore)
            new Warp(urls, server.Dispose)
        with e ->
            failwithf "Error starting website:\n%s" (e.ToString())

    [<Extension>]
    static member RunAndWaitForInput(app: WarpApplication<'EndPoint>, ?debug, ?urls, ?rootDir, ?scripted, ?assembly) =
        try
            let assembly =
                match assembly with
                | Some a -> a
                | None -> Assembly.GetCallingAssembly()
            let warp = Warp.Run(app, ?debug = debug, ?urls = urls, ?rootDir = rootDir, ?scripted = scripted, assembly = assembly)
            stdout.WriteLine("Serving {0}, press Enter to stop.", warp.Urls)
            stdin.ReadLine() |> ignore
            warp.Stop()
            0
        with e ->
            eprintfn "%A" e
            1

    static member Page (?Body: #seq<Element>, ?Head: #seq<Element>, ?Title: string, ?Doctype: string) =
        Content.Page(?Body = Body, ?Doctype = Doctype, ?Head = Head, ?Title = Title)

#if NO_UINEXT
#else
    static member Doc (doc: Doc) = Content.Page doc

    static member Doc (?Body: #seq<Doc>, ?Head: #seq<Doc>, ?Title: string, ?Doctype: string) =
        Content.Page(?Body = Body, ?Doctype = Doctype, ?Head = Head, ?Title = Title)
#endif

    static member Json data =
        Content.Json data

    static member CreateApplication(f: Context<'EndPoints> -> 'EndPoints -> Async<Content<'EndPoints>>) : WarpApplication<'EndPoints> =
        Sitelet.Infer f

    static member CreateSPA (f: Context<SPA.EndPoint> -> #seq<Element>) =
        Warp.CreateApplication (fun ctx SPA.EndPoint.Home ->
            Warp.Page(Body = f ctx)
        )

    static member CreateSPA (f: Context<SPA.EndPoint> -> Async<Content<SPA.EndPoint>>) =
        Warp.CreateApplication (fun ctx SPA.EndPoint.Home ->
            f ctx
        )

    static member Text out =
        Warp.CreateApplication (fun ctx SPA.EndPoint.Home ->
            Warp.Page(Body = [Text out])
        )


type ClientAttribute = WebSharper.Pervasives.JavaScriptAttribute
type ServerAttribute = WebSharper.Pervasives.RpcAttribute
type EndPointAttribute = Sitelets.EndPointAttribute
type MethodAttribute = Sitelets.MethodAttribute
type JsonAttribute = Sitelets.JsonAttribute
type QueryAttribute = Sitelets.QueryAttribute
type FormDataAttribute = Sitelets.FormDataAttribute
type WildCardAttribute = Sitelets.WildcardAttribute
