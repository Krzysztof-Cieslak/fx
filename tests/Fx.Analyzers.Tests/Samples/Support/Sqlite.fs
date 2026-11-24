// A stand-in for the SQLite binding (src/Fx.Sqlite), loaded by Eff009Storage.fsx: scripts cannot
// declare namespaces, and the real assembly may not be built when the samples are analyzed. It calls
// nothing of its own, so the analyzers find nothing in it.
namespace Fx.Sqlite

type Database() =
    member _.Name = "stand-in"

module Database =
    let connect (name: string) : Database = Unchecked.defaultof<Database>

    let exec (db: Database) (sql: string) : int = sql.Length
