# Delta for Arqueo de Cierre

## ADDED Requirements

### Requirement: A Proveedor Gasto Linked To A Compra Introduces No New Derivation Term

A `gasto` with `id_comprobante_compra` set MUST flow through the existing
`SUM(gastos.importe on that medio)` term of the per-medio `importe_esperado`
derivation exactly like any other gasto — `CalculadorDeArqueo` MUST NOT
gain a compra-specific branch, term, or formula.

#### Scenario: A compra payment reduces esperado through the existing term only
- GIVEN a turno with `1500` in efectivo pagos and a `400` gasto
  (`categoria = proveedor`, linked to a confirmed compra) paid in efectivo
- WHEN the efectivo derivation runs
- THEN `importe_esperado` decreases by exactly `400` through
  `SUM(gastos.importe on that medio)`, with no separate compra term

#### Scenario: CalculadorDeArqueo source is unchanged by this stage
- GIVEN the `CalculadorDeArqueo` implementation before and after stage 8
  ships
- WHEN both versions are compared
- THEN they are byte-identical — no new branch or term was introduced for
  compra-linked gastos
