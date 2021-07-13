module Tests.Undo

open System.Threading.Tasks
open FSharp.Control.Tasks

open Expecto

open Prelude
open Tablecloth
open TestUtils

module Canvas = LibBackend.Canvas
module PT = LibExecution.ProgramTypes
module RT = LibExecution.RuntimeTypes
module Exe = LibExecution.Execution
module S = PT.Shortcuts

let hop (h : PT.Handler.T) = PT.SetHandler(h.tlid, h.pos, h)

let handler code = testHttpRouteHandler "" "GET" (FSharpToExpr.parsePTExpr code)

let testUndoFns : Test =
  test "test undo functions" {
    let tlid = 7UL
    let n1 = PT.TLSavepoint tlid
    let n2 = hop (handler "blank - blank")
    let n3 = hop (handler "blank - 3")
    let n4 = hop (handler "3 - 4")
    let u = PT.UndoTL tlid
    let ops = [ n1; n1; n1; n1; n2; n3; n4; u; u; u ]
    Expect.equal (LibBackend.Undo.undoCount ops tlid) 3 "undocount"
  }


let testUndo : Test =
  testTask "test undo" {
    do! clearCanvasData (CanvasName.create "test-undo")
    let tlid = 7UL
    let ha code = hop ({ handler code with tlid = tlid })
    let sp = PT.TLSavepoint tlid
    let u = PT.UndoTL tlid
    let r = PT.RedoTL tlid
    let ops = [ sp; ha "1"; sp; ha "2"; sp; ha "3"; sp; ha "4"; sp; ha "5" ]

    let exe (ops : PT.Oplist) =
      task {
        let! meta = testCanvasInfo "test-undo"
        let! c = Canvas.fromOplist meta ops
        let! state = executionStateFor "test-undo" Map.empty Map.empty
        let h = Map.get tlid c.handlers |> Option.unwrapUnsafe
        return! Exe.executeExpr state Map.empty (h.ast.toRuntimeType ())
      }

    let! v = exe ops
    Expect.equal v (RT.DInt 5I) "check assumptions"

    let! v = exe (ops @ [ u ])
    Expect.equal v (RT.DInt 4I) "first undo"

    let! v = exe (ops @ [ u; u ])
    Expect.equal v (RT.DInt 3I) "second undo"

    let! v = exe (ops @ [ u; u; r ])
    Expect.equal v (RT.DInt 4I) "2 undos and a redo"

    let! v = exe (ops @ [ u; u; r; r ])
    Expect.equal v (RT.DInt 5I) "2 undos and 2 redos"

    let! v = exe (ops @ [ u; u; r; r; u ])
    Expect.equal v (RT.DInt 4I) "2 undos and 2 redos, then another undo"

    let! v = exe (ops @ [ u; u; r; r; u; r ])
    Expect.equal v (RT.DInt 5I) "2 undos and 2 redos, then another undo + redo"
  }

let testCanvasVerificationUndoRenameDupedName : Test =
  testTask "verification triggers in undo/redo case" {
    let dbID = gid ()
    let nameID = gid ()
    let dbID2 = gid ()
    let nameID2 = gid ()
    let pos = { x = 0; y = 0 }
    let! meta = testCanvasInfo "test-undo-verification"

    let ops1 =
      [ PT.CreateDBWithBlankOr(dbID, pos, nameID, "Books")
        PT.TLSavepoint dbID
        PT.DeleteTL dbID
        PT.CreateDBWithBlankOr(dbID2, pos, nameID2, "Books") ]

    let! c = Canvas.fromOplist meta ops1
    Expect.isOk (Canvas.verify c) "should initially verify"

    let ops2 = ops1 @ [ PT.UndoTL dbID ]
    let! c = Canvas.fromOplist meta ops2
    Expect.isError (Canvas.verify c) "should fail to verify"
  }

let tests =
  testList
    "undo"
    [ testUndoFns; testUndo; testCanvasVerificationUndoRenameDupedName ]
