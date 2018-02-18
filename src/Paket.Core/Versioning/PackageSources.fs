﻿module Paket.PackageSources 

open System
open System.IO
open System.Text.RegularExpressions

open Paket.Logging
open Chessie.ErrorHandling

open Newtonsoft.Json
open System.Threading.Tasks

let private envVarRegex = Regex("^%(\w*)%$", RegexOptions.Compiled)

type EnvironmentVariable = 
    { Variable : string
      Value    : string }

    static member Create(variable) = 
        if envVarRegex.IsMatch(variable) then
            let trimmed = envVarRegex.Match(variable).Groups.[1].Value
            match Environment.GetEnvironmentVariable(trimmed) with
            | null ->
                traceWarnfn "environment variable '%s' not found" variable
                Some { Variable = variable; Value = ""}
            | expanded ->
                Some { Variable = variable; Value = expanded }
        else
            None

[<StructuredFormatDisplay("{AsString}")>]
type NugetSourceAuthentication = 
    | PlainTextAuthentication of username : string * password : string * authType : Utils.AuthType
    | EnvVarAuthentication of usernameVar : EnvironmentVariable * passwordVar : EnvironmentVariable * authType : Utils.AuthType
    | ConfigAuthentication of username : string * password : string * authType : Utils.AuthType
        with
            override x.ToString() =
                match x with
                    | PlainTextAuthentication(u,_,t) -> sprintf "PlainTextAuthentication (username = %s, password = ***, authType = %A)" u t
                    | EnvVarAuthentication(u,_,t) ->  sprintf "EnvVarAuthentication (usernameVar = %s, passwordVar = ***, authType = %A)" u.Variable t
                    | ConfigAuthentication(u,_,t) -> sprintf "ConfigAuthentication (username = %s, password = ***, authType = %A)" u t
            member x.AsString = x.ToString()

let toCredentials = function
    | PlainTextAuthentication(username,password,authType) ->
        Credentials(username, password, authType)
    | ConfigAuthentication(username, password,authType) ->
        Credentials(username, password, authType)
    | EnvVarAuthentication(usernameVar, passwordVar, authType) -> 
        Credentials(usernameVar.Value, passwordVar.Value, authType)

let tryParseWindowsStyleNetworkPath (path : string) =
    let trimmed = path.TrimStart()
    if (isUnix || isMacOS) && trimmed.StartsWith @"\\" then
        trimmed.Replace('\\', '/') |> sprintf "smb:%s" |> Some
    else None

let RemoveOutsideQuotes(path : string) =
    let trimChars = [|'\"'|]
    path.Trim(trimChars)

let urlSimilarToTfsOrVsts url =
    String.containsIgnoreCase "visualstudio.com" url || (String.containsIgnoreCase "/_packaging/" url && String.containsIgnoreCase "/nuget/v" url)

let urlIsNugetGallery url =
    String.containsIgnoreCase "nuget.org" url

let urlIsMyGet url =
    String.containsIgnoreCase "myget.org" url

type KnownNuGetSources =
    | OfficialNuGetGallery
    | TfsOrVsts
    | MyGet
    | UnknownNuGetServer


type NugetSource = 
    { Url : string
      Authentication : NugetSourceAuthentication option }
    member x.BasicAuth =
        x.Authentication |> Option.map toCredentials

type NugetV3Source = NugetSource

let userNameRegex = Regex("username[:][ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
let passwordRegex = Regex("password[:][ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
let authTypeRegex = Regex("authtype[:][ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let internal parseAuth(text:string, source) =
    let getAuth() = ConfigFile.GetAuthentication source |> Option.map (function Credentials(username, password, authType) -> ConfigAuthentication(username, password, authType) | _ -> ConfigAuthentication("","",AuthType.Basic))
    if text.Contains("username:") || text.Contains("password:") then
        if not (userNameRegex.IsMatch(text) && passwordRegex.IsMatch(text)) then 
            failwithf "Could not parse auth in \"%s\"" text

        let username = userNameRegex.Match(text).Groups.[1].Value
        let password = passwordRegex.Match(text).Groups.[1].Value

        let authType = 
            if (authTypeRegex.IsMatch(text))
            then authTypeRegex.Match(text).Groups.[1].Value |> Utils.parseAuthTypeString
            else Utils.AuthType.Basic

        let auth = 
            match EnvironmentVariable.Create(username),
                  EnvironmentVariable.Create(password) with
            | Some userNameVar, Some passwordVar ->
                EnvVarAuthentication(userNameVar, passwordVar, authType) 
            | _, _ -> 
                PlainTextAuthentication(username, password, authType)

        match toCredentials auth with
        | Credentials(username, password, _) when username = "" && password = "" -> getAuth()
        | _ -> Some auth
    else
        getAuth()

/// Represents the package source type.
type PackageSource =
| NuGetV2 of NugetSource
| NuGetV3 of NugetV3Source
| LocalNuGet of string * Cache option
    override this.ToString() =
        match this with
        | NuGetV2 source -> source.Url
        | NuGetV3 source -> source.Url
        | LocalNuGet(path,_) -> path
    member x.NuGetType =
        match x.Url with
        | _ when urlIsNugetGallery x.Url -> KnownNuGetSources.OfficialNuGetGallery
        | _ when urlIsMyGet x.Url -> KnownNuGetSources.MyGet
        | _ when urlSimilarToTfsOrVsts x.Url -> KnownNuGetSources.TfsOrVsts
        | _ -> KnownNuGetSources.UnknownNuGetServer
    static member Parse(line : string) =
        let sourceRegex = Regex("source[ ]*[\"]([^\"]*)[\"]", RegexOptions.IgnoreCase)
        let parts = line.Split ' '
        let source = 
            if sourceRegex.IsMatch line then
                sourceRegex.Match(line).Groups.[1].Value.TrimEnd([| '/' |])
            else
                parts.[1].Replace("\"","").TrimEnd([| '/' |])

        let feed = normalizeFeedUrl source
        PackageSource.Parse(feed, parseAuth(line, feed))

    static member Parse(source,auth) = 
        match tryParseWindowsStyleNetworkPath source with
        | Some path -> PackageSource.Parse(path)
        | _ ->
            match System.Uri.TryCreate(source, System.UriKind.Absolute) with
            | true, uri ->
#if DOTNETCORE
                if uri.Scheme = "file" then 
#else
                if uri.Scheme = System.Uri.UriSchemeFile then 
#endif
                    LocalNuGet(source,None)
                else 
                    if String.endsWithIgnoreCase "v3/index.json" source then
                        NuGetV3 { Url = source; Authentication = auth }
                    else
                        NuGetV2 { Url = source; Authentication = auth }
            | _ ->  match System.Uri.TryCreate(source, System.UriKind.Relative) with
                    | true, uri -> LocalNuGet(source,None)
                    | _ -> failwithf "unable to parse package source: %s" source

    member this.Url = 
        match this with
        | NuGetV2 n -> n.Url
        | NuGetV3 n -> n.Url
        | LocalNuGet(n,_) -> n

    member this.IsLocalFeed = 
        match this with
        | LocalNuGet(n,_) -> true
        | _ -> false

    member this.Auth = 
        match this with
        | NuGetV2 n -> n.Authentication
        | NuGetV3 n -> n.Authentication
        | LocalNuGet(n,_) -> None

    static member NuGetV2Source url = NuGetV2 { Url = url; Authentication = None }
    static member NuGetV3Source url = NuGetV3 { Url = url; Authentication = None }

    static member FromCache (cache:Cache) = LocalNuGet(cache.Location,Some cache)

    static member WarnIfNoConnection (source,_) = 
        let n url auth =
            use client = Utils.createHttpClient(url, auth |> Option.map toCredentials)
            try 
                client.DownloadData url |> ignore 
            with _ ->
                traceWarnfn "Unable to ping remote NuGet feed: %s." url
        match source with
        | NuGetV2 x -> n x.Url x.Authentication
        | NuGetV3 x -> n x.Url x.Authentication
        | LocalNuGet(path,_) -> 
            if not (Directory.Exists (RemoveOutsideQuotes path)) then 
                traceWarnfn "Local NuGet feed doesn't exist: %s." path

let DefaultNuGetSource = PackageSource.NuGetV2Source Constants.DefaultNuGetStream


type NugetPackage = {
    Id : string
    VersionRange : VersionRange
    Kind : NugetPackageKind
    TargetFramework : string option
}
and [<RequireQualifiedAccess>] NugetPackageKind =
    | Package
    | DotnetCliTool
