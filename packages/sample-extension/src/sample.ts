// The sample extension in TypeScript: the same contract as the F# sample (extensions/Fx.Ext.Sample),
// one of each hook kind, data, and methods with typed errors, written against the TypeScript SDK. The
// transport-parity suite (tests/Fx.Extensions.Tests) runs it as a process on the pinned Bun and expects
// the transcript of the F# sample; test/sample.test.ts checks that its manifest is the F# one.

import {
  type Activation,
  Codec,
  Decision,
  Declare,
  type Extension,
  ExtMethod,
  type Gate,
  Hook,
  Manifest,
  type Observer,
  Runtimes,
  type Transform,
  contribution,
  err,
  extensionId,
  ok,
} from "../../sdk/src/index.ts";

export const Codecs = {
  greeting: Codec.record({ name: Codec.string, language: Codec.string }).named("Greeting"),
  note: Codec.record({ text: Codec.string }).named("Note"),
  division: Codec.record({ dividend: Codec.int, divisor: Codec.int }).named("Division"),
  mathError: Codec.union("kind", { divideByZero: {} }),
};

/** The hook points the sample hooks into; the host that runs them defines them. */
export const Points = {
  greeting: Hook.gate("sample.greeting", Codecs.greeting),
  text: Hook.transform("sample.text", Codec.string),
  words: Hook.contribute("sample.word", Codec.string),
  notes: Hook.observe("sample.note", Codecs.note),
};

/** The sample's RPC contract: `ext/<id>/<name>`. */
export const Methods = {
  echo: ExtMethod.define("echo", Codecs.note, Codecs.note).describe("Returns the note it is given."),
  divide: ExtMethod.define("divide", Codecs.division, Codec.int)
    .withErrors(Codecs.mathError, () => "division by zero")
    .describe("Integer division; dividing by zero is a typed error."),
  observed: ExtMethod.define("observed", Codec.unit, Codec.array(Codec.string)).describe("The notes the recorder has seen, in order."),
  fail: ExtMethod.define("fail", Codec.unit, Codec.unit).describe("Throws: a defect."),
};

export const id = extensionId("sample");

export const manifest: Manifest = {
  ...Manifest.create(id, "1.0.0", Runtimes.bun),
  description: "The sample extension: one of each hook kind, data and methods.",
  capabilities: ["clock"],
  contributions: [
    Declare.contribution(Points.greeting, "polite"),
    Declare.contribution(Points.greeting, "fragile"),
    Declare.contribution(Points.text, "trim"),
    Declare.contribution(Points.text, "shout"),
    Declare.contribution(Points.text, "bracket"),
    Declare.contribution(Points.text, "guard"),
    Declare.contribution(Points.words, "alpha"),
    Declare.contribution(Points.words, "beta"),
    Declare.contribution(Points.notes, "recorder"),
    Declare.contribution(Points.notes, "flaky"),
  ],
  rpc: {
    methods: [Declare.method(Methods.echo), Declare.method(Methods.divide), Declare.method(Methods.observed), Declare.method(Methods.fail)],
  },
};

const languages = new Set(["en", "pl"]);

/** Builds the contributions; the recorder's notes are the state of this activation. */
export function activate(): Activation {
  const notes: string[] = [];

  const polite: Gate<{ name: string; language: string }> = {
    check: (greeting) =>
      greeting.name.trim() === ""
        ? Decision.deny("a greeting needs a name")
        : !languages.has(greeting.language)
          ? Decision.ask(`greet in ${greeting.language}?`)
          : Decision.allow,
  };

  const fragile: Gate<{ name: string; language: string }> = {
    check: (greeting) => {
      if (greeting.name === "crash") throw new Error("the fragile gate broke");
      return Decision.allow;
    },
  };

  const guard: Transform<string> = {
    priority: 0,
    apply: (text) => {
      if (text.includes("explode")) throw new Error("the guard exploded");
      return text;
    },
  };

  const recorder: Observer<{ text: string }> = { observe: (note) => void notes.push(note.text) };

  const flaky: Observer<{ text: string }> = {
    observe: (note) => {
      if (note.text === "boom") throw new Error("the flaky observer failed");
    },
  };

  return {
    contributions: [
      contribution(Points.greeting, "polite", polite),
      contribution(Points.greeting, "fragile", fragile),
      contribution(Points.text, "trim", { priority: 10, apply: (s: string) => s.trim() }),
      contribution(Points.text, "shout", { priority: 20, apply: (s: string) => s.toUpperCase() + "!" }),
      contribution(Points.text, "bracket", { priority: 20, apply: (s: string) => "[" + s + "]" }),
      contribution(Points.text, "guard", guard),
      contribution(Points.words, "alpha", "alpha"),
      contribution(Points.words, "beta", "beta"),
      contribution(Points.notes, "recorder", recorder),
      contribution(Points.notes, "flaky", flaky),
    ],
    methods: [
      Methods.echo.handle((note) => ok(note)),
      Methods.divide.handle((d) => (d.divisor === 0 ? err({ kind: "divideByZero" as const }) : ok(Math.trunc(d.dividend / d.divisor)))),
      Methods.observed.handle(() => ok([...notes])),
      Methods.fail.handle(() => {
        throw new Error("the sample failed");
      }),
    ],
  };
}

/** The sample as an extension. */
export const extension: Extension = { manifest, activate };
