# Delta for Auxiliary Catalogs

## ADDED Requirements

### Requirement: codigo_afip Is Populated Via A Double Net, With No Schema Change

`tipos_comprobante`, `condiciones_fiscales`, and `alicuotas_iva` already carry `codigo_afip smallint
NULL` since Etapa 1, left NULL on purpose. This stage MUST populate it via **two independent nets**:
an idempotent data statement (`WHERE codigo_afip IS NULL`) for already-migrated databases, and a
seed change for fresh databases — each net tested independently, since the seeder only runs against
an empty table after migrations.

#### Scenario: An already-migrated database gets codigo_afip via the data statement
- GIVEN an existing database with `tipos_comprobante.codigo_afip IS NULL` on `FA`
- WHEN the migration's data statement runs
- THEN `FA.codigo_afip = 1`, and no row is inserted, activated, or deactivated

#### Scenario: A fresh database gets codigo_afip via the seed alone
- GIVEN a brand-new database seeded from `TiposComprobanteBase`
- WHEN the seeder runs (data statement is a no-op — the table was empty at migration time)
- THEN `FA.codigo_afip = 1` is present from the seed net alone

#### Scenario: Removing either net alone still fails its own test
- GIVEN the double-net contract
- WHEN the data statement is removed while the seed stays, or vice versa
- THEN the respective scenario (already-migrated vs. fresh database) fails independently

### Requirement: Exento And No Gravado Keep codigo_afip NULL By Rule

`Exento` and `No gravado` rows in `alicuotas_iva` MUST keep `codigo_afip = NULL` — they are not
alícuotas. Their amounts belong in `ImpOpEx` and `ImpTotConc` respectively (see `comprobante-fiscal`
capability) and MUST NEVER be mapped as if they were an `AlicIva` entry.

#### Scenario: Exento and No gravado remain NULL after the double net runs
- GIVEN the data statement and seed net for `alicuotas_iva` have both run
- WHEN `Exento` and `No gravado` rows are inspected
- THEN both still have `codigo_afip IS NULL`
