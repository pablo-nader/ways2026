---
name: claims-match-code
description: "Trigger: writing or editing a doc-comment on a test, a test name, a comment that describes what a guard covers, UI copy that promises a behavior, or a PR line claiming what a test proves. The prose ships only if the code delivers exactly what it says — no more."
license: Apache-2.0
metadata:
  author: ways-project
  version: "1.0"
---

## Activation Contract

Load whenever prose is about to assert something about behavior: a `<summary>` on a
test, a test method name, an inline comment explaining what a guard defends, a user
facing message that promises a consequence, or a PR bullet claiming what is proven.

This is the sibling of `mutation-proof-tests`. That skill asks "can this test kill
the clause?". This one asks the cheaper, more frequently failed question: **does the
sentence next to the code describe what the code actually does?**

Born from four same-class findings across two slices of stage-desktop-pos, all caught
by judgment-day rather than by the author:

1. `VincularUnDispositivoAUnPuntoVentaWebDaModoIncompatible` shipped a `<summary>`
   claiming it covered "los dos statements a la vez" — the best-effort pre-check AND
   the re-check under `FOR UPDATE`. For a punto de venta already Web at load time the
   pre-check throws before the transaction opens, so the re-check is never reached.
   The test was fine; the sentence was false.
2. `UnHeaderAuthorizationInvalidoNoCaeALaCookieAunConSesionCookieValida` kept a
   doc-comment saying any `Authorization` header authenticates "EXCLUSIVAMENTE por
   bearer". The same commit had just narrowed the selector to the `Bearer ` prefix,
   so `Basic`/`Dispositivo` now fall through to the cookie. The behavior changed and
   the prose did not.
3. `PantallaDeVinculacion` told the admin the device was linked and to revoke and
   re-pair, with a comment asserting they would "nunca reintentar a ciegas desde esta
   misma pantalla" — while the `catch` reset `enviando` and re-enabled the select,
   the input and the submit button with the values still loaded.
4. `los_permisos_remotos_pueden_escribir_pero_no_leer_la_credencial_de_dispositivo`
   asserted membership in a `&[&str]` constant, while the PR described it as proving
   the runtime capability denial. It proved the constant.

## The rule

Prose may describe what the code does, or less. It may never describe more.

When you cannot make the code match the claim in the same commit, weaken the claim —
do not leave the stronger sentence standing and plan to catch up later.

## Never cite another file by line number

A cross-file citation of the form `OtroArchivo.cs:1130-1135` is a claim about what
lives at those lines, and it rots on its own: the target file moves and the sentence
keeps pointing somewhere else, now confidently wrong. Cite the **member** —
`ServicioDeVentas.UpsertStockLoteAsync` — or a `<see cref="..."/>`, which the compiler
checks for you.

Measured, not assumed: a sweep of every `ServicioDeVentas.cs:<línea>` citation in
`src/` found **4 of 4 drifted**, by 30 to 500 lines.

| Citing site | Claimed | Actually lives at |
|---|---|---|
| `ServicioDeRemitos.EjecutarAnulacionAsync` | `:1130-1135` (reversa sin chequeo de negativo) | `UpsertStockLoteAsync`'s doc-comment |
| `AsignadorDeNumeroComprobante` | `:342-349` (huecos sí, duplicados no) | the `gaps are accepted` comment, ~`:374` |
| `ServicioDeOrdenesDeCompra.EnviarAsync` | `:278-280` (numerar en transacción propia) | the `estrategiaNumeracion` draw, ~`:385-387` |
| `20260819195638_PresupuestosEtapa17.Down` | `:930` (`\|\| !tipo.AfectaStock`) | ~`:1429` |

The one fixed in that sweep drifted twice over: it read `:537` when a reviewer found it
and `:567` by the time the fix landed. A citation that moves while you are correcting it
is telling you the format is wrong, not the number.

The same applies to citations into docs (`docs/10-modelo-de-datos.md:1387-1390`): prefer
the section anchor (`doc 10 §9.1`) over the line, and check the section number is unique
in that file before leaning on it — doc 10 has two `## 9.` headings.

## Checks before the prose ships

| You wrote | Prove it or weaken it |
|---|---|
| "este test cubre X e Y" | Can X fail while Y still passes? If an earlier branch short-circuits, the test reaches one of them. Name only that one, and point at the test that reaches the other. |
| "esta guarda rechaza Z" | Delete the guard. If something else already rejects Z, the guard is not what rejects it. See `mutation-proof-tests`. |
| A message promising the user a consequence | Is that consequence wired? A copy that claims a block needs the `disabled` that enforces it, in the same commit (`react-async-state` rule 7). |
| "prueba que \<runtime behavior\>" | Does the assertion touch the runtime, or a constant/config the runtime happens to read? Say which, exactly. |
| A comment describing behavior you just changed | Re-read every comment in the hunks you touched. Stale prose is written by edits, not by authors. |

## When a claim cannot be proven

Say what is actually proven and what it rests on. A test whose doc-comment reads
"asserts the constant; relies on `registrar_capacidad_remota` staying a 1:1 mirror of
it" is honest and useful. The same test described as proving runtime denial is a trap
for whoever reads it next.

This is the same principle as the repo's `guardas inmatables no se shippean`
(PR #257): claim the ground you hold, not the ground you meant to hold.
