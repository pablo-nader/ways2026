# Lectura en vuelo al guardar y término aplicado: pantallas hermanas

## Objetivo
Llevar a las pantallas hermanas la corrección que ya tienen `Personal.tsx` y `Proveedores.tsx` (PR #430).

## Problema
- **Defecto 1 (spinner colgado).** `cargar` baja `cargando` solo si su token sigue vigente. Una escritura acuña
  una generación nueva y descarta la lectura en vuelo; si la escritura se rechaza, la rama de fallo no recarga y
  la tabla queda en "Cargando…". Alcanzable cuando un control que inicia una escritura depende solo de
  `bloqueado` y queda habilitado durante una lectura.
- **Defecto 2 (término no aplicado).** El refresco posterior a una escritura recarga con el texto tipeado en la
  caja de búsqueda y no con el último término enviado.

## Alcance
Solo `src/Ways.Web/src/paginas`. Sin cambios de API ni de esquema. Cambio mínimo, mismo criterio que
`Personal.tsx`: `habiaLecturaEnVuelo` leído antes de acuñar el token y recarga en la rama de fallo; ref
`terminoVigente` para el refresco.

Fuera de alcance: el doble envío y la carrera de respuestas viejas de `Clientes.tsx` (no tiene token de
generación ni `ocupadoRef`); la prueba faltante de la condición `habiaLecturaEnVuelo` en `Personal.tsx`.

## Tareas
- [x] T1 — `PaginaCatalogo.tsx` y `Categorias.tsx`: defecto 1 en `guardar`. Ruta: delegada (2+ archivos). Commit `c2345d3b`.
- [x] T2 — `PuntosVenta.tsx`: defecto 1 en `guardarAlta` (`guardar`, `guardarModo` y la baja no son alcanzables con una lectura en vuelo). Ruta: delegada. Commit `9fdd3ff5`.
- [x] T3 — `Usuarios.tsx`: defecto 1 en `guardar` (`accion` y `confirmarBaja` no son alcanzables). Ruta: delegada. Commit `a3b43a22`.
- [x] T4 — `Clientes.tsx`: defecto 2 (archivo de pruebas nuevo). Ruta: delegada. Commit `be9f18ff`.

## Criterios de aceptación
- Cada corrección tiene una prueba escrita antes y vista fallar.
- La condición `habiaLecturaEnVuelo` de cada pantalla tiene una prueba que mata su mutación.
- `npm run lint`, `npm run build` y `npm run test` en verde en `src/Ways.Web`.
- Judgment-day con ronda limpia antes del PR.

## Entrega
Un solo PR (pronóstico: menos de 400 líneas). Estrategia `single-pr`.

## Verificación
- RED observado antes de cada corrección: PaginaCatalogo y Categorias "Unable to find ... Bebidas / Nike" (tabla en el spinner); PuntosVenta "Unable to find an element with the text: PV Centro"; Usuarios "Unable to find an element with the text: vendedor.sur" (dos pruebas); Clientes "expected '/clientes?busqueda=zzz' to be '/clientes'" (cuatro pruebas).
- Mutación de `habiaLecturaEnVuelo` (condición reemplazada por `true` o sin `if`): en cada pantalla falla la prueba de guarda ("expected ... to be called 1 times, but got 2 times"); en PuntosVenta falla además la prueba preexistente "un rechazo del servidor rinde su mensaje, deja el formulario abierto y no recarga la sesión".
- En PuntosVenta y Usuarios el error es compartido y la lectura lo limpia al aterrizar: tras releer se vuelve a rendir el motivo del rechazo.
- Alcanzabilidad: en PuntosVenta y Usuarios las filas (editar, modo, baja, desbloquear) viven dentro de la tabla, que no se renderiza mientras `cargando`; PuntosVenta solo lee al montar y Usuarios tiene la puerta modal, por lo que esas escrituras no pueden arrancar con una lectura en vuelo. Lo mismo vale para `confirmarBaja` en PaginaCatalogo y Categorias.
- `npm run lint`: sin errores (23 advertencias preexistentes). `npm run build`: correcto. `npm run test`: 233 archivos, 6953 pruebas en verde.

## Judgment-day
- Ronda 1 (`b0a63235`): sin críticos. Dos advertencias confirmadas por ambos jueces en `PuntosVenta.tsx` y `Usuarios.tsx`:
  el mensaje de rechazo re-aplicado tras la relectura podía caer sobre un formulario reabierto, y tapaba el aviso
  de una relectura fallida.
- Corrección (`b1cfc2e7`): la relectura de la rama de fallo no limpia el error (`conservarError`); se quitó la
  re-aplicación. Cuatro pruebas nuevas, vistas fallar antes. Suite web: 233 archivos, 6957 pruebas en verde;
  `npm run lint` con salida 0; `npm run build` correcto.
- Ronda 2 (solo el delta): ambos jueces dan los dos hallazgos por resueltos, sin hallazgos nuevos. Aprobado.
