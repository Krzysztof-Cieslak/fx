// Writing an extension in TypeScript (spec §16.1–16.3): contribution types and hooks, methods with
// typed errors, activation, and the `Extension` value the server runs. It mirrors Fx.Sdk: a manifest
// declares, `activate` provides exactly what it declares (the host checks), and code runs in the
// extension's environment. Hook points belong to the host (or a native extension); a TypeScript
// extension names them with `Hook.gate`, `Hook.transform`, `Hook.observe` and `Hook.contribute` and the
// codec of what flows through them.

import { Codec, createCodec, type Validation, Validation as V } from "./codec.ts";
import type { ContributionDecl, Decision as DecisionJson, Manifest, MethodDecl } from "./generated/contracts.d.ts";
import type { Json } from "./json.ts";
import { decisionCodec, slotDeclCodec, transformSettings, type SlotDecl } from "./protocol.ts";
import { toJsonSchema } from "./schema.ts";

/**
 * The environment extension code runs in: its extension's id, the capabilities the host grants it,
 * and the caller's cancellation (`IExtensionEnv`). An extension's authority is this value.
 */
export interface ExtensionEnv {
  /** The id of the extension whose code runs. */
  readonly extension: string;
  /** Aborted when the host cancels the request, or the connection closes. */
  readonly signal: AbortSignal;
  /** The clock capability. */
  readonly clock: Clock;
}

/** The current time, as the clock capability gives it. */
export interface Clock {
  now(): Date;
}

/** The system clock. */
export const systemClock: Clock = { now: () => new Date() };

/** The kinds of contribution types a TypeScript extension contributes to (spec §16.3). */
export type ContributionKind = "data" | "gate" | "transform" | "observe";

/** How an extension process runs a contribution's code for the host: JSON in, JSON out. */
export type Serve = (input: unknown, env: ExtensionEnv) => Promise<Validation<Json>>;

/**
 * A contribution type: a typed key named by the host (`gate:model-egress`, `sample.word`), and how its
 * values cross to the host: their JSON form (`toJson`) and, for code, how this process serves it.
 */
export interface ContributionType<T> {
  readonly name: string;
  readonly kind: ContributionKind;
  /** The JSON form of a value: a data value, or code's settings (a transform's priority). */
  toJson(value: T): Json | undefined;
  /** How this process serves a value's code; `undefined` for data. */
  readonly serve: ((value: T) => Serve) | undefined;
}

/** A gate: decides about an intent; gates combine Deny > Ask > Allow, and a gate that fails denies. */
export interface Gate<I> {
  check(intent: I, env: ExtensionEnv): Decision | Promise<Decision>;
}

/** A transform: rewrites a value. Lower priorities run first; one that fails fails the pipeline. */
export interface Transform<T> {
  readonly priority: number;
  apply(value: T, env: ExtensionEnv): T | Promise<T>;
}

/** An observer: sees events; it cannot change them, and its failures are reported, never propagated. */
export interface Observer<E> {
  observe(event: E, env: ExtensionEnv): void | Promise<void>;
}

/** What a gate decides: allow, ask a human, or deny (the generated type). */
export type Decision = DecisionJson;

export const Decision = {
  allow: { decision: "allow" } as Decision,
  ask(reason: string): Decision {
    return { decision: "ask", reason };
  },
  deny(reason: string): Decision {
    return { decision: "deny", reason };
  },
};

export const Transform = {
  /** A transform from a pure function. */
  ofFunction<T>(priority: number, apply: (value: T) => T): Transform<T> {
    return { priority, apply: (value) => apply(value) };
  },
};

function serving<T>(codec: Codec<T>, run: (value: T, env: ExtensionEnv) => Promise<Json>): Serve {
  return async (input, env) => {
    const decoded = codec.decode(input);
    return decoded.ok ? V.ok(await run(decoded.value, env)) : decoded;
  };
}

/** Hook points (spec §16.3), named as the host defines them; the names carry the kind. */
export const Hook = {
  /** Gates on intents: the host sends the intent, the gate answers a `Decision`. */
  gate<I>(point: string, intent: Codec<I>): ContributionType<Gate<I>> {
    return {
      name: "gate:" + point,
      kind: "gate",
      toJson: () => undefined,
      serve: (gate) => serving(intent, async (value, env) => decisionCodec.encode(await gate.check(value, env))),
    };
  },
  /** Transforms of values; their priority is their JSON form. */
  transform<T>(point: string, value: Codec<T>): ContributionType<Transform<T>> {
    return {
      name: "transform:" + point,
      kind: "transform",
      toJson: (transform) => transformSettings.encode({ priority: transform.priority }),
      serve: (transform) => serving(value, async (current, env) => value.encode(await transform.apply(current, env))),
    };
  },
  /** Observers of events. */
  observe<E>(point: string, event: Codec<E>): ContributionType<Observer<E>> {
    return {
      name: "observe:" + point,
      kind: "observe",
      toJson: () => undefined,
      serve: (observer) =>
        serving(event, async (value, env) => {
          await observer.observe(value, env);
          return null;
        }),
    };
  },
  /** Data, concatenated across extensions. */
  contribute<T>(name: string, item: Codec<T>): ContributionType<T> {
    return { name, kind: "data", toJson: (value) => item.encode(value), serve: undefined };
  },
};

/** Variant slots an extension declares (`variant-slot`): enumerated, with their choices. */
export const VariantSlots = {
  contributionType: Hook.contribute<SlotDecl>("variant-slot", slotDeclCodec),
};

/** A contribution with its type erased: what an activation provides. */
export interface Contribution {
  readonly type: string;
  readonly id: string;
  readonly kind: ContributionKind;
  /** Its JSON form, sent to the host on activation. */
  readonly json: Json | undefined;
  /** How this process runs its code; `undefined` for data. */
  readonly serve: Serve | undefined;
}

/** A contribution of a value under a type, with an id unique for the type. */
export function contribution<T>(type: ContributionType<T>, id: string, value: T): Contribution {
  return { type: type.name, id, kind: type.kind, json: type.toJson(value), serve: type.serve?.(value) };
}

/** A method's outcome: its result, or one of its typed errors. */
export type MethodResult<R, E> = { readonly ok: true; readonly value: R } | { readonly ok: false; readonly error: E };

export function ok<R>(value: R): MethodResult<R, never> {
  return { ok: true, value };
}

export function err<E>(error: E): MethodResult<never, E> {
  return { ok: false, error };
}

/** Why an implemented method gave no result: its params did not decode, or a typed error. */
export type MethodFailure =
  | { readonly kind: "invalidParams"; readonly issues: import("./codec.ts").Issue[] }
  | { readonly kind: "failed"; readonly message: string; readonly data: Json };

/** An implemented method with its types erased. A method that throws is a defect. */
export interface MethodImpl {
  readonly name: string;
  invoke(params: unknown, env: ExtensionEnv): Promise<{ ok: true; result: Json } | { ok: false; failure: MethodFailure }>;
}

const noError: Codec<never> = createCodec<never>(
  { kind: "anyOf", schemas: [], description: "This method has no typed errors." },
  () => null,
  () => V.fail([{ path: [], message: "this method has no typed errors" }]),
);

/**
 * A method of an extension's RPC contract (`ExtMethod`): its name and the codecs of its params, result
 * and typed errors. One value declares it in the manifest and implements it.
 */
export class ExtMethod<P, R, E> {
  private constructor(
    readonly name: string,
    readonly description: string | undefined,
    readonly params: Codec<P>,
    readonly result: Codec<R>,
    readonly error: Codec<E>,
    readonly message: (error: E) => string,
  ) {}

  /** A method without typed errors. Names are camelCase segments separated by `/`. */
  static define<P, R>(name: string, params: Codec<P>, result: Codec<R>): ExtMethod<P, R, never> {
    return new ExtMethod(name, undefined, params, result, noError, () => "no error");
  }

  describe(description: string): ExtMethod<P, R, E> {
    return new ExtMethod(this.name, description, this.params, this.result, this.error, this.message);
  }

  /** Gives the method typed errors: its expected failures, with the text the host shows for each. */
  withErrors<E2>(error: Codec<E2>, message: (error: E2) => string): ExtMethod<P, R, E2> {
    return new ExtMethod(this.name, this.description, this.params, this.result, error, message);
  }

  /** The method's entry in the manifest, with JSON Schemas of its params and result. */
  declare(): MethodDecl {
    const decl: MethodDecl = { name: this.name };
    if (this.description !== undefined) decl.description = this.description;
    decl.params = toJsonSchema(this.params.schema);
    decl.result = toJsonSchema(this.result.schema);
    return decl;
  }

  /** Implements the method. Params that do not decode never reach `handle`. */
  handle(handle: (params: P, env: ExtensionEnv) => MethodResult<R, E> | Promise<MethodResult<R, E>>): MethodImpl {
    const method = this;
    return {
      name: this.name,
      async invoke(params, env) {
        const decoded = method.params.decode(params);
        if (!decoded.ok) return { ok: false, failure: { kind: "invalidParams", issues: decoded.issues } };
        const outcome = await handle(decoded.value, env);
        return outcome.ok
          ? { ok: true, result: method.result.encode(outcome.value) }
          : { ok: false, failure: { kind: "failed", message: method.message(outcome.error), data: method.error.encode(outcome.error) } };
      },
    };
  }
}

/** What an activated extension provides: exactly what its manifest declares, and how to stop it. */
export interface Activation {
  readonly contributions: readonly Contribution[];
  readonly methods: readonly MethodImpl[];
  /** Releases what activation acquired. */
  deactivate?(env: ExtensionEnv): void | Promise<void>;
}

/** An extension: its manifest, and the activation that builds its contributions and methods. */
export interface Extension {
  readonly manifest: Manifest;
  activate(env: ExtensionEnv): Activation | Promise<Activation>;
}

/** Typed helpers to write a manifest's declarations (`Declare`). */
export const Declare = {
  /** Declares a contribution of a type with an id. */
  contribution<T>(type: ContributionType<T>, id: string): ContributionDecl {
    return { type: type.name, id };
  },
  /** Declares a data contribution with its value, for declarative manifests. */
  value<T>(type: ContributionType<T>, id: string, value: T): ContributionDecl {
    const json = type.toJson(value);
    return json === undefined ? { type: type.name, id } : { type: type.name, id, value: json };
  },
  /** Declares a method of the RPC contract. */
  method<P, R, E>(method: ExtMethod<P, R, E>): MethodDecl {
    return method.declare();
  },
};

/**
 * What an activation is missing or adds compared with the manifest, as the host checks it
 * (`extension.mismatch`): empty when it provides exactly what the manifest declares.
 */
export function activationMismatches(manifest: Manifest, activation: Activation): string[] {
  const key = (type: string, id: string) => type + "/" + id;
  const declared = new Set((manifest.contributions ?? []).map((c) => key(c.type, c.id)));
  const provided = new Set(activation.contributions.map((c) => key(c.type, c.id)));
  const declaredMethods = new Set((manifest.rpc?.methods ?? []).map((m) => m.name));
  const providedMethods = new Set(activation.methods.map((m) => m.name));
  return [
    ...[...declared].filter((c) => !provided.has(c)).map((c) => `declared but not provided: ${c}`),
    ...[...provided].filter((c) => !declared.has(c)).map((c) => `provided but not declared: ${c}`),
    ...[...declaredMethods].filter((m) => !providedMethods.has(m)).map((m) => `method declared but not provided: ${m}`),
    ...[...providedMethods].filter((m) => !declaredMethods.has(m)).map((m) => `method provided but not declared: ${m}`),
  ];
}
