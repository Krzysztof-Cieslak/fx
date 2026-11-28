/*
 * The tree-sitter library fx links (T008, docs/spikes/tree-sitter.md): the core as one translation
 * unit, then the few functions the binding needs that the C API lacks. The grammars are compiled
 * separately (each parser.c defines static tables with the same names) and archived with this.
 *
 * FX_TS_API keeps the shim's functions visible in the shared library on Linux and macOS, where the
 * core is compiled with hidden visibility (its API is marked visible in api.h). On Windows the DLL's
 * exports come from the .def file TreeSitter.targets writes. The static library exports nothing:
 * Native AOT binds the imports at link time.
 */
#include "lib.c"
/* FX_TS_PIN: the archives this was built from, written by TreeSitter.targets. */
#include "pin.h"

#if defined(_WIN32)
#define FX_TS_API
#else
#define FX_TS_API __attribute__((visibility("default")))
#endif

/* Stops a parse once the int32 the payload points to is non-zero (set from another thread). */
static bool fx_ts_parse_cancelled(TSParseState *state) {
  const volatile int32_t *flag = (const volatile int32_t *)state->payload;
  return flag != NULL && *flag != 0;
}

/*
 * Parses UTF-8 text, checking *cancel as it goes (tree-sitter calls back every hundred or so
 * operations). Returns NULL when cancelled, or when the parser has no language, after resetting the
 * parser so the next parse starts afresh. cancel may be NULL.
 */
FX_TS_API TSTree *fx_ts_parse_utf8(
  TSParser *parser,
  const char *text,
  uint32_t length,
  const volatile int32_t *cancel
) {
  TSStringInput input = {text, length};
  TSInput source = {&input, ts_string_input_read, TSInputEncodingUTF8, NULL};
  TSParseOptions options = {(void *)cancel, fx_ts_parse_cancelled};
  TSTree *tree = ts_parser_parse_with_options(parser, NULL, source, options);
  if (tree == NULL) ts_parser_reset(parser);
  return tree;
}

/* The archives this library was built from, e.g. "tree-sitter-0.27.2 tree-sitter-python-0.25.0". */
FX_TS_API const char *fx_ts_pin(void) { return FX_TS_PIN; }

/* The ABI range this core accepts, for the binding's checks. */
FX_TS_API uint32_t fx_ts_language_version(void) { return TREE_SITTER_LANGUAGE_VERSION; }
FX_TS_API uint32_t fx_ts_min_compatible_language_version(void) { return TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION; }
