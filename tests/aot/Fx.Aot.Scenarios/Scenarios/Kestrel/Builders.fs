/// `WebApplication.CreateSlimBuilder` with endpoint routing (`MapGet`/`MapPost` with RequestDelegate
/// values) under Native AOT, next to `CreateEmptyBuilder`, which LocalServer uses: the slim builder
/// reads appsettings.json from the content root, whose Kestrel:Endpoints add listeners.
module Fx.Aot.Scenarios.Kestrel.Builders

open System
open System.IO
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support

/// What a cloned repository could contain: a listener on every interface, if the port were not 0.
let private appSettings =
    """{ "Kestrel": { "Endpoints": { "FromAppSettings": { "Url": "http://127.0.0.1:0" } } } }"""

/// Builds the app with routing and RequestDelegate endpoints, listens on 127.0.0.1:0 in code, and
/// reports the listeners and a few requests.
let private run (label: string) (builder: WebApplicationBuilder) : Task<string list> =
    task {
        // The slim builder logs to the console ("Now listening on ..."), which would change the output.
        builder.Logging.ClearProviders() |> ignore

        builder.WebHost.ConfigureKestrel(fun kestrel -> kestrel.Listen(IPAddress.Loopback, 0))
        |> ignore

        let app = builder.Build()

        app.MapGet(
            "/items/{id}",
            RequestDelegate(fun context ->
                context.Response.WriteAsync("item " + string context.Request.RouteValues["id"]))
        )
        |> ignore

        app.MapPost(
            "/items",
            RequestDelegate(fun context ->
                context.Response.StatusCode <- 201
                Task.CompletedTask)
        )
        |> ignore

        do! withinTask (app.StartAsync())

        let address =
            app.Urls
            |> Seq.find (fun a -> a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))

        use http = client Tcp (Uri address)
        use! item = within (http.GetAsync "/items/42")
        let! item = describe item
        use! created = within (http.PostAsync("/items", new Net.Http.ByteArrayContent([||])))
        let! created = describe created
        use! missing = within (http.GetAsync "/nothing")
        let! missing = describe missing
        let listeners = app.Urls.Count
        do! app.StopAsync()
        do! app.DisposeAsync()

        return
            [
                $"{label}: listeners = {listeners}"
                $"{label}: GET /items/42 = {item}, POST /items = {created}, GET /nothing = {missing}"
            ]
    }

let slimBuilder =
    Scenario.task "kestrel-slim-builder" (fun () ->
        task {
            let dir = Path.Combine(Path.GetTempPath(), uniqueName "aot-content-")
            Directory.CreateDirectory dir |> ignore

            try
                File.WriteAllText(Path.Combine(dir, "appsettings.json"), appSettings)

                let options () =
                    WebApplicationOptions(ContentRootPath = dir)

                let! slim = run "slim builder" (WebApplication.CreateSlimBuilder(options ()))

                let empty = WebApplication.CreateEmptyBuilder(options ())
                empty.WebHost.UseKestrelCore() |> ignore
                empty.Services.AddRoutingCore() |> ignore
                let! empty = run "empty builder" empty

                return slim @ empty
            finally
                Directory.Delete(dir, true)
        })

let scenarios = [ slimBuilder ]
