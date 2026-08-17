# Delta for Gastos

## ADDED Requirements

### Requirement: A Compra-Linked Proveedor Gasto Writes A Pago Movement To The Ledger

A gasto with `categoria = proveedor` AND `id_proveedor NOT NULL` MUST write
exactly one negative `pago` movement to
`movimientos_cuenta_corriente_proveedor` inside `InsertarGastoAsync`'s
existing transaction, carrying `id_gasto` and — when `id_comprobante_compra`
is set — the same value as the movement's imputación. A gasto failing
either condition (a different categoria, or `categoria = proveedor` with
`id_proveedor IS NULL`) MUST write no movement — the same predicate the
retired `saldo-de-proveedor` formula used.

#### Scenario: A proveedor-categoria gasto with id_proveedor writes one pago movement
- GIVEN an open turno and a gasto of `categoria = proveedor`, `id_proveedor`
  set, `importe = 400`
- WHEN the gasto is inserted
- THEN exactly one `pago` movement of `importe = -400` is written in the
  same transaction

#### Scenario: A proveedor-categoria gasto with no id_proveedor writes no movement
- GIVEN a gasto of `categoria = proveedor` with `id_proveedor IS NULL`
- WHEN it is inserted
- THEN no ledger movement is written — the same case the retired formula
  already excluded from the saldo

#### Scenario: A gasto still requires an open turno regardless of the ledger write
- GIVEN punto de venta 7 has no open turno
- WHEN a `categoria = proveedor` gasto with `id_proveedor` set is submitted
- THEN it is rejected with `409 turno_no_abierto` and no ledger movement is
  written
