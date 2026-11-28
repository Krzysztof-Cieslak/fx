// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

export interface Limits {
  /** Integer, at least 0. */
  maxChars: number;
  /** Integer, at least 0. */
  maxItems: number;
  /** Integer, at least 0. */
  maxDepth: number;
  /** Integer, at least 0. */
  maxStringChars: number;
}

/** A profile: the value of every variant slot, the extension versions, the conformance results and the component lockfile's hash. */
export interface Profile {
  /** Integer, exactly 1. */
  format: number;
  core: string;
  selections: { [key: string]: unknown };
  extensions: {
    [key: string]: {
      version: string;
      /** Matches /^sha256:[0-9a-f]{64}$/. */
      hash?: string;
    };
  };
  conformance: {
    [key: string]: {
      [key: string]: {
        /** Integer, at least 0. */
        passed: number;
        /** Integer, at least 0. */
        failed: number;
        /** Integer, at least 0. */
        skipped: number;
      };
    };
  };
  /** Matches /^sha256:[0-9a-f]{64}$/. */
  lockfile?: string;
}

/** The value of a variant slot and where it came from. */
export interface ResolvedVariant {
  /** A variant slot id: dotted segments, e.g. "agent.actionSurface". */
  slot: string;
  value: unknown;
  source: VariantSource;
}

/** A variant slot: its id, what it changes, its default and the values it may take. */
export interface SlotDecl {
  /** Matches /^[a-z][A-Za-z0-9_-]*(\.[a-z][A-Za-z0-9_-]*)+$/. */
  id: string;
  description: string;
  default: unknown;
  choices?: unknown[];
  /** @default false */
  privileged?: boolean;
}

/** A variants policy: slots pinned to one value, and values slots may not take. */
export interface VariantPolicy {
  /** @default {} */
  pin?: { [key: string]: unknown };
  /** @default {} */
  forbid?: { [key: string]: unknown[] };
}

/** The variant slots a run read, each once, in the order first read. */
export type VariantReads = ResolvedVariant[];

/** Where a variant value comes from. */
export interface VariantSource {
  /** Where a value comes from: default, host, project, session, explicit or policy. */
  layer: "default" | "host" | "project" | "session" | "explicit" | "policy";
  /** @default "" */
  origin?: string;
}

/** Variant selections: slot ids and their values. */
export interface VariantsConfig {
  /** How the agent acts: cells-only (one cell tool and finish) or hybrid (cells plus direct read, edit and bash tools). */
  "agent.actionSurface"?: "cells-only" | "hybrid";
  /** What editing a range the agent has not read does: off, warn, or require reading it first (an error). */
  "edit.guard"?: "off" | "warn" | "require";
  /** The bounds of value previews shown to models: characters, items, depth and string length. */
  "values.previewLimits"?: Limits;
  /**
   * The largest value, in bytes of JSON, that is copied inline; larger values travel as blob references.
   * Integer, from 0 to 1048576.
   */
  "values.inlineMaxBytes"?: number;
  /**
   * The largest argument of a host call, in bytes of JSON, that a cell passes inline (a file's text, a command's input).
   * Integer, from 1024 to 12582912.
   */
  "values.argumentMaxBytes"?: number;
  /** The shell that runs scripts on Windows: pwsh (PowerShell 7) or powershell (Windows PowerShell). */
  "shell.windows"?: "pwsh" | "powershell";
  /** How a goal's iterations carry context: continue, compact, or fresh-handoff (a fresh session with a handoff). */
  "goal.context"?: "continue" | "compact" | "fresh-handoff";
  /**
   * The model for the main role, as provider/model; null until configured.
   * A model: provider/model, e.g. "anthropic/claude-opus".
   * Matches /^[A-Za-z0-9][A-Za-z0-9._-]*\/[^\s]+$/.
   */
  "model.role.main"?: string | null;
  /**
   * The model for the fast role, as provider/model; null until configured.
   * A model: provider/model, e.g. "anthropic/claude-opus".
   * Matches /^[A-Za-z0-9][A-Za-z0-9._-]*\/[^\s]+$/.
   */
  "model.role.fast"?: string | null;
  /**
   * The model for the summarizer role, as provider/model; null until configured.
   * A model: provider/model, e.g. "anthropic/claude-opus".
   * Matches /^[A-Za-z0-9][A-Za-z0-9._-]*\/[^\s]+$/.
   */
  "model.role.summarizer"?: string | null;
  /**
   * The model for the review role, as provider/model; null until configured.
   * A model: provider/model, e.g. "anthropic/claude-opus".
   * Matches /^[A-Za-z0-9][A-Za-z0-9._-]*\/[^\s]+$/.
   */
  "model.role.review"?: string | null;
  /**
   * The model for the judge role, as provider/model; null until configured.
   * A model: provider/model, e.g. "anthropic/claude-opus".
   * Matches /^[A-Za-z0-9][A-Za-z0-9._-]*\/[^\s]+$/.
   */
  "model.role.judge"?: string | null;
  /**
   * The model for the embed role, as provider/model; null until configured.
   * A model: provider/model, e.g. "anthropic/claude-opus".
   * Matches /^[A-Za-z0-9][A-Za-z0-9._-]*\/[^\s]+$/.
   */
  "model.role.embed"?: string | null;
}
