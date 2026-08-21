# Spike de alineación — Etapa 18, Slice 1

> Design: `design.md:67-105` (decisiones 1-2, subject `A4-3x8`). Tasks: `tasks.md` slice 1, tareas
> 1.1-1.5 y Reconciliación 5 (spike partido en **1a autónomo** / **1b bloqueado en el dueño**).

## Qué mide este documento

Dos criterios de salida, ambos binarios y ambos requeridos antes de que el slice 3 pueda abrir
(verify criterio 4):

- **E1 (geometría física)** — requiere la impresora y la hoja troquelada de referencia del dueño.
  **Human-in-the-loop por naturaleza** (design.md:101-102): ningún agente de este pipeline puede
  ejecutarlo. Tarea **1.4**, registrada `[ ]` y **BLOQUEADA EN EL DUEÑO**.
- **E2 (no-regresión)** — no comparte esa restricción y corre en este entorno. Tarea **1.5**.

## E1 — Corridas físicas (una fila por corrida, a cargo del dueño)

**Instrucciones de medición para el dueño**:

1. Abrir la grilla de calibración (`HojaDeEtiquetas` en `modo="calibracion"`, subject `A4-3x8` —
   la geometría más ajustada, borde a borde, sin canaletas: si el margen no imprimible del
   hardware recorta columnas, este es el subject que lo va a mostrar primero).
2. Configurar impresión según el bloque `d-print-none` en pantalla: **A4, escala 100% (nunca
   "ajustar a la página"), sin márgenes, gráficos de fondo activados**.
3. Imprimir sobre la hoja troquelada de referencia (Avery 3422 o equivalente del mercado local).
4. Medir con precisión ≥ 0.1 mm la desviación del **origen de cada celda** (la cruz de registro de
   6 mm) respecto de su posición nominal, en al menos: las **4 celdas de esquina**, la **celda
   central**, y **ambos extremos de la última fila**.
5. Medir el **cuadrado de escala**: si no mide 100.0 ± 0.3 mm, la corrida es **NULA** (no un FAIL)
   — algo en la cadena de impresión (driver, escala real aplicada) no está en 100%; corregir y
   repetir antes de registrar un veredicto.
6. Calcular la **deriva acumulada de la última fila** (diferencia entre el primer y el último
   extremo medido en esa fila).
7. Verificar E1: **todas** las desviaciones de origen dentro de **±1.0 mm** de nominal **y** la
   deriva acumulada de la última fila dentro de **±1.5 mm** ⇒ **PASS**. Cualquier medición fuera de
   esos límites ⇒ **FAIL** (ver "Camino de FAIL" abajo).
8. Guardar evidencia (foto de la hoja medida con regla/calibre visible, o el archivo de medición)
   y anotar su ruta en la columna `Evidencia`.
9. Agregar la fila a la tabla de abajo y actualizar `tasks.md` tarea 1.4 a `[x]` **solo** cuando
   este documento tenga al menos una corrida con veredicto `PASS` en E1.

**Camino de FAIL**: STOP. No se cambia de librería en silencio — la decisión de licencia de
QuestPDF se escala al dueño como decisión comercial bloqueante (`proposal.md` OD1). El slice 4 es
independiente y puede avanzar mientras tanto (`design.md:387-389`).

| Fecha | Navegador + versión | SO | Impresora (marca/modelo) | Hoja de referencia | Escala / márgenes configurados | Cuadrado de escala medido (mm) | Desvío celda esquina 1 (mm) | Desvío celda esquina 2 (mm) | Desvío celda esquina 3 (mm) | Desvío celda esquina 4 (mm) | Desvío celda central (mm) | Desvío extremo 1 última fila (mm) | Desvío extremo 2 última fila (mm) | Deriva acumulada última fila (mm) | Veredicto E1 | Veredicto E2 | Evidencia (ruta) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| _(sin corridas — pendiente del dueño, tarea 1.4)_ | | | | | | | | | | | | | | | | | |

**Estado actual**: **sin corridas registradas.** Tarea 1.4 permanece `[ ]` y bloqueada en el
dueño. El slice 3 (`Etiquetas.tsx`) **no puede abrir** hasta que esta tabla tenga una fila con
`Veredicto E1 = PASS` (verify criterio 4, `tasks.md` Reconciliación 5).

## E2 — No-regresión (tarea 1.5, ejecutable en este entorno)

Tres pruebas, las tres corren sin la impresora/papel del dueño (`design.md:93-97`,
`tasks.md` Reconciliación 5):

| # | Prueba | Cómo se ejecuta | Resultado |
|---|---|---|---|
| 1 | `git diff --exit-code src/Ways.Web/src/estilos/impresion.css` limpio | Comando de shell, determinístico | Ver evidencia en el reporte de apply de este slice — `impresion.css` no aparece en el diff del slice (verify criterio 3, mutation target 2) |
| 2 | `CajaZ.test.tsx` / `CuentaCorriente.test.tsx` verdes y **sin editar** | `npx vitest run` sobre ambos archivos | Ver evidencia en el reporte de apply — ninguno de los dos archivos aparece en el diff del slice; ambas suites corren dentro de la corrida completa de `npx vitest run` |
| 3 | Comparación "Guardar como PDF" caja-a-caja de cada vista (`main` vs. la rama), mismo navegador/config | Impresión real desde un navegador contra la app corriendo (o un dev/headless print) | **NO completada en este pase de apply.** Ver nota abajo |

**Nota sobre la prueba 3 — gap reconocido, no corner-cut silencioso**: esta comparación requiere
una sesión de navegador real contra la app completa (API + Postgres + login) para navegar hasta
`CajaZ`/`CuentaCorriente` con datos reales y comparar el resultado impreso. El repo no tiene infra
de E2E (Playwright/Puppeteer) y la etapa tiene como dependencia vinculante *"no new web
dependency"* (`proposal.md:475`) — instalar una para esta única comparación violaría esa
restricción. Se registra como **pendiente**, a ejecutar manualmente (por el dueño o un desarrollador
con la app corriendo) antes de cerrar el veredicto E2 con las tres pruebas completas. Las otras dos
pruebas de E2, que sí son determinísticas y no requieren un navegador real, están **verdes**.

**Veredicto E2 provisional**: 2/3 pruebas verdes y determinísticas; la tercera queda pendiente de
una sesión manual. No se marca `PASS` completo hasta que las tres corran.
