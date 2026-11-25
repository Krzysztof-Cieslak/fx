/// The TypeScript SDK's generated files are the generator's output, and its package is named from the
/// product name.
module Fx.Sdk.Process.Tests.GeneratedTests

open System.Diagnostics
open System.IO
open Expecto
open Fx
open Fx.Contracts

let private packageJson (directory: string) : Json =
    match Json.parse (File.ReadAllText(Repo.path [ "packages"; directory; "package.json" ])) with
    | Ok json -> json
    | Error e -> failtest $"packages/{directory}/package.json: {e}"

[<Tests>]
let tests =
    testList
        "Generated TypeScript"
        [
            test "the SDK's contract types are generated from the codecs" {
                Repo.expectGenerated
                    [ "packages"; "sdk"; "src"; "generated"; "contracts.d.ts" ]
                    (SdkDeclarations.renderContracts ())
            }

            test "the SDK's product constants are generated from the naming constant" {
                Repo.expectGenerated
                    [ "packages"; "sdk"; "src"; "generated"; "product.ts" ]
                    (SdkDeclarations.renderProduct ())
            }

            test "the SDK's package is named from the naming constant" {
                let json = packageJson "sdk"
                Expect.equal (Json.tryField "name" json) (Some(Json.String(Product.npmPackage "sdk"))) "name"
                Expect.equal (Json.tryField "version" json) (Some(Json.String Product.Version)) "version"
            }

            test "the F# SDK's packages are named from the naming constant" {
                for name in [ "Sdk"; "Sdk.Process" ] do
                    let project = Repo.path [ "src"; "Fx." + name; "Fx." + name + ".fsproj" ]

                    let info =
                        ProcessStartInfo("dotnet", [ "msbuild"; project; "-getProperty:PackageId" ])

                    info.RedirectStandardOutput <- true
                    info.RedirectStandardError <- true
                    info.Environment["DOTNET_NOLOGO"] <- "1"

                    use proc =
                        match Process.Start info with
                        | null -> failtest "dotnet did not start"
                        | p -> p

                    let output = proc.StandardOutput.ReadToEnd()
                    proc.WaitForExit()
                    Expect.equal proc.ExitCode 0 $"dotnet msbuild {name}: {proc.StandardError.ReadToEnd()}"
                    Expect.equal (output.Trim()) (Product.nugetPackage name) name
            }
        ]
