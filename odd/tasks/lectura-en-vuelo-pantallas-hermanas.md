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
- [ ] T1 — `PaginaCatalogo.tsx` y `Categorias.tsx`: defecto 1. Ruta: delegada (2+ archivos).
- [ ] T2 — `PuntosVenta.tsx`: defecto 1 en `guardarAlta`, y en `guardar` / `guardarModo` si son alcanzables con una lectura en vuelo. Ruta: delegada.
- [ ] T3 — `Usuarios.tsx`: defecto 1. Ruta: delegada.
- [ ] T4 — `Clientes.tsx`: defecto 2 (archivo de pruebas nuevo). Ruta: delegada.

## Criterios de aceptación
- Cada corrección tiene una prueba escrita antes y vista fallar.
- La condición `habiaLecturaEnVuelo` de cada pantalla tiene una prueba que mata su mutación.
- `npm run lint`, `npm run build` y `npm run test` en verde en `src/Ways.Web`.
- Judgment-day con ronda limpia antes del PR.

## Entrega
Un solo PR (pronóstico: menos de 400 líneas). Estrategia `single-pr`.

## Verificación
(pendiente)
