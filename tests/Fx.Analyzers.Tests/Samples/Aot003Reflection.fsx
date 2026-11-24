// AOT003: runtime reflection, dynamic code and reflection-based serializers.
open System
open System.Reflection
open Microsoft.FSharp.Reflection

type Shape =
    | Circle of radius: float
    | Square of side: float

let methods = typeof<Shape>.GetMethods() // expect: AOT003
let properties = (box 1).GetType().GetProperties() // expect: AOT003
let byName = Type.GetType "System.Int32" // expect: AOT003
let created = Activator.CreateInstance<obj>() // expect: AOT003
let assembly = Assembly.GetExecutingAssembly() // expect: AOT003
let containing = typeof<Shape>.Assembly // expect: AOT003
let isUnion = FSharpType.IsUnion typeof<Shape> // expect: AOT003
let fields = FSharpValue.GetUnionFields(Circle 1.0, typeof<Shape>) // expect: AOT003
let invoke (m: MethodInfo) = m.Invoke(null, [||]) // expect: AOT003
let dynamicInvoke (d: Delegate) = d.DynamicInvoke() // expect: AOT003
let quoted = <@ 1 + 1 @> // expect: AOT003
let json = Text.Json.JsonSerializer.Serialize(1) // expect: AOT003
let loadContext = Runtime.Loader.AssemblyLoadContext.Default // expect: AOT003
let days = Enum.GetValues typeof<DayOfWeek> // expect: AOT003
