# CI: suite de integración en fragmentos

## Objetivo
Repartir `tests/Ways.IntegrationTests` en varios jobs de CI (uno por runner) para bajar el
tiempo de pared a la mitad y volver a un `timeout-minutes` con margen real.

## Problema
El job `backend-integration` corre la suite entera en un solo job. El 2026-10-11 tardó 24–28
minutos en runners rápidos y 41–43 en lentos (unos 3.200 tests, 27 min de `dotnet test` en la
corrida 38171837512). Con tope de 45 las corridas lentas se cancelaban sin ningún test en rojo
y bloqueaban `main`. El PR #386 sube el tope a 75 como parche; la suite sigue creciendo.

## Restricciones
- Una sola suite por daemon de Docker: se fragmenta por job (runners distintos), nunca con dos
  `dotnet test` en el mismo job.
- No cambia la semántica de los tests: ni colecciones, ni fixtures, ni atributos.
- El ruleset de `main` exige un check llamado `backend-integration`: queda como job agregador.
- `deploy-produccion.yml` solo mira la conclusión del workflow `ci`.

## Diseño
- Listado: `dotnet vstest <dll> --ListFullyQualifiedTests` (2.777 métodos, 239 clases hoy).
- Reparto: clases en orden ordinal, la clase de índice `i` va al fragmento `i mod N`. Es
  determinista para un commit y reparte parejo los prefijos pesados (Ventas*, Compras*).
- Cada fragmento arma su `--filter` por clase (`FullyQualifiedName~<clase>.`), corre con
  `--blame-hang-timeout` y sube el trx y el listado como artefacto.
- El agregador `backend-integration` falla si algún fragmento no terminó en `success` y
  verifica que los fragmentos sean disjuntos y que su unión sea el listado completo.
- `N` sale de la matriz (`strategy.job-total`): cambiar la cantidad es tocar una línea.

## Tareas
- [x] T1 — Script de reparto y verificación con sus tests (`.github/scripts/`). Ruta: delegada (2+ archivos).
- [x] T2 — `ci.yml`: matriz de fragmentos + agregador `backend-integration`. Ruta: delegada, mismo writer.
- [x] T3 — Medir los fragmentos en CI y confirmar `timeout-minutes`. Ruta: inline (una línea). Medido el 2026-10-19 con N=3: el fragmento 2 de #464 pasó sus 1.741 tests en 37 m 48 s y el job murió al tocar los 40; el fragmento 3 de #466 fue cancelado a los 40 m 21 s sin ningún test fallido. Resultado: N=4 y tope de 60 minutos.

## Criterios de aceptación
- El check `backend-integration` sigue existiendo y queda rojo si un fragmento falla, se cancela o se saltea.
- Unión de fragmentos = suite completa, sin clases repetidas, verificado en cada corrida.
- Cada fragmento tarda alrededor de `1/N` de la suite entera (N=4 desde el 2026-10-19).

## Verificación
- RED observado antes de implementar (`ModuleNotFoundError`); después, `python -m unittest` 30 en verde.
- Listado real: 2.777 métodos en 239 clases. Con N=2: 120 clases / 1.360 métodos y 119 / 1.417; filtros de unos 8.300 caracteres.
- Punta a punta sin Docker, con `PoliticasTests` y `RutasDeLaSpaTests`: un `dotnet test` por fragmento (20 y 43 resultados) y `verificar` en verde; con un solo fragmento falla por tests no ejecutados.
- Judgment-day: dos jueces ciegos, sin hallazgos severos.
- Tope inicial de 40 minutos por fragmento con N=2: la mitad de la peor corrida observada (43 min) son unos 22, más 1–2 de preparación. Después la suite creció con las etapas del conector MCP y, ya con N=3, los fragmentos rozaron el tope (ver T3): desde el 2026-10-19 corren N=4 fragmentos con tope de 60 minutos, unos 28 minutos de tests más la preparación por fragmento según esa medición.
- No se corrió la suite de integración en local. RDD apagado en este clon: sin revisión nativa.

## Entrega
Un solo PR sobre `main`. Choca con #386 en la línea de `timeout-minutes`: el que entre segundo se rebasa.
