# Managed Native Binding MVP

## Provenance decision

The managed CoinJoin binding is codec-only until a production packaged native
artifact is published and independently identified.

The authoritative native repository currently resolves as follows:

- `origin/main` and `organization/main`: `405adbd5c434a67957e5b3162d3077796e763cae`
- signer ABI ops 11/12 introduced at: `c03442ff793db12a105fbbecc9284d41b1f5fcb4`
- cleanup after that ABI change: `405adbd5c434a67957e5b3162d3077796e763cae`

The repository contains source and build scripts, but no tracked production
`.dylib`/`.so`, release asset, tag, or published artifact digest for this ABI.
Consequently there is no authoritative native artifact hash to pin. The older
managed values (`NativeCommit=4c7f7b5...` and the `2818...` macOS SHA-256) are
not valid provenance for ops 11/12 and have been removed from the runtime
claim.

`LiquidCoinJoinNativeBinding` now reports provenance as explicitly unavailable
(`NativeCommit = "UNAVAILABLE"`) and does not resolve, hash, or load a native
library. `LiquidCoinJoinFrame` and its frame tests remain the only active MVP
surface. A production loader may be restored only together with the exact
packaged artifact, platform, SHA-256, and matching native commit recorded here.
