/// The outline queries (tree-sitter's S-expression patterns), one per grammar; TSX uses TypeScript's.
/// A pattern captures a definition as `@definition.<kind>` and its name as `@name` (Outline.fs).
/// Literals rather than embedded .scm files: resources are reflection, which the AOT rules ban.
[<RequireQualifiedAccess>]
module internal Fx.TreeSitter.Queries

[<Literal>]
let FSharp =
    """
; Outline of F# (tree-sitter-fsharp). Captures: @definition.<kind> on the node whose lines the
; item spans, @name on its name. Nesting comes from where the nodes are, not from the query.

(namespace name: (_) @name) @definition.namespace

(named_module name: (_) @name) @definition.module

(module_defn (identifier) @name) @definition.module

(record_type_defn (type_name type_name: (_) @name)) @definition.type
(union_type_defn (type_name type_name: (_) @name)) @definition.type
(anon_type_defn (type_name type_name: (_) @name)) @definition.type
(interface_type_defn (type_name type_name: (_) @name)) @definition.type
(enum_type_defn (type_name type_name: (_) @name)) @definition.type
(delegate_type_defn (type_name type_name: (_) @name)) @definition.type
(type_abbrev_defn (type_name type_name: (_) @name)) @definition.type
(type_extension (type_name type_name: (_) @name)) @definition.type

(exception_definition exception_name: (_) @name) @definition.exception

; Module-level functions and values: a let directly in a file, module or namespace (not one inside
; a function body). Queries have no alternation of parents, so one pattern per parent.
(file
  (declaration_expression
    (function_or_value_defn
      (function_declaration_left (identifier) @name)) @definition.function))
(named_module
  (declaration_expression
    (function_or_value_defn
      (function_declaration_left (identifier) @name)) @definition.function))
(module_defn
  (declaration_expression
    (function_or_value_defn
      (function_declaration_left (identifier) @name)) @definition.function))
(namespace
  (declaration_expression
    (function_or_value_defn
      (function_declaration_left (identifier) @name)) @definition.function))

(file
  (declaration_expression
    (function_or_value_defn
      (value_declaration_left
        (identifier_pattern (long_identifier_or_op (identifier) @name)))) @definition.value))
(named_module
  (declaration_expression
    (function_or_value_defn
      (value_declaration_left
        (identifier_pattern (long_identifier_or_op (identifier) @name)))) @definition.value))
(module_defn
  (declaration_expression
    (function_or_value_defn
      (value_declaration_left
        (identifier_pattern (long_identifier_or_op (identifier) @name)))) @definition.value))
(namespace
  (declaration_expression
    (function_or_value_defn
      (value_declaration_left
        (identifier_pattern (long_identifier_or_op (identifier) @name)))) @definition.value))

; Members: `member this.Name`, `member Name`, `static member`, abstract members.
(member_defn
  (method_or_prop_defn
    name: (property_or_ident method: (identifier) @name))) @definition.member

(member_defn
  (method_or_prop_defn
    name: (property_or_ident . (identifier) @name .))) @definition.member

(member_defn (member_signature (identifier) @name)) @definition.member
"""

[<Literal>]
let FSharpSignature =
    """
; Outline of F# signature files (tree-sitter-fsharp's fsharp_signature grammar). Captures:
; @definition.<kind> on the node whose lines the item spans, @name on its name.

(namespace name: (_) @name) @definition.namespace

(named_module name: (_) @name) @definition.module

(module_defn (identifier) @name) @definition.module

(record_type_defn (type_name type_name: (_) @name)) @definition.type
(union_type_defn (type_name type_name: (_) @name)) @definition.type
(anon_type_defn (type_name type_name: (_) @name)) @definition.type
(interface_type_defn (type_name type_name: (_) @name)) @definition.type
(enum_type_defn (type_name type_name: (_) @name)) @definition.type
(delegate_type_defn (type_name type_name: (_) @name)) @definition.type
(type_abbrev_defn (type_name type_name: (_) @name)) @definition.type

(exception_definition exception_name: (_) @name) @definition.exception

(value_definition (value_declaration_left (identifier_pattern) @name)) @definition.value

(member_defn (member_signature (identifier) @name)) @definition.member
"""

[<Literal>]
let CSharp =
    """
; Outline of C# (tree-sitter-c-sharp). Captures: @definition.<kind> on the node whose lines the
; item spans, @name on its name.

(namespace_declaration name: (_) @name) @definition.namespace
(file_scoped_namespace_declaration name: (_) @name) @definition.namespace

(class_declaration name: (identifier) @name) @definition.class
(struct_declaration name: (identifier) @name) @definition.struct
(record_declaration name: (identifier) @name) @definition.record
(interface_declaration name: (identifier) @name) @definition.interface
(enum_declaration name: (identifier) @name) @definition.enum
(delegate_declaration name: (identifier) @name) @definition.delegate

(method_declaration name: (identifier) @name) @definition.method
(constructor_declaration name: (identifier) @name) @definition.constructor
(destructor_declaration name: (identifier) @name) @definition.destructor
(operator_declaration operator: _ @name) @definition.operator
(property_declaration name: (identifier) @name) @definition.property
(indexer_declaration "this" @name) @definition.indexer
(event_declaration name: (identifier) @name) @definition.event
(event_field_declaration (variable_declaration (variable_declarator name: (identifier) @name))) @definition.event
(field_declaration (variable_declaration (variable_declarator name: (identifier) @name))) @definition.field
(enum_member_declaration name: (identifier) @name) @definition.member
"""

[<Literal>]
let Python =
    """
; Outline of Python (tree-sitter-python). Captures: @definition.<kind> on the node whose lines the
; item spans, @name on its name. Decorated definitions span their decorators.

(decorated_definition definition: (class_definition name: (identifier) @name)) @definition.class
(decorated_definition definition: (function_definition name: (identifier) @name)) @definition.function

(class_definition name: (identifier) @name) @definition.class
(function_definition name: (identifier) @name) @definition.function

; Module-level assignments: X = 1, X: int = 1.
(module (expression_statement (assignment left: (identifier) @name) @definition.value))
"""

[<Literal>]
let TypeScript =
    """
; Outline of TypeScript and TSX (tree-sitter-typescript: both grammars name these nodes alike).
; Captures: @definition.<kind> on the node whose lines the item spans, @name on its name. An
; export spans its `export` keyword.

(export_statement declaration: (function_declaration name: (_) @name)) @definition.function
(export_statement declaration: (generator_function_declaration name: (_) @name)) @definition.function
(export_statement declaration: (class_declaration name: (_) @name)) @definition.class
(export_statement declaration: (abstract_class_declaration name: (_) @name)) @definition.class
(export_statement declaration: (interface_declaration name: (_) @name)) @definition.interface
(export_statement declaration: (type_alias_declaration name: (_) @name)) @definition.type
(export_statement declaration: (enum_declaration name: (_) @name)) @definition.enum
(export_statement declaration: (internal_module name: (_) @name)) @definition.namespace
(export_statement
  declaration: (lexical_declaration
    (variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression)]))) @definition.function
(export_statement
  declaration: (lexical_declaration (variable_declarator name: (identifier) @name))) @definition.value

(function_declaration name: (_) @name) @definition.function
(generator_function_declaration name: (_) @name) @definition.function
(function_signature name: (_) @name) @definition.function
(class_declaration name: (_) @name) @definition.class
(abstract_class_declaration name: (_) @name) @definition.class
(interface_declaration name: (_) @name) @definition.interface
(type_alias_declaration name: (_) @name) @definition.type
(enum_declaration name: (_) @name) @definition.enum
(internal_module name: (_) @name) @definition.namespace
(module name: (_) @name) @definition.namespace

(method_definition name: (_) @name) @definition.method
(abstract_method_signature name: (_) @name) @definition.method
(public_field_definition name: (_) @name) @definition.field
; Interface members only: the same nodes make up inline object types ({ a: string }).
(interface_body (method_signature name: (_) @name) @definition.method)
(interface_body (property_signature name: (_) @name) @definition.property)

; Top-level consts and lets; those holding a function are functions.
(program
  (lexical_declaration
    (variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression)])) @definition.function)
(program (lexical_declaration (variable_declarator name: (identifier) @name)) @definition.value)
"""
