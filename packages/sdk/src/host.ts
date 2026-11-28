// The capabilities of an extension served in this process, over its connection to the host (`host/*`,
// T109b), as Fx.Sdk.Process's `HostClient` builds them for F#: what the manifest does not list throws
// `CapabilityDenied` here, without asking the host; the extension's own slots are asked for once, when it
// activates (the host's values do not change while it runs).

import type { Codec } from "./codec.ts";
import type { HostError, Manifest, ResolvedVariant } from "./generated/contracts.d.ts";
import { Capability, CapabilityDenied, type Clock, type ExtensionEnv, type Grants, type Packages, type Variants, deniedCapabilities } from "./extension.ts";
import { ExtensionProtocol, hostDataCodec, hostErrorCodec, hostPackagesCodec, hostSlotsCodec } from "./protocol.ts";
import { Codec as C } from "./codec.ts";
import { type RpcPeer, RpcError, errorCodes } from "./rpc.ts";

type HostOutcome<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: HostError };

/** The host could not answer a `host/*` request: a defect where the extension used the capability. */
export class HostUnreachable extends Error {
  constructor(message: string) {
    super(message);
    this.name = "HostUnreachable";
  }
}

async function hostCall<T>(peer: RpcPeer, method: string, params: unknown, codec: Codec<T>, signal: AbortSignal): Promise<HostOutcome<T>> {
  let raw: unknown;
  try {
    raw = await peer.request(method, params, signal);
  } catch (e) {
    if (e instanceof RpcError && e.code === errorCodes.applicationError) {
      const typed = hostErrorCodec.decode(e.data);
      if (typed.ok) return { ok: false, error: typed.value };
    }
    if (e instanceof RpcError) throw new HostUnreachable(`the host did not answer ${method}: ${e.message}`);
    throw e;
  }
  const decoded = codec.decode(raw);
  if (!decoded.ok) throw new HostUnreachable(`the host's answer to ${method} does not decode`);
  return { ok: true, value: decoded.value };
}

const granted = (manifest: Manifest, capability: string) => (manifest.capabilities ?? []).includes(capability);

/** The values of the extension's own slots if its manifest lists `variants`: asked once, at activation. */
export async function prefetchSlots(peer: RpcPeer, manifest: Manifest, signal: AbortSignal): Promise<{ ok: true; value: ResolvedVariant[] } | { ok: false; error: string }> {
  if (!granted(manifest, Capability.variants)) return { ok: true, value: [] };
  try {
    const outcome = await hostCall(peer, ExtensionProtocol.hostMethods.variants, { extension: manifest.id }, hostSlotsCodec, signal);
    return outcome.ok ? { ok: true, value: outcome.value.slots } : { ok: false, error: `the host did not answer host/variants: ${JSON.stringify(outcome.error)}` };
  } catch (e) {
    return { ok: false, error: `the host did not answer host/variants: ${e instanceof Error ? e.message : String(e)}` };
  }
}

/** The environment of a served extension: its id, the clock, and its capabilities over the connection. */
export function connectedEnv(peer: RpcPeer, manifest: Manifest, slots: readonly ResolvedVariant[], clock: Clock) {
  const extension = manifest.id;
  const denied = deniedCapabilities(extension);
  const value = <T>(outcome: HostOutcome<T>): T => {
    if (outcome.ok) return outcome.value;
    if (outcome.error.kind === "notGranted") throw new CapabilityDenied(extension, outcome.error.capability);
    throw new HostUnreachable(`the host refused: ${JSON.stringify(outcome.error)}`);
  };
  const grant = <T>(outcome: HostOutcome<T>) => {
    if (outcome.ok) return { ok: true as const, value: outcome.value };
    if (outcome.error.kind === "grant") return { ok: false as const, error: outcome.error.error as never };
    return value(outcome) as never;
  };

  return (signal: AbortSignal): ExtensionEnv => {
    const variants: Variants = granted(manifest, Capability.variants)
      ? { read: (slot) => slots.find((s) => s.slot === slot), slots: () => [...slots] }
      : denied.variants;
    const packages: Packages = granted(manifest, Capability.packages)
      ? {
          list: async () => value(await hostCall(peer, ExtensionProtocol.hostMethods.packages, { extension }, hostPackagesCodec, signal)).packages,
          data: async (type) => {
            const outcome = await hostCall(peer, ExtensionProtocol.hostMethods.contributions, { extension, type }, hostDataCodec, signal);
            if (outcome.ok) return { ok: true, value: outcome.value.contributions };
            const error = outcome.error;
            if (error.kind === "unknownType") return { ok: false, error: { kind: "unknownType", type: error.type } };
            if (error.kind === "notData") return { ok: false, error: { kind: "notData", type: error.type, contributionKind: error.contributionKind } };
            return value(outcome);
          },
        }
      : denied.packages;
    const grants: Grants = granted(manifest, Capability.grants)
      ? {
          check: async (query) => grant(await hostCall(peer, ExtensionProtocol.hostMethods.grantsCheck, { extension, query }, C.json, signal)) as never,
          request: async (ask) => grant(await hostCall(peer, ExtensionProtocol.hostMethods.grantsRequest, { extension, ask }, C.json, signal)) as never,
          await: async (request) => grant(await hostCall(peer, ExtensionProtocol.hostMethods.grantsAwait, { extension, request }, C.json, signal)) as never,
          withdraw: async (request, reason) =>
            grant(await hostCall(peer, ExtensionProtocol.hostMethods.grantsWithdraw, { extension, request, reason }, C.unit, signal)) as never,
        }
      : denied.grants;
    return { extension, signal, clock, variants, packages, grants };
  };
}
