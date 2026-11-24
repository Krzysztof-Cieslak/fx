/*
** The SQLite library of the product: the pinned amalgamation (Sqlite.props) with these compile-time
** options, plus non-variadic shims for the binding in src/Fx.Sqlite. Sqlite.targets compiles this one
** file into a static library (linked into Native AOT binaries) and a shared library (loaded by the JIT
** for tests). Any change here changes the build key, so every target rebuilds.
**
** docs/spikes/sqlite.md explains each choice. Options are checked by tests through
** `PRAGMA compile_options`.
*/

/* Serialized: connections and statements may be used from any thread (finalizers included). */
#define SQLITE_THREADSAFE 1

/* Security. */
#define SQLITE_OMIT_LOAD_EXTENSION 1 /* no run-time extension loading at all */
#define SQLITE_DQS 0                 /* "text" is an identifier, never a string literal */
#define SQLITE_TRUSTED_SCHEMA 0      /* schemas cannot call functions with side effects */
#define SQLITE_ENABLE_API_ARMOR 1    /* misuse returns SQLITE_MISUSE instead of crashing */

/* Defaults. */
#define SQLITE_DEFAULT_FOREIGN_KEYS 1
#define SQLITE_TEMP_STORE 2 /* temporary tables and indices in memory unless a pragma says otherwise */

/* Features. JSON is built in. */
#define SQLITE_ENABLE_FTS5 1

/* From the recommended options (https://sqlite.org/compile.html#recommended_compile_time_options). */
#define SQLITE_DEFAULT_MEMSTATUS 0
#define SQLITE_LIKE_DOESNT_MATCH_BLOBS 1
#define SQLITE_OMIT_DECLTYPE 1
#define SQLITE_OMIT_DEPRECATED 1
#define SQLITE_OMIT_PROGRESS_CALLBACK 1
#define SQLITE_OMIT_SHARED_CACHE 1
#define SQLITE_STRICT_SUBTYPE 1
#define SQLITE_USE_ALLOCA 1

/* Platform: what a configure script would detect. */
#if !defined(_WIN32)
#define HAVE_USLEEP 1
#define HAVE_LOCALTIME_R 1
#endif
#if defined(__linux__)
#define HAVE_FDATASYNC 1
#endif

#include "sqlite3.c"

/*
** sqlite3_db_config is variadic, and variadic calls cannot be made through DllImport portably (on
** Apple arm64 variadic arguments go on the stack). This covers the options that take an int and an
** int* (SQLITE_DBCONFIG_DEFENSIVE, SQLITE_DBCONFIG_TRUSTED_SCHEMA, ...).
*/
SQLITE_API int sqlite3shim_db_config_flag(sqlite3 *db, int op, int value, int *current) {
  return sqlite3_db_config(db, op, value, current);
}
