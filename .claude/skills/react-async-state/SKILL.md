---
name: react-async-state
description: "Trigger: React screen with async fetches/saves, form editing, useState + await, ABM screens in Ways.Web, stale response, double submit, loading flags, confirmation gate, delete button, modal, focus restore, double click, re-entrancy. Every async read that sets screen state ships with token/generation gating; every write ships with a full-window disabled state and a synchronous re-entrancy ref, and its own response is gated wherever the screen can unmount or be superseded while it is in flight."
license: Apache-2.0
metadata:
  author: gentleman-programming
  version: "1.0"
---

## Activation Contract

Load when writing or modifying any `src/Ways.Web` screen (or component) that combines
React state with async operations: fetch-then-set flows, form save flows, per-row
editors, cached sub-resource loads. Born from stage-3 Slice 5 (`Articulos.tsx`),
where five judgment-day rounds each found a new variant of the same defect class.

## Hard Rules

1. **Functional updaters build from `prev`, never from closure state.**
   `setX((prev) => ({ ...prev[k], ...patch }))` — NEVER `{ ...leerDeEstado(k), ...patch }`.
   A helper that reads component state inside an updater reintroduces the stale
   snapshot the updater exists to avoid (multiple dispatches in one async invocation
   silently clobber each other; `finally` runs even after a `catch`'s `return`).

2. **Every async read that sets screen state is token-gated, and a write's own response is
   gated wherever the screen can go away while it is in flight.**
   Keep a `useRef` token (or generation counter). The read captures the token at
   start; every state application after every `await` checks
   `ref.current === token` first. That includes the read that refreshes after a write
   (`++generacion.current`), each with its own generation. The WRITER'S OWN response
   keeps the same gate wherever the component can unmount or be superseded while the
   write is in flight (pages with navigation or links, closable panels and modals): a
   late 2xx must not navigate, close or reopen a panel, or overwrite what replaced it.
   `Articulos.guardar` captures and checks its own token (a modal can be closed or
   reopened); `NuevaFamilia.crear` asks `useMontado()` before navigating (the user can
   leave through the menu or Back). It may be omitted only inside a surface that truly
   cannot close or navigate during the write, or when the response only sets the
   screen's own state and re-reads it (`Familias.guardar`: an unmounted screen discards
   those setters).

3. **Every action that supersedes an in-flight read or gated write invalidates the token.**
   Open-for-edit, new-blank-form, cancel, save, delete — all of them bump the token
   BEFORE changing what is on screen. Document the invalidation contract on the ref,
   not just one pairwise case.

4. **`finally` blocks that reset flags shared between operations are token-gated too.**
   An unconditional `finally { setOcupado(false) }` from a stale read (or from a gated
   write that was superseded) re-enables the buttons of a NEWER in-flight operation →
   double-submit window (duplicate POST). A flag that only this write sets, like the ref
   of rule 11, is released in the `finally` without a gate: nothing else can be holding it.

5. **Disabled state covers the full window, per entity.**
   From click until the post-write refresh has landed: inputs AND submit stay inert
   (`guardando` + `refrescando` per-row/per-entity flags, not one page-level boolean).
   A page-level busy flag lies as soon as two entities can be operated in sequence.
   Do not reset a superseded form's busy flag before its replacement is actually
   mounted (an async open resets it in its token-gated post-await block, not before).

6. **A committed write is never reported as a failure.**
   Isolate the post-write refresh from the write's try/catch. Refresh failure gets a
   distinct, reassuring message ("se guardó, pero no se pudo actualizar la vista"),
   and a swallowing loader must rethrow (opt-in flag) when the caller needs to
   distinguish. Shared loaders called concurrently need a generation guard so the
   last-STARTED call wins, not the last-RESOLVED.

7. **No silent catch-to-empty for required reference data.**
   `.catch(() => setCatalogo([]))` on data a required field depends on must also
   surface a visible aviso — and the aviso's copy must match real enforcement
   (if it says submission is blocked, `disabled` must actually block it).

8. **Key per-entity subtrees by entity id.**
   `key={entidad.id ?? 'nuevo'}` on the form/editor subtree so switching entities
   remounts and resets caches, drafts and suggestions. Caches keyed by a shared
   dimension (e.g. lista id) leak across entities without this.

9. **Block supersede-during-write; don't token-reconcile it.**
   While a WRITE is outstanding, disable every action that could supersede it
   (open-other-row, new, delete — not just the submit button). Trying to make
   supersede-during-save safe via token reconciliation mutated the bug across
   four consecutive review rounds (stale finally, same-row re-open, failed
   supersede leaving a resubmittable create, delete resurrection). Blocking the
   window kills the whole class of supersede by another action. What it cannot stop is
   the screen going away (menu, Back) or a panel closed and reopened: that is the
   writer's own-response gate of rule 2, not a reconciliation of the supersede.
   The handler's re-entrancy guard is the ref of rule 11 and nothing else: an
   `if (ocupado) return` on state repeats the `disabled` of the control that fires the
   handler: its `return` never executes (rule 15).
   Rethrows that signal failure to a caller are generation-gated like setters.

10. **ANY correctness pattern established on one surface is replicated across ALL
    sibling surfaces with the same interaction, in the same PR.** This covers
    error-recovery paths (self-heal of a race 409, refetch after rejection, stale-state
    aviso) AND data-honesty patterns (a filtered save mirrored in the totals/flags, an
    authoritative-response render, a confirmation gate). Three occurrences prove the
    class: the `turno_ya_abierto` self-heal implemented in `Caja.tsx` and omitted from
    the twin `PanelGateTurno` in the same PR (stage 7); the incomplete-line
    flags+counter built for `CompraEditor.tsx` and omitted from `Transferencias.tsx`
    built days later with the identical grid (stage 8) — the silent-drop bug shipped
    twice. Before committing a screen with a multi-line editor, a filtered request, or
    an error-recovery catch: grep the marker (error code, filter predicate name,
    counter copy) across `src/paginas` — every sibling must carry the same semantics.

11. **La guarda de reentrancia es un `ref`, no estado.**
    `if (ocupado) return` lee el snapshot del render; dos clics en el mismo tick pasan
    ambos porque el estado que gatea la guarda no se actualiza hasta el próximo render,
    y ambos disparan su propia escritura. Usar un `useRef<boolean>` sincrónico como
    espejo, seteado ANTES del primer `await` y liberado en el `finally` sin gate (nunca
    condicionado al token — la reentrancia se destraba siempre); el estado sigue
    existiendo aparte, solo para renderizar el disabled. Es la única guarda de reentrancia
    del handler: una guarda de estado delante de ella repite el `disabled` del botón y su
    `return` no se ejecuta nunca (regla 15).
    ```
    const bloqueadoRef = useRef(false);
    async function onGuardar() {
      if (bloqueadoRef.current) return;
      bloqueadoRef.current = true;
      setOcupado(true);
      try {
        await guardar();
      } finally {
        bloqueadoRef.current = false; // sin gate de token: siempre libera
        setOcupado(false); // solo esta escritura lo toca; si otra operación también lo cambiara, con gate (regla 4)
      }
    }
    ```
    Prueba que lo demuestra: dos `element.click()` sincrónicos dentro de un mismo `act`,
    afirmar exactamente una request emitida. (Observado dos veces: el sobreviviente M35
    de la ronda de judgment-day del slice 2 de la etapa 20 reapareció como un DELETE
    doble real en el slice 5.)

12. **Capturar el destino de restauración de foco en el handler del evento, nunca en
    un efecto.** Un `useEffect` pasivo corre DESPUÉS del commit que deshabilitó el
    disparador; el navegador ya aplicó la regla de "focus fixup" y movió el foco a
    `<body>`, así que `document.activeElement` leído en el efecto captura `body`, no el
    elemento a restaurar. Capturar `evento.currentTarget` de forma sincrónica dentro del
    `onClick` y pasarlo como dato al flujo de confirmación/cierre.
    ```
    function onClick(evento: React.MouseEvent<HTMLButtonElement>) {
      const disparador = evento.currentTarget;
      abrirConfirmacion({ alCerrar: () => disparador.focus() });
    }
    ```
    En lugar de leer `document.activeElement` dentro de un `useEffect` posterior al
    deshabilitado del control.
    Advertencia: **jsdom no implementa la regla de focus-fixup**, así que un test de
    restauración de foco verde en jsdom no es evidencia de comportamiento real de
    navegador — el test debe simular el blur explícitamente dentro del mismo `act` y
    dejarlo escrito en un comentario doc que aclare esa limitación.

13. **Una compuerta de confirmación debe declarar el nivel en el que es inerte.**
    "Modal" solo es cierto si TODO control activable de ese nivel queda bloqueado
    (`bloqueado = ocupado || puertaAbierta` en cada `disabled`, los links reciben
    `preventDefault` explícito — `aria-disabled` y `pointer-events` en CSS son solo
    indicativos, no bloquean teclado ni activación por tecnología asistiva). Si la
    escritura lleva su propio token (regla 2), se acuña en CONFIRMAR (primera sentencia
    sincrónica después de la guarda de ref de la regla 11), nunca al abrir la compuerta;
    un cancelar no debe suceder a nada ni incrementar la generación; una respuesta 2xx
    cierra y refresca, y una 4xx renderiza el error, mientras la pantalla siga siendo la
    que preguntó (regla 2). Si la barra de navegación de la aplicación sigue viva
    mientras la compuerta está abierta, llamarla "pantalla inerte", no "modal", y no
    poner `aria-modal` salvo que exista una trampa de foco real (un banner de error
    fuera de un diálogo `aria-modal` es inalcanzable para tecnología asistiva).

14. **Un slot de estado tiene un solo dueño.** Dos generaciones independientes
    escribiendo el mismo slot `error` se pisan entre sí (un fallo rápido seguido de un
    éxito lento borra el fallo previo). Cada fuente asíncrona que reporta un error
    obtiene su propio estado renderizado; un mensaje que reporta una precondición
    vigente no debe ser limpiado por una acción no relacionada.

15. **Una guarda de handler que repite el `disabled` de su botón es código muerto.**
    React no despacha `onClick` en un botón `disabled`, y la compuerta de confirmación ya
    ignora "Cancelar" y Escape mientras `ocupado`: `if (ocupado) return` en `pedirX`,
    `cancelarX`, `abrirX` o `guardar` no puede ejecutarse, ninguna prueba lo mata y la
    mutación sobrevive. Lo que vive es la guarda de ref de la regla 11, porque el disparo
    no pasa por un botón deshabilitado en el mismo tick: el `submit` de un formulario y el
    doble clic síncrono sobre el botón que confirma (dos `click()` en el mismo `act`, una
    sola request). Un formulario con su botón por defecto deshabilitado tampoco se envía
    con Enter, así que una guarda de estado en su `onSubmit` solo la alcanza una palanca
    sintética (`fireEvent.submit`): se la escribe únicamente con una prueba que lo diga y
    la mate. Antes de escribir una guarda, mutarla: si sobrevive, no se escribe.

16. **Cada acción que empieza borra lo que dijo la anterior, y cada una lleva su prueba.**
    El aviso de éxito o el rechazo de la acción previa no pueden quedar en pantalla
    mientras la nueva está abierta o en vuelo (`setError('')` / `setAviso('')` al abrir,
    confirmar, reintentar, cambiar lo elegido o volver a previsualizar). Ningún camino
    feliz ejercita esas líneas: la prueba deja el mensaje visible, dispara la acción
    siguiente con su request pendiente y afirma que el mensaje ya no está, una por
    hermana (regla 15 de `mutation-proof-tests`). Una limpieza que otra ya hizo antes en
    todo camino alcanzable es redundante y no se escribe.

## Decision Gates

| Situation | Action |
|---|---|
| New async fetch whose response calls a state setter | Token/generation guard before EVERY setter after EVERY await |
| New save flow | Full-window disabled state + synchronous ref guard (rule 11); own-response gate (token in a modal, `useMontado` in a page) when the response can navigate, close or reopen a panel, or overwrite a successor (rule 2) |
| Write whose response navigates | `if (!montado.current) return` before `navigate` (rule 2) |
| Post-write refresh added | Separate try/catch + distinguishable message |
| Helper reads state inside a functional updater | Rewrite to use `prev` |
| Aviso copy claims a block | Wire the actual `disabled` enforcement in the same commit |
| Error-recovery path added to one surface | Grep the error code; replicate in every sibling surface of the PR |
| `if (ocupado) return` en un handler cuyo botón ya está `disabled` | Mutar la guarda; si sobrevive, borrarla (regla 15) |
| Acción nueva en una pantalla con aviso o error visible | Borrar lo previo al empezar y probarlo con la request pendiente (regla 16) |

## Verification

Before committing, re-trace on paper at minimum: A-in-flight → open B; A-in-flight →
new/cancel; write in flight → the user leaves (menu, Back) and the response lands; plain
success (UI must still update!); plain failure (error must still render!); post-write
refresh failure. `tsc -b`, `oxlint`, `vite build` clean.
