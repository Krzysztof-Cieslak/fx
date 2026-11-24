// EFF009: SQLite outside a storage interpreter; data lives behind store capabilities.
#load "Support/Sqlite.fs"

open Fx.Sqlite

let db = Database.connect "events.db" // expect: EFF009
let made = Database() // expect: EFF009
let changed = Database.exec db "DELETE FROM events" // expect: EFF009
