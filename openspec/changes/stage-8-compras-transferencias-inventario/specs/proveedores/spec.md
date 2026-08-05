# Delta for Proveedores

## ADDED Requirements

### Requirement: Proveedor Referenced By A Comprobante Compra Cannot Be Removed

The composite FK from `comprobantes_compra.id_proveedor` to `proveedores`
MUST be `ON DELETE RESTRICT`. A proveedor referenced by at least one
`comprobante_compra` MUST NOT be removable at the schema layer.

#### Scenario: A hard delete of a referenced proveedor is rejected at the schema layer
- GIVEN a proveedor referenced by a confirmada compra
- WHEN a hard delete of that proveedor row is attempted at the database
- THEN Postgres rejects it with a foreign key violation, mapped by
  `db-error-backstops` to a domain conflict

### Requirement: Proveedor Saldo Read Entry Point

Proveedor detail reads MUST expose a saldo entry point implementing the
`saldo-de-proveedor` derived read, gated by `Politicas.OperacionDePos`.

#### Scenario: Proveedor detail includes the derived saldo
- GIVEN a proveedor with confirmed compras and linked gastos
- WHEN their detail is read
- THEN the response includes the derived saldo per the
  `saldo-de-proveedor` specification
