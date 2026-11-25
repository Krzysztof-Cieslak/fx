// The extension process's side of the extension protocol (docs/modules/extensions.md), for TypeScript
// extensions: serves `Extension` values to the host, as Fx.Sdk.Process's `ExtensionServer` does for F#
// ones, with the same methods, typed errors and rules: every request names the extension; activating
// twice answers the same; code runs in its extension's environment with the request's cancellation;
// a failure in extension code is a defect (JSON-RPC InternalError), which the host turns into a
// denying gate, a failed transform, a reported observer or a crashed method.

import type { Validation } from "./codec.ts";
import type { Activated, ExtensionProtocolError, Manifest } from "./generated/contracts.d.ts";
import { type Activation, type Clock, type Extension, type ExtensionEnv, systemClock } from "./extension.ts";
import {
  ExtensionProtocol,
  activateParamsCodec,
  activatedCodec,
  callParamsCodec,
  extensionRefCodec,
  invokeParamsCodec,
  protocolErrorCodec,
  protocolErrorMessage,
} from "./protocol.ts";
import { type PeerInfo, RpcError, RpcPeer, errorCodes } from "./rpc.ts";

interface Served {
  readonly manifest: Manifest;
  readonly activation: Activation;
}

/** How a server runs extension code. */
export interface ServerOptions {
  /** The process, as it introduces itself in `initialize`. */
  readonly peer: PeerInfo;
  /** The clock extension code gets; the system clock by default. */
  readonly clock?: Clock;
}

function typed(error: ExtensionProtocolError): RpcError {
  return RpcError.typed(protocolErrorMessage(error), protocolErrorCodec.encode(error));
}

function decodeParams<T>(validation: Validation<T>): T {
  if (!validation.ok) throw RpcError.invalidParams(validation.issues);
  return validation.value;
}

/** What a process answers for an activation: the JSON form of each contribution, and the method names. */
export function describeActivation(manifest: Manifest, activation: Activation): Activated {
  return {
    manifest,
    contributions: activation.contributions.map((c) => (c.json === undefined ? { type: c.type, id: c.id } : { type: c.type, id: c.id, value: c.json })),
    methods: activation.methods.map((m) => m.name),
  };
}

/**
 * Serves the extensions on a connection: handles the host's `initialize` (refusing hosts that do not
 * speak the extension protocol's version) and the protocol's requests.
 */
export function serveExtensions(peer: RpcPeer, extensions: readonly Extension[], options: ServerOptions): RpcPeer {
  const statics = new Map(extensions.map((e) => [e.manifest.id, e]));
  if (statics.size !== extensions.length) throw new TypeError("Two extensions served by one process have the same id.");
  const active = new Map<string, Served>();
  const clock = options.clock ?? systemClock;
  const envOf = (extension: string, signal: AbortSignal): ExtensionEnv => ({ extension, signal, clock });
  const answer = (activated: Activated) => activatedCodec.encode(activated);

  return peer
    .serve({
      peer: options.peer,
      accept: (params) => {
        const version = params[ExtensionProtocol.versionMember];
        if (typeof version !== "string" || !ExtensionProtocol.versions.includes(version)) {
          throw new RpcError(
            errorCodes.invalidRequest,
            `the host must speak extension protocol ${ExtensionProtocol.versions.join(", ")} (initialize member ${ExtensionProtocol.versionMember})`,
          );
        }
        return { [ExtensionProtocol.servesMember]: extensions.map((e) => e.manifest.id) };
      },
    })
    .onRequest(ExtensionProtocol.methods.activate, async (raw, signal) => {
      const params = decodeParams(activateParamsCodec.decode(raw));
      const served = active.get(params.extension);
      if (served !== undefined) return answer(describeActivation(served.manifest, served.activation));
      const extension = statics.get(params.extension);
      if (extension !== undefined) {
        const activation = await extension.activate(envOf(extension.manifest.id, signal));
        active.set(extension.manifest.id, { manifest: extension.manifest, activation });
        return answer(describeActivation(extension.manifest, activation));
      }
      if (params.manifest !== undefined && params.manifest.id === params.extension) {
        throw typed({
          kind: "activationFailed",
          extension: params.manifest.id,
          reason: `this process has no extension runtime ${params.manifest.runtime}`,
        });
      }
      throw typed({ kind: "unknownExtension", extension: params.extension });
    })
    .onRequest(ExtensionProtocol.methods.invoke, async (raw, signal) => {
      const params = decodeParams(invokeParamsCodec.decode(raw));
      const address = `${params.extension}/${params.type}/${params.id}`;
      const served = active.get(params.extension);
      if (served === undefined) throw typed({ kind: "notActive", extension: params.extension });
      const contribution = served.activation.contributions.find((c) => c.type === params.type && c.id === params.id);
      if (contribution === undefined) throw typed({ kind: "unknownContribution", address });
      if (contribution.serve === undefined) throw typed({ kind: "inProcessOnly", address });
      const output = await contribution.serve(params.input, envOf(params.extension, signal));
      if (!output.ok) throw typed({ kind: "invalid", issues: output.issues });
      return { output: output.value };
    })
    .onRequest(ExtensionProtocol.methods.call, async (raw, signal) => {
      const params = decodeParams(callParamsCodec.decode(raw));
      const served = active.get(params.extension);
      if (served === undefined) throw typed({ kind: "notActive", extension: params.extension });
      const method = served.activation.methods.find((m) => m.name === params.method);
      if (method === undefined) throw typed({ kind: "unknownMethod", method: params.method });
      const outcome = await method.invoke(params.params, envOf(params.extension, signal));
      if (outcome.ok) return { output: outcome.result };
      const failure = outcome.failure;
      throw typed(
        failure.kind === "invalidParams"
          ? { kind: "invalid", issues: failure.issues }
          : { kind: "methodFailed", message: failure.message, data: failure.data },
      );
    })
    .onRequest(ExtensionProtocol.methods.deactivate, async (raw, signal) => {
      const params = decodeParams(extensionRefCodec.decode(raw));
      const served = active.get(params.extension);
      if (served === undefined) throw typed({ kind: "notActive", extension: params.extension });
      active.delete(params.extension);
      await served.activation.deactivate?.(envOf(params.extension, signal));
      return null;
    });
}
