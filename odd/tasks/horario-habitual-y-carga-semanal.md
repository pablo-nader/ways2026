# Horario habitual y carga semanal de horas

Locator: `odd/tasks/horario-habitual-y-carga-semanal.md` · Engram: `odd/horario-habitual-y-carga-semanal/tasks`

## Objetivo

Que cargar las horas del personal deje de ser una carga por empleado y por día en un modal.
Cada empleado tiene un horario habitual y una grilla semanal precarga la semana con ese
horario; se ajusta lo que difiere y se guarda la semana entera de una vez.

## Problema y motivo

Hoy cada carga de horas es un asiento de `movimientos_personal` que se carga desde la cuenta
del empleado, de a uno, con fecha, horas y valor hora. Con varios empleados y todos los días
es muy incómodo (pedido del dueño, 2026-10-28). Además el tope de 24 h se valida por asiento
y no por día.

## Alcance

- Horario habitual en `personal`: horas por día de la semana (ISO 1 = lunes … 7 = domingo).
  Un día con 0 h es franco; sin horario cargado no se precarga nada.
- Grilla semanal (lunes a domingo × personal activo). Un día ya confirmado muestra lo cargado;
  uno sin confirmar muestra el horario habitual como precarga. Guardar confirma la semana.
- Editar un día ya confirmado escribe la diferencia en el libro, que sigue siendo append-only.
- Tope de 24 h por empleado y por día sobre la suma del día. Entre empleados no hay tope.

Fuera de alcance: fichaje con hora de entrada y salida, carga desde el POS, acreditación
automática sin confirmación.

## Decisiones

- D1 (dueño, 2026-10-28): el horario habitual solo precarga la grilla. Nada se acredita en el
  saldo hasta que el usuario guarda la semana. Se descartó un proceso que acredite solo cada día.
- D2: el libro sigue append-only (D4 de `personal-y-horas`). Bajar las horas de un día
  confirmado escribe un asiento de tipo `horas` con horas negativas; el CHECK pasa de
  `(0, 24]` a `[-24, 24]` sin cero. El valor hora de la reducción sale LIFO de los asientos
  positivos del día, para que el importe revierta exactamente lo acreditado.
- D3: tabla `jornadas_personal` con una fila por `(id_personal, fecha)` que marca el día como
  confirmado y guarda el total. Hace falta para distinguir "confirmado en 0 h" (faltó) de "sin
  cargar" (se precarga). La verdad del dinero sigue siendo el libro; la jornada se mantiene
  igual a la suma de horas del día en cada escritura, incluida la carga individual existente.
- D4: horario habitual como `horario_habitual numeric(4,2)[]` en `personal` (7 posiciones,
  cada una entre 0 y 24, NULL = sin horario), en línea con `ofertas.dias_semana`.
- D5: la grilla no precarga ni acepta días futuros (`fecha_futura`), para no acreditar horas
  no trabajadas al guardar a mitad de semana.
- D6: permisos iguales a los vigentes: lectura de la grilla para Admin y Supervisor
  (`LecturaPersonal`); guardar con `GestionDeCuentaDePersonal`; el horario habitual es parte
  del ABM de personal (Admin).

## Restricciones

- docs/10 es el contrato del esquema (scoping, nombres, RLS, FKs compuestas).
- Saldo e inserción del asiento en una escritura atómica bajo el lock de `personal`
  (skills `single-read-under-lock`, `ef-retry-safe-writes`).
- Guardas estructurales y migraciones con backstops (skills `structural-guard-tests`,
  `db-error-backstops`, `rls-migration-backfills`).

## Entrega

Estrategia: `auto-chain`, cadena `stacked-to-main`. Estimado: unas 2.000 líneas de autoría,
una rebanada por tarea. Cada rebanada pasa judgment-day y sale como PR con auto-merge.

## Tareas

- [ ] T1 — Horario habitual en `personal` (backend). Ruta: delegada (entidad, configuración,
  migración, contratos, servicio, tests).
  Aceptación: alta y edición aceptan `horarioHabitual` de 7 valores entre 0 y 24 con 2
  decimales, o null; otra longitud o valor fuera de rango responde 400
  `horario_habitual_invalido`; el listado y el detalle lo devuelven.
  Checks: dominio, aplicación e integración de personal; guardas estructurales.
  Evidencia (parcial): commit af1187d1; dominio 1936 OK, aplicación 1150 OK; integración de personal NO ejecutada (Docker no responde), pendiente. Ruta: delegada.
- [ ] T2 — Jornadas y carga semanal (backend). Ruta: delegada.
  `jornadas_personal`, CHECK de horas con signo, `GET /api/personal/horas/semana?desde=`
  (lunes) y `POST /api/personal/horas/semana`, tope diario y jornada en la carga individual.
  Aceptación: la grilla devuelve cargado, confirmado y precarga por día; guardar escribe solo
  diferencias; bajar horas revierte el importe exacto; superar 24 h por día responde 400;
  día futuro responde 400; confirmar en 0 h no se vuelve a precargar.
  Checks: dominio del cálculo de diferencias, integración del endpoint, concurrencia, RLS.
- [ ] T3 — Horario habitual en la pantalla Personal (web). Ruta: delegada.
  Aceptación: el formulario carga y edita las 7 horas; vacío = sin horario.
  Checks: Vitest de helpers y de la pantalla.
- [ ] T4 — Pantalla de carga semanal (web). Ruta: delegada.
  Aceptación: navegar semanas, precarga diferenciada visualmente, edición en celda, guardado
  único, días futuros deshabilitados, errores por celda.
  Checks: Vitest de helpers y de la pantalla; guarda de rutas de la SPA.

## Progreso

Exploración hecha el 2026-10-28. Siguiente paso: T1.
