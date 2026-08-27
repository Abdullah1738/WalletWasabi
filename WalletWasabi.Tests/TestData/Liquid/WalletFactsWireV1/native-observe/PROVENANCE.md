# MANAGED-WALLET-FACTS-FFI-001 native-observe fixture

Native-produced ground-truth fixture for the managed wallet-facts observation binding tests.

- Native authority: wasabi-liquid-native commit `bd50133a9fbcac5d187757e634c1cc2fc65a10ac`.
- Generator: a scratch cargo harness (throwaway project, not shipped) reusing the fixture
  builder of `crates/wallet-facts-ffi/tests/e2e.rs` at that commit verbatim — same test
  descriptor (`elwpkh([28b3f14e/84'/1'/0']tpubDC2Q4xK4XH72GM7MowNuajyWVbigRLBWKswyP5T88hpPwu5nGqJWnda8zhJEFt71av73Hm8mUMMFSz9acNVzz8b1UbdSHCDXKTbSv5eEytu/<0;1>/*)#u0khc0kg`),
  same `EPOCH = [0x41; 32]`, `SLIP77 = [0x52; 32]`, `StdRng::from_seed([0x99; 32])` fixture
  transactions, and the pinned entropy pair `ENTROPY_A = [0x63; 32]` (capacity query) /
  `ENTROPY_B = [0x74; 32]` (write call), driven through `wln_wallet_facts_observe_impl_v1`
  with the frozen two-call capacity protocol.
- Shapes: `request-single` owns the external (branch 0, index 0) and internal (branch 1,
  index 1) outputs of one candidate; `request-zero` is the lawful zero-owned batch (every
  output blinded to a non-catalog script); `request-multi` lists both candidates in descriptor
  request order and the response orders the two transactions by ascending consensus txid
  (`6acd40dd…` before `f531d410…`).
- `expected-response-*.hex` are the exact native-produced WLFV frames under the pinned entropy
  pair; `expected-fields.tsv` is the decoded field dump (txid, witness binding, inputs, and
  per-owned-output index/branch/derivation/script/spend key/blinding key/asset/value) in exact
  WLFV order.
- Provenance harness: scratch project with path dependencies on the pinned native checkout's
  `crates/{wallet-facts,wallet-facts-wire,wallet-facts-ffi}`; neither real repo was modified.
