module QuickTest

open System
open System.Text.RegularExpressions
open System.Collections.Generic
open Fable.Core

[<Emit("print($0)")>]
let print(x: obj): unit = ()

let equal expected actual =
   let areEqual = expected = actual
   print $"{expected} = {actual} > {areEqual}"
   if not areEqual then
       print $"[ASSERT ERROR] Expected {expected} but got {actual}"
       exn "" |> raise

let testCase (msg: string) f: unit =
    print msg
    f ()
    print ""

let throwsAnyError (f: unit -> 'a): unit =
    let success =
        try
            f() |> ignore
            true
        with e ->
            print $"Got expected error: %s{string e}"
            false
    if success then
        print "[ERROR EXPECTED]"

let main() =
    testCase "Array.length works" <| fun () ->
        let xs = [|"a"; "a"; "a"; "a"|]
        Array.length xs |> equal 4
