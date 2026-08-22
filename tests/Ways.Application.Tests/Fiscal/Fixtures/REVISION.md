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

## Contents (slice 2 — WSAA)

| File | What it represents |
|---|---|
| `Wsaa/TraGolden.xml` | `loginTicketRequest` (TRA) built by `GeneradorDeTra` for the fixed clock/uniqueId used by `GeneradorDeTraTests` — element names and order pinned (target 24). |
| `Wsaa/LoginCmsEnvelopeGolden.xml` | The `loginCms` SOAP envelope built by `SobreSoap.Construir` — namespace URI, `soapenv` prefix, `in0` element, byte-for-byte (target 28). |
| `Wsaa/LoginTicketResponse.xml` | A successful `loginCms` SOAP response (`loginCmsResponse`/`loginCmsReturn` wrapping the escaped `loginTicketResponse`) — used by `ClienteWsaaTests` to prove token/sign/expiration parsing. |
| `Wsaa/Faults/Fault{500,501,502,600,601,602}.xml` | The six WSAA fault responses of the taxonomy (target 34), one file per code. |

## Contents (slice 3 — WSFE)

**Honesty note, reasserted (T4)**: unlike the WSAA fixtures above (verified against
`Especificacion_Tecnica_WSAA_1.2.2.pdf` during slice 2), the WSFE fixtures below are a
best-effort transcription of the **public** WSFEv1 contract built by this agent **without direct
access to `manual-desarrollador-ARCA-COMPG-v4-0.pdf`**. Element names, nesting (`Auth`/`FeCabReq`/
`FeDetReq`/`FeDetResp`), and the split between call-level `Errors[]` and per-detail
`Observaciones[]` follow the well-documented public WSFEv1 shape, but no test in 19a can catch a
transcription error against the real manual or the real wire — confirming this set against the PDF
and against a real WSFE response is 19b's first task (same limitation already recorded for the
WSAA fault-code numbering, T3).

| File | What it represents |
|---|---|
| `Wsfe/FecaeSolicitarRequestGolden.xml` | The `FECAESolicitar` envelope built by `MapeadorWsfe.ConstruirFecaeSolicitar` for a fixed one-line invoice (`Concepto = 1`, no service dates) — `Auth`/`FeCabReq`/`FeDetReq` order, money/date/currency formatting, byte-for-byte (targets 37, 38, 39). |
| `Wsfe/Respuestas/FecaeSolicitarAprobado.xml` | `Resultado = A`, a CAE, empty `Observaciones` — the plain-approval response state (target 46). |
| `Wsfe/Respuestas/FecaeSolicitarAprobadoConObservaciones.xml` | `Resultado = A`, a CAE, **and** one `Obs` — proves an approval-with-observations still writes a CAE (target 46). |
| `Wsfe/Respuestas/FecaeSolicitarRechazado.xml` | `Resultado = R`, no CAE, a business-rejection `Obs` (code `10015`, a generic validation reason — not `10016`) — the ordinary rejection path (spec: "A rejected response writes no CAE..."). |
| `Wsfe/Respuestas/FecaeSolicitarNumeroNoCorrelativo.xml` | `Resultado = R` with `Obs.Code = 10016` — invariant I1's failure mode (target 48). |
| `Wsfe/Respuestas/FecaeSolicitarTicketInvalido.xml` | A call-level `Errors[]` with `Code = 600` and **no** `FeDetResp` at all — the invalid-ticket/sign path (target 49). |
| `Wsfe/Respuestas/FecompConsultarEncontrado.xml` | `FECompConsultar` found — a `ResultGet` with the same CAE as the approved fixture (I2 adoption, target 67 wiring). |
| `Wsfe/Respuestas/FecompConsultarNoEncontrado.xml` | `FECompConsultar` not found — an empty `ResultGet` and a top-level `Errors[Code=602]`. |
| `Wsfe/Respuestas/FecompUltimoAutorizadoHead.xml` | `FECompUltimoAutorizado` for a series with prior authorizations — `CbteNro = 104`. |
| `Wsfe/Respuestas/FecompUltimoAutorizadoVacio.xml` | `FECompUltimoAutorizado` for a never-used series — `CbteNro = 0`, the legal "series never used" answer (target 51). |
| `Wsfe/Respuestas/FeParamGetTiposIva.xml` | `FEParamGetTiposIva` — minimal completeness coverage for `ParametrosAsync` (no numbered target covers `FEParamGet*` in this slice's mutation table; mentioned explicitly in the apply mandate). |
