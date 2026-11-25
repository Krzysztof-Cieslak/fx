// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate.

/** Why an identity provider did not vouch for a client. */
export type AuthError =
  | { kind: "noProof"; reason: string }
  | { kind: "rejected"; reason: string }
  | { kind: "expired" }
  | { kind: "unavailable"; reason: string };

/** What the host established about a connection before the client said anything. */
export interface Connection {
  kind: ConnectionKind;
  /** Non-empty. */
  peerAccount?: string;
  /** @default false */
  ownerSecret?: boolean;
}

/** How a client is connected to the host. */
export type ConnectionKind = "in-process" | "unix-socket" | "named-pipe" | "loopback" | "stdio" | "remote";

/** The host: a stable id of the installation and the OS account it runs as. */
export interface HostIdentity {
  machine: string;
  account: OsAccount;
}

/** Why the host refused a client's identity in initialize. */
export type IdentityFailure =
  | { kind: "missing" }
  | { kind: "invalid"; issues: Issue[] }
  | {
      kind: "unknownProvider";
      /** Non-empty. */
      provider: string;
    }
  | {
      kind: "refused";
      /** Non-empty. */
      provider: string;
      error: AuthError;
    }
  | {
      kind: "invalidPrincipal";
      /** Non-empty. */
      provider: string;
      reason: string;
    }
  | {
      kind: "noRole";
      /** Non-empty. */
      principal: string;
    }
  | { kind: "tokenRejected" };

export interface Issue {
  path: (string | number)[];
  message: string;
}

/** An account of the host machine's operating system: a Unix user id or a Windows SID. */
export interface OsAccount {
  /** Non-empty. */
  id: string;
  /** Non-empty. */
  name?: string;
}

/** Who is connecting (spec §11.2): a principal an identity provider vouches for. */
export interface Principal {
  /** A principal's stable id: `<kind>:<provider>:<subject>`, e.g. `user:local-os-user:7Q2M9X4KD1/1000`. */
  id: string;
  /** Non-empty. */
  display: string;
  kind: PrincipalKind;
  /** Non-empty. */
  provider: string;
  /**
   * What the provider knows about the principal, by name.
   *
   * @default {}
   */
  claims?: { [key: string]: string[] };
}

/** Who acts: a person (user), an unattended device, or a program (service). */
export type PrincipalKind = "user" | "device" | "service";

/** What a principal may do on the host: viewer (reads), collaborator (also drives sessions and runs), owner (also permissions and the host). */
export type Role = "viewer" | "collaborator" | "owner";

/** A client's session with the host: who it is, what it may do, and the token that resumes it (only when just issued). */
export interface SessionInfo {
  /** Non-empty. */
  id: string;
  /** Non-empty. */
  token?: string;
  principal: Principal;
  roles: Role[];
  /** Non-empty. */
  client: string;
  /**
   * Microseconds since the Unix epoch, UTC.
   * Integer.
   */
  expires: number;
}

/** How a client signs in: through an identity provider (with what it asks for), or with a session token from before. */
export type SignIn =
  | {
      kind: "provider";
      /** Non-empty. */
      provider: string;
      credential?: unknown;
    }
  | {
      kind: "token";
      /** Non-empty. */
      token: string;
    };
