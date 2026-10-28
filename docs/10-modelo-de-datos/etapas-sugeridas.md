> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · Etapas sugeridas (definido todo, implementado por partes)

## Etapas sugeridas (definido todo, implementado por partes)

| Etapa | Alcance | Desbloquea |
|---|---|---|
| 1 | Organización (doc 09) + padrones: áreas, categorías, marcas, grupos, condiciones fiscales, alícuotas, tipos de comprobante, medios de pago | Todo lo demás |
| — | **Extension point deferido (doc 09, `design.md` ADR-7):** el login por subdominio (`usuario@tenant.dominio.com`, flow A) sigue sin implementar — Etapa 1 solo entrega el login por `mail` (flow B, tenant-agnóstico). Flow A necesita una resolución de tenant *antes* de la sesión (`Host` header) y depende de wildcard DNS/TLS a nivel de hosting, un problema de despliegue ortogonal al modelo de datos. | — |
| 2 | Clientes y proveedores | Comprobantes |
| 3 | Artículos + códigos de barra + listas de precio + precios con historia | POS |
| 4 | Ofertas | POS completo |
| 5 | Comprobantes de venta + pagos + stock + movimientos | **Vender** (paridad núcleo) |
| 6 | Turnos de caja + arqueos + tesorería + pagos | Cerrar caja |
| 7 | Cuenta corriente + reliquidación a precio del día | Paridad total con el legacy |
| 8 | Comprobantes de compra + transferencias de stock (implementada — stage-8-compras-transferencias-inventario) | Superar al legacy |

El mapeo de migración del doc 03 se ajusta a estos nombres cuando se implemente cada
etapa; los datos del legacy entran en la etapa 5 (ventas históricas → `items_comprobante_venta`).

> **Cierre del programa (Etapa 8, stage-8-compras-transferencias-inventario): implementada —
> última fila de esta tabla.** Comprobantes de compra (§5, ciclo `borrador → confirmada →
> anulada`, `CalculadorDeCompra`, `precio_sugerido` como sugerencia nunca auto-aplicada),
> transferencias de stock (§6, dos movimientos espejados en una transacción) y un conteo de
> inventario mínimo (§6, decisión autónoma de alcance — motivo ya reservado por doc 10, sin
> workflow de snapshot/variance completo) están todos implementados, y la web correspondiente
> (`Compras`/`CompraEditor`/`Transferencias`/`ConteoDeInventario`, panel de saldo en
> `Proveedores`). No hay etapa 9 en este documento: cualquier trabajo remanente (inventario de
> conteo completo, órdenes de compra; el libro IVA compras ya está implementado, ver §5) es un cambio post-paridad normal, no
> una etapa nueva de este mapeo. El programa post-paridad (etapas 9 en adelante) vive en el
> [doc 11](../11-programa-post-paridad.md); las tablas nuevas que esas etapas introduzcan se
> documentan en este documento al implementarse.
>
> **Retirado (Etapa 15, stage-15-cc-proveedores-ledger):** el saldo de proveedor "derivado (§5,
> `Σ compras confirmadas − Σ pagos ligados`, sin tabla ni ledger extra — deliberadamente más
> simple que la cuenta corriente de clientes de la etapa 7)" que este párrafo describía queda
> retirado — reemplazado por `movimientos_cuenta_corriente_proveedor` (§8b), un ledger completo
> con la misma forma que el de clientes. El backfill de la migración de Etapa 15 reproduce esta
> fórmula exacta como el saldo inicial de cada proveedor, así que la lectura no cambia en el
> momento de la migración — solo su mecanismo, de una agregación en caliente a un libro auditable.

> **Etapa 20 (stage-20-organizacion-relaciones-y-bajas) — baja lógica guardada por uso, CERO
> DDL.** Esta etapa **no agrega, altera ni borra ninguna tabla ni columna**: la última migración
> sigue siendo la de la etapa 19a. Usa lo que ya estaba — `deleted_at` en todas las entidades y
> `estado` en `tenants` — para habilitar la baja de tenant, empresa, punto de venta y usuario:
>
> - **Nunca hay borrado físico.** Toda FK del modelo es `Restrict` y la baja es un `UPDATE` de
>   `deleted_at`; no existe un `DELETE FROM` sobre `tenants`, `empresas`, `puntos_venta` ni
>   `usuarios`. Deshacer una baja hecha por error es un `UPDATE` de una columna.
> - **"En uso" = lo que el cliente creó después de la línea base de aprovisionamiento**, medido
>   con una comparación estricta contra el `created_at` de la propia entidad. Un dependiente ya
>   dado de baja **sigue bloqueando**: la baja de la fila no borra que hubo operación.
> - **El conjunto de dependientes sale de la metadata de EF, no de una lista a mano.** Cada tipo
>   que referencia a la entidad se clasifica en directo / puenteado / ignorado, hay un
>   inventario ordenado versionado como golden, y una tabla nueva sin clasificar rompe un test
>   antes de llegar a producción. Toda tabla que este documento agregue en adelante entra por
>   ese inventario.
> - **Una fila de catálogo compartido (`id_empresa`/dueño `NULL`) no bloquea**, en coherencia con
>   la regla de catálogo compartido de §1 y del doc 09.
> - **La cascada está acotada a la organización y comparte un instante**: tenant → sus empresas,
>   sus puntos de venta y sus usuarios; empresa → sus puntos de venta. Los datos operativos
>   (ventas, compras, stock, caja, cuenta corriente) **no se cascadean**: son los que bloquean.
> - **Mínimos estructurales antes del guard de uso**: un tenant conserva al menos una empresa
>   viva (`ultima_empresa_del_tenant`) y una empresa al menos un punto de venta vivo
>   (`ultimo_punto_venta_de_la_empresa`). Los hermanos ya dados de baja no cuentan como vivos.
> - **Seis códigos `409` en total**: `tenant_en_uso`, `empresa_en_uso`, `punto_venta_en_uso`,
>   `usuario_en_uso` y los dos mínimos de arriba. Fuera de alcance de tenant la respuesta es
>   `404`, nunca un `409` que delate el estado de la fila.
>
> La baja de **empresa** y de **punto de venta** queda **latente** hasta que exista un alta que
> cree un segundo hermano: hoy el mínimo estructural dispara primero en todo intento vía API.
