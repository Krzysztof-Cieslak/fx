---
id: 01M8FT7B29YMK0D1N62KCX31FT
anchor: '@file'
created: 2026-11-28T00:52:29Z
norm: '1'
---

A capability is attenuated in one place (ExtensionCapabilities.attenuate) and mirrored for processes in HostClient.capabilities and packages/sdk/src/host.ts; a new one also needs a name in Capability.known (admission refuses others) and a host/* handler in RpcRemote.hostHandlers.
