# Certificados Fiscales Specification

## Purpose

Storage and encryption of the fiscal X.509 certificate and private key per empresa+ambiente, the
`AdministracionFiscal` policy gate, rotation, and the absolute clause that key material never
appears outside the encrypted column.

## Requirements

### Requirement: AdministracionFiscal Gates The Certificate ABM, Admin Only

`/api/fiscal/certificados` (GET/POST/DELETE) MUST be gated by the new `Politicas.AdministracionFiscal`
policy, admitting only `RolConocido.Admin`.

#### Scenario: A Vendedor is rejected from the certificate ABM
- GIVEN a user with role Vendedor
- WHEN they call `POST /api/fiscal/certificados`
- THEN the request is rejected (authorization-wise)

#### Scenario: Admin can register a certificate
- GIVEN a user with role Admin
- WHEN they call `POST /api/fiscal/certificados` with a valid PEM and encrypted key material
- THEN the request succeeds

### Requirement: The Private Key Is Encrypted With AES-256-GCM Bound To Its Own Row

`clave_privada_cifrada` MUST be encrypted with `System.Security.Cryptography.AesGcm` using an AAD
composed of `id_tenant | id_empresa | ambiente | huella_sha256`, so the ciphertext is authenticated
against its own row identity. The master key MUST come from configuration/environment
(`Ways:Fiscal:ClaveMaestra`) and MUST NEVER be stored in the database or the repository.

#### Scenario: A certificate's private key round-trips through AES-GCM
- GIVEN a certificate registered with its private key encrypted under its row's AAD
- WHEN it is decrypted for a signing operation
- THEN the plaintext matches the original key bytes exactly

#### Scenario: Moving ciphertext to another empresa's row fails authentication
- GIVEN a certificate row's `clave_privada_cifrada`, `nonce`, and `tag_autenticacion`
- WHEN those bytes are copied into a different empresa's `certificados_fiscales` row and decryption
  is attempted
- THEN `AesGcm` decryption fails — the AAD no longer matches the row identity

#### Scenario: A missing master key makes the fiscal path inert, never a plaintext fallback
- GIVEN `Ways:Fiscal:ClaveMaestra` is unset or unreadable
- WHEN any fiscal signing operation is attempted
- THEN it fails loudly (409/503) — the system never decrypts to, or falls back to, plaintext

### Requirement: At Most One Active Certificate Per Empresa And Ambiente

`ux_certificados_fiscales_activo` MUST guarantee that at most one row per `(id_tenant, id_empresa,
ambiente)` has `activo = true AND deleted_at IS NULL`. Rotation MUST deactivate the superseded row
and activate the new one inside a single transaction.

#### Scenario: A second concurrent activation for the same empresa+ambiente is refused
- GIVEN an active certificate for `(empresa 1, homologacion)`
- WHEN a raw INSERT attempts a second `activo = true` row for the same empresa and ambiente
- THEN Postgres rejects it via `ux_certificados_fiscales_activo`, SQLSTATE `23505`

#### Scenario: Rotation is atomic — no window with two active certificates
- GIVEN an active certificate `A` for `(empresa 1, homologacion)`
- WHEN a rotation registers certificate `B` and activates it
- THEN the transaction that deactivates `A` and activates `B` commits or rolls back as one unit —
  never a state where both are simultaneously active

### Requirement: Key Material Never Appears In Any DTO, Log, Or API Response

`clave_privada_cifrada`, `nonce`, and `tag_autenticacion` MUST be absent from every DTO returned by
`/api/fiscal/certificados`, and MUST never be logged. The decrypted key MUST exist only inside the
CMS signing call, cleared with `CryptographicOperations.ZeroMemory` afterward.

#### Scenario: Listing certificates never exposes key material
- GIVEN two registered certificates for a tenant
- WHEN `GET /api/fiscal/certificados` is called
- THEN the response contains `alias`, `ambiente`, `vigencia_desde/hasta`, and `activo`, and contains
  no field derived from `clave_privada_cifrada`, `nonce`, or `tag_autenticacion`
