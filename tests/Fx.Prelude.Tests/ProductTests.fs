module Fx.Prelude.Tests.ProductTests

open System.Text.RegularExpressions
open Expecto
open Fx

let private name = Product.Name

[<Tests>]
let tests =
    testList
        "Product"
        [
            test "name is usable as a JS identifier, npm scope, file name and env prefix" {
                Expect.isTrue (Regex.IsMatch(name, "^[a-z][a-z0-9]*$")) $"invalid product name '{name}'"
            }

            test "version is set" { Expect.isNotEmpty Product.Version "version" }

            test "names derive from the product name" {
                Expect.equal Product.binaryName name "binary"
                Expect.equal Product.kernelGlobal name "kernel global"
                Expect.equal Product.protocolNamespace name "protocol namespace"
                Expect.equal Product.projectDirName ("." + name) "project dir"
                Expect.equal Product.homeDirName ("." + name) "home dir"
                Expect.equal Product.windowsDataDirName name "windows data dir"
                Expect.equal Product.componentsLockFileName (name + ".components.lock") "lockfile"
            }

            test "environment variables use the upper-case prefix" {
                Expect.equal Product.envPrefix (name.ToUpperInvariant() + "_") "prefix"
                Expect.equal (Product.envVar "HOME") (name.ToUpperInvariant() + "_HOME") "variable"
            }

            test "package and extension ids use the product name" {
                let pascal = name.Substring(0, 1).ToUpperInvariant() + name.Substring 1
                Expect.equal Product.pascalName pascal "pascal"
                Expect.equal (Product.npmPackage "sdk") ("@" + name + "/sdk") "npm"
                Expect.equal (Product.nugetPackage "Sdk") (pascal + ".Sdk") "nuget"
                Expect.equal (Product.extensionId "env.local") (name + ".env.local") "extension"
            }
        ]
