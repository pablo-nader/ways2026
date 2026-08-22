# Fixture revision — WSAA (Slice 2)

Pinned per proposal decision 8: the fixture set is the executable contract, and it names its
manual section so a future manual revision shows up as a diff between fixture sets.

- **Manual**: `manual-desarrollador-ARCA-COMPG-v4-0.pdf` (RG 4291, rev. 15/01/2025).
- **WSAA spec**: `Especificacion_Tecnica_WSAA_1.2.2.pdf`.

## Honesty notes (carried from design.md open questions)

- **T3 — fault numbering is the proposal's, not the wire's.** The specification publishes
  symbolic fault codes (e.g. `ns1:cms.sign.invalid`, `ns1:coe.alreadyAuthenticated`); these
  fixtures use the proposal's own numeric taxonomy (`500/501/502/600/601/602`, `faultcode` as a
  bare digit string) instead. Confirming the exact wire strings is a **19b** task — the mapping in
  `ClienteWsaa.MapearFalla` is one fixture edit away from whichever shape 19b confirms.
- **T4 — these fixtures are a manual transcription, not a captured wire trace.** No 19a test can
  detect a transcription error against the real manual; 19b's first task is the fixture-vs-reality
  diff.

## Contents (this slice)

| File | What it represents |
|---|---|
| `Wsaa/TraGolden.xml` | `loginTicketRequest` (TRA) built by `GeneradorDeTra` for the fixed clock/uniqueId used by `GeneradorDeTraTests` — element names and order pinned (target 24). |
| `Wsaa/LoginCmsEnvelopeGolden.xml` | The `loginCms` SOAP envelope built by `SobreSoap.Construir` — namespace URI, `soapenv` prefix, `in0` element, byte-for-byte (target 28). |
| `Wsaa/LoginTicketResponse.xml` | A successful `loginCms` SOAP response (`loginCmsResponse`/`loginCmsReturn` wrapping the escaped `loginTicketResponse`) — used by `ClienteWsaaTests` to prove token/sign/expiration parsing. |
| `Wsaa/Faults/Fault{500,501,502,600,601,602}.xml` | The six WSAA fault responses of the taxonomy (target 34), one file per code. |
