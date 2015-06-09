﻿namespace WebSharper.Warp.Test

open WebSharper

type Action =
    | [<CompiledName "">] Index

module Client =

    open WebSharper.Html.Client

    [<JavaScript>]
    let Content () =
        Button [Text "Click me!"]

module Server =

    open WebSharper.Sitelets
    open WebSharper.Html.Server
    open WebSharper.Warp

    let Sitelet =
        Application.Create (function
            | Action.Index ->
                Content.PageContent <| fun _ ->
                    { Page.Default with
                        Body =
                            [
                                Div [ClientSide <@ Client.Content () @>]
                            ]}
        )

    [<EntryPoint>]
    let main argv = 
        Application.Run(Sitelet)
        0 // return an integer exit code