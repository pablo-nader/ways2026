# Atajos de importe sin instantánea

## Objetivo

Que F5 a F8 (y la barra de botones de atajo) del POS funcionen en la web y en el escritorio
antes de la primera sincronización, sin depender de la instantánea del punto de venta.

## Problema y causa raíz (reporte del dueño, 2026-10-14)

Con artículos por importe asignados a F5/F6/F7, la tecla siempre muestra
`MENSAJE_DE_ATAJO_SIN_DATOS`. `agregarPorAtajo`, `atajosDeImporte` y la barra de botones
leen solo `instantaneaDelPuntoVenta` (`sincronizacionOffline.instantanea`). Esa instantánea
viene de `GET /api/pos/instantanea`, que exige `Politicas.RequiereDispositivo`: en una
sesión de navegador responde 403, el error se traga en `useSincronizacionOffline` y la
instantánea queda `null` para siempre. En el escritorio pasa lo mismo hasta que la primera
sincronización tiene éxito.

## Alcance

- Lectura liviana y sin guarda de dispositivo: `GET /api/articulos/atajos-de-importe`, con la
  política del grupo (`OperacionDePos`, igual que el escaneo). Devuelve
  `{ teclaAtajo, idArticulo, codigoInterno, nombre }` de los artículos activos, disponibles a
  la venta, por importe y con tecla. Aislada por tenant (filtro global de EF); la tecla es
  única por tenant, así que no hay filtro de empresa.
- Web: con instantánea el mapa sale de ella; sin instantánea (y fuera de presupuesto) sale de
  la lectura online, cargada una vez por punto de venta con token de generación.
- Con el mapa online la tecla resuelve por el camino de escaneo online enviando
  `${importe}*${codigoInterno}`, igual que tipearlo.
- `MENSAJE_DE_ATAJO_SIN_DATOS` queda solo para cuando ninguna fuente está disponible.

## Fuera de alcance

- El servidor de escaneo, la instantánea y el contrato de `ArticuloEscaneado` no cambian.

## Tareas

- [ ] T1 Endpoint `atajos-de-importe` + test de integración (aislamiento, solo activos y vendibles por importe con tecla, rol).
- [ ] T2 Cliente web y estado en `Pos.tsx` (mapa `instantánea ?? online`, botones y tecla), tests de `Pos.test.tsx`.
- [ ] T3 Verificación: guardas estructurales, vitest, tsc, lint.

## Checks

- `dotnet test` focalizado + `SuperficieDeAutorizacion` (no aplica: GET) y `Ways.Application.Tests` completo.
- `npx vitest run src/paginas/Pos.test.tsx`, `tsc -b`, lint.

## Progreso

Ruta: escritor delegado único (backend y web son un mismo slice de ~250 líneas).
