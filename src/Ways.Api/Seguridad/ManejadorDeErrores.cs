using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Common;

namespace Ways.Api.Seguridad;

/// <summary>
/// Traduce los <see cref="ErrorDominio"/> a ProblemDetails con su código de negocio,
/// y cualquier otra excepción a un 500 genérico sin filtrar detalles internos.
/// </summary>
public class ManejadorDeErrores(
    IProblemDetailsService problemDetails,
    ILogger<ManejadorDeErrores> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext contexto, Exception excepcion, CancellationToken ct)
    {
        var (estado, titulo, codigo) = excepcion switch
        {
            ErrorDominio e => (e.EstadoHttp, e.Message, e.Codigo),

            // Backstop de la carrera entre el chequeo previo de `ServicioDeUsuarios` y el
            // `SaveChangesAsync`: dos requests concurrentes pueden pasar el chequeo y chocar
            // recién acá. Traduce el mismo 409 de negocio en vez de dejar pasar un 500 genérico
            // (que además sería un oráculo de enumeración cross-tenant: 409 vs 500 delataría si
            // el mail ya existe en otro tenant).
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: "ux_usuarios_mail" } } =>
                (StatusCodes.Status409Conflict, "El mail ya está en uso.", "mail_duplicado"),

            // Mismo backstop que el de arriba, para la otra unicidad de `usuarios`
            // (`usuario` por tenant, ADR-7): la misma carrera entre el chequeo previo de
            // `ServicioDeUsuarios` y el `SaveChangesAsync` puede chocar acá.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: "ux_usuarios_usuario" } } =>
                (StatusCodes.Status409Conflict, "El usuario ya existe.", "usuario_duplicado"),

            // stage-5-pos-ventas (Slice 3, task 3.10, db-error-backstops, design: Backstop Map
            // — "ordering trap"): ux_comprobantes_venta_numero tiene que resolverse ANTES de
            // llegar a ClasificarUnicidad (el caso genérico de más abajo) — su nombre contiene
            // "_numero", así que la rama genérica de esa substring (backstop de
            // ux_clientes_numero, "Ya existe un cliente con ese número") lo atraparía primero y
            // lo clasificaría mal. Exact-match acá, ARRIBA del caso genérico, mismo criterio
            // que el trap "_codigo_interno"/"codigos_barra" vs. "_codigo" dentro de esa
            // función — la diferencia es que ese trap se resuelve DENTRO de ClasificarUnicidad
            // (mismo Contains-chain) y este tiene que resolverse ANTES de siquiera llamarla.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string uxComprobante } }
                when string.Equals(uxComprobante, "ux_comprobantes_venta_numero", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un comprobante con ese número en este punto de venta y tipo.", "numero_de_comprobante_duplicado"),

            // Backstop genérico (judgment-day, slice 3 ronda 1) para las ~10 unicidades nuevas
            // de catálogos/parámetros/catálogos fiscales: mismo mecanismo de carrera que los
            // dos casos de arriba, pero agrupado por familia (a partir del nombre del índice,
            // que ya codifica qué se duplicó) en vez de repetir un caso por índice.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string ux } }
                when ClasificarUnicidad(ux) is { } familia =>
                (StatusCodes.Status409Conflict, familia.Titulo, familia.Codigo),

            // Backstop de defensa en profundidad (judgment-day ronda 1, item 3, stage-3-slice-2):
            // el .Distinct() del servicio ya evita el duplicado en el camino normal, pero esta
            // es la constraint real ante cualquier duplicado que lo esquive (p.ej. una carrera
            // entre dos PUT concurrentes sobre el mismo artículo). PK_articulos_empresas usa la
            // convención por default de EF (PascalCase, "PK_"), a diferencia del resto del
            // esquema (snake_case, doc 10) — match case-insensitive para no depender de esa
            // inconsistencia de nombre.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string pk } }
                when string.Equals(pk, "pk_articulos_empresas", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "La empresa ya está en el subconjunto de disponibilidad del artículo.", "empresa_duplicada_en_subset"),

            // Backstop de la constraint que cierra la baja irreversible del Consumidor Final
            // (stage-2-clientes-proveedores, design decision 4, task 1.12): ReglaDeClientes.
            // ValidarNoConsumidorFinal ya bloquea el camino normal de ServicioDeClientes —
            // esto es el backstop ante un UPDATE/DELETE que la esquive directamente.
            DbUpdateException { InnerException: PostgresException { SqlState: "23514", ConstraintName: "ck_clientes_cf_protegido" } } =>
                (StatusCodes.Status409Conflict, "El cliente Consumidor Final no se puede editar ni eliminar.", "consumidor_final_protegido"),

            // Backstop de esquema (judgment-day, slice 3 ronda 2, item 2; GATE-APROBADO
            // 2026-08-03) para "vigente_hasta > vigente_desde" en precios — ServicioDePrecios.
            // AbrirNuevoPrecioAsync ya lo garantiza en el camino de servicio (mismo código de
            // dominio, ver el chequeo simétrico contra la fila activa/el predecesor); esto cubre
            // una escritura cruda/fuera de banda que lo bypasee (misma familia que
            // ck_clientes_cf_protegido).
            DbUpdateException { InnerException: PostgresException { SqlState: "23514", ConstraintName: "ck_precios_ventana_valida" } } =>
                (StatusCodes.Status400BadRequest, "vigente_hasta no puede ser anterior a vigente_desde.", "vigente_desde_invalido"),

            // Backstop genérico para las FKs compuestas nuevas (fk_*_empresa, fk_categorias_padre,
            // fk_parametros_punto_venta, …): una referencia a una fila que no existe (o que
            // pertenece a otro tenant, invisible bajo RLS) llega acá como 23503 en vez de
            // dejar pasar un 500 — p.ej. un IdCategoriaPadre de otro tenant.
            //
            // El match por prefijo también atrapa FKs administradas por la plataforma (no solo
            // las alimentadas por input de cliente) — tradeoff deliberado (judgment-day, slice 3
            // ronda 2): preferimos convertir un eventual 500 no logueado en un 400 logueado
            // (ver el LogWarning de abajo) antes que mantener una lista cerrada de FKs que hay
            // que actualizar a mano en cada migración nueva.
            //
            // stage-3-articulos-y-precios (task 1.10, db-error-backstops): confirmado sin
            // cambio de código — el match por prefijo "fk_" de abajo ya cubre las 16 FKs nuevas
            // de esta etapa (fk_articulos_tenant/area/categoria/marca/grupo/proveedor_habitual/
            // alicuota_iva, fk_articulos_empresas_tenant/articulo/empresa,
            // fk_codigos_barra_tenant/articulo, fk_precios_tenant/articulo/lista_precio,
            // fk_numeraciones_articulos_tenant): todas siguen la convención fk_* del resto del
            // esquema, así que no hace falta un caso nuevo acá.
            //
            // stage-4-ofertas (Slice 1, task 1.7): confirmado sin cambio de código — el mismo
            // match por prefijo "fk_" ya cubre las 8 FKs nuevas de esta etapa
            // (fk_ofertas_tenant/empresa/articulo/grupo/categoria,
            // fk_ofertas_listas_tenant/oferta/lista_precio).
            //
            // stage-5-pos-ventas (Slice 3, task 3.13): confirmado sin cambio de código — el
            // mismo match por prefijo "fk_" ya cubre las FKs nuevas de las seis tablas de esta
            // slice (comprobantes_venta: tenant/punto_venta/cliente/empleado/tipo_comprobante/
            // comprobante_asociado; items_comprobante_venta: tenant/comprobante/articulo/area/
            // lista_precio/oferta/alicuota_iva; pagos_comprobante: tenant/comprobante/
            // medio_pago; stock: tenant/articulo/punto_venta; movimientos_stock: tenant/
            // articulo/punto_venta/punto_venta_destino/comprobante_venta/empleado;
            // movimientos_cuenta_corriente: tenant/cliente/punto_venta/empleado/
            // comprobante_venta/pago_comprobante) — todas siguen la convención fk_* del resto
            // del esquema.
            DbUpdateException { InnerException: PostgresException { SqlState: "23503", ConstraintName: string fk } }
                when fk.StartsWith("fk_", StringComparison.Ordinal) =>
                LogYClasificarReferenciaInvalida(fk, log),

            // stage-5-pos-ventas (Slice 3, task 3.11, db-error-backstops, design: Backstop Map):
            // pk_stock — exención documentada de prueba de carrera, misma familia que
            // pk_numeraciones_comprobante: el único escritor de stock (Slice 4/5, INSERT ...
            // ON CONFLICT DO UPDATE) nunca puede disparar 23505 por construcción. Defensa de
            // esquema pura, alcanzable solo por un INSERT crudo/fuera de banda, probada con SQL
            // directo.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string pkStock } }
                when string.Equals(pkStock, "pk_stock", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe stock cargado para ese artículo en ese punto de venta.", "stock_duplicado"),

            // stage-5-pos-ventas (Slice 3, task 3.12, db-error-backstops, design: Backstop Map):
            // las tres CHECKs nuevas de comprobantes_venta/pagos_comprobante/movimientos_stock
            // no comparten un prefijo común (a diferencia de "ck_ofertas_"), así que el guard
            // de esta rama llama directo a ClasificarCheckDeVentas (switch por nombre EXACTO,
            // nunca Contains) en vez de filtrar por StartsWith primero. ValidadorDePagos/
            // ReglaDeComprobantes/el camino de escritura de movimientos_stock (Slice 4/5) ya
            // validan los tres invariantes en el servicio — bajo operación normal ninguna de
            // las tres ramas es alcanzable, quedan como backstop de una escritura cruda/fuera
            // de banda (misma familia que ClasificarCheckDeOfertas).
            DbUpdateException { InnerException: PostgresException { SqlState: "23514", ConstraintName: string ckVenta } }
                when ClasificarCheckDeVentas(ckVenta) is { } checkVenta =>
                (checkVenta.EstadoHttp, checkVenta.Titulo, checkVenta.Codigo),

            // Backstop genérico (db-error-backstops, judgment-day slice 3 ronda 1): cualquier
            // valor numérico que desborda la precisión/escala de su columna (p.ej. un margen o
            // un límite de crédito por encima de lo que valida la capa de servicio) llega acá
            // como 22003 en vez de dejar pasar un 500 — no está atado a una constraint puntual
            // porque numeric_value_out_of_range aplica por igual a cualquier columna numeric(p,s).
            DbUpdateException { InnerException: PostgresException { SqlState: "22003" } } =>
                (StatusCodes.Status400BadRequest, "El valor numérico está fuera de rango.", "valor_fuera_de_rango"),

            // stage-4-ofertas (Slice 1, task 1.7, db-error-backstops, design decision 8):
            // switch por nombre EXACTO detrás de un guard de prefijo "ck_ofertas_" — a
            // diferencia de ClasificarUnicidad (match por Contains/sufijo), acá no hay riesgo
            // de colisión de substring porque los cuatro nombres son literales completos y
            // mutuamente exclusivos. Agregado DESPUÉS de las dos ramas exactas existentes
            // (ck_clientes_cf_protegido/ck_precios_ventana_valida): cero cambio de
            // comportamiento para esas dos. ReglaDeOfertas ya valida los cuatro invariantes en
            // el camino de servicio (Slice 1, dominio; Slice 2, ServicioDeOfertas) — bajo
            // operación normal ninguna de las cuatro ramas de abajo es alcanzable, quedan como
            // backstop de una escritura cruda/fuera de banda (misma familia que las dos ramas
            // de arriba).
            DbUpdateException { InnerException: PostgresException { SqlState: "23514", ConstraintName: string ckOferta } }
                when ckOferta.StartsWith("ck_ofertas_", StringComparison.Ordinal)
                    && ClasificarCheckDeOfertas(ckOferta) is { } checkOferta =>
                (checkOferta.EstadoHttp, checkOferta.Titulo, checkOferta.Codigo),

            // stage-4-ofertas (Slice 1, task 1.7): pk_ofertas_listas — la única superficie
            // genuinamente racy de esta etapa (design: Backstop Map). El replace-set de
            // ServicioDeOfertas (Slice 2: delete-all + insert transaccional, ids .Distinct()ed)
            // ya evita el duplicado en el camino normal; esto cubre dos PUT concurrentes que
            // reemplazan el mismo set de listas de una oferta y chocan acá — misma familia que
            // pk_articulos_empresas.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string pkOfertasListas } }
                when string.Equals(pkOfertasListas, "pk_ofertas_listas", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "La lista de precios ya está en el subconjunto de targeting de la oferta.", "oferta_lista_duplicada"),

            // stage-5-pos-ventas (Slice 2, task 2.6, db-error-backstops, design: Backstop Map):
            // pk_numeraciones_comprobante — exención documentada de prueba de carrera. A
            // diferencia de pk_ofertas_listas/PK_articulos_empresas (que SÍ tienen un camino de
            // escritura normal que puede chocar), el único escritor de esta tabla
            // (AsignadorDeNumeroComprobante) inserta con ON CONFLICT DO NOTHING — nunca puede
            // disparar 23505 por construcción. Esta rama queda como defensa de esquema pura,
            // alcanzable solo por un INSERT crudo/fuera de banda que bypasee el asignador
            // (misma familia que pk_stock, Slice 3), probada con SQL directo, no con una
            // carrera real.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string pkNumeracion } }
                when string.Equals(pkNumeracion, "pk_numeraciones_comprobante", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe una numeración para ese punto de venta y tipo de comprobante.", "numeracion_duplicada"),

            // Defensa en profundidad genérica (judgment-day, item 2, stage-4-ofertas): EF
            // interpreta un UPDATE/DELETE que afecta 0 filas de las esperadas (en vez de la 1
            // esperada por su predicado de PK) como un conflicto de concurrencia y lanza
            // DbUpdateConcurrencyException — p.ej. un segundo escritor cuyo DELETE apunta a filas
            // que otro escritor ya borró y comiteó primero. Sin este caso, eso llegaba como 500
            // crudo en vez de un 409 traducido.
            //
            // Colocado ACÁ, DESPUÉS de todos los casos `DbUpdateException { InnerException:
            // PostgresException {...} }` de arriba y ANTES del catch-all `_`, en vez de arriba de
            // todo junto a `ErrorDominio`: como `DbUpdateConcurrencyException` DERIVA de
            // `DbUpdateException`, esta posición es la única que estructuralmente GARANTIZA que
            // nunca puede eclipsar ninguno de esos casos más específicos (cada uno de ellos exige
            // además un `InnerException` de tipo `PostgresException` con un `SqlState`/
            // `ConstraintName` puntual — si alguna vez `DbUpdateConcurrencyException` llegara a
            // traer ese mismo shape de `InnerException`, el switch ya lo habría resuelto arriba
            // antes de llegar acá). Genérico a propósito (no ofertas-específico): cualquier
            // replace-set/edición concurrente que EF detecte como "0 filas afectadas" en
            // cualquier tabla cae acá con el mismo código estable.
            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict, "El registro fue modificado por otra operación concurrente; reintentá la operación.", "edicion_concurrente"),

            _ => (StatusCodes.Status500InternalServerError,
                  "Ocurrió un error inesperado.",
                  "error_interno")
        };

        if (estado >= 500)
        {
            log.LogError(excepcion, "Error no controlado en {Ruta}.", contexto.Request.Path);
        }

        contexto.Response.StatusCode = estado;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = contexto,
            Exception = excepcion,
            ProblemDetails = new ProblemDetails
            {
                Status = estado,
                Title = titulo,
                Extensions = { ["codigo"] = codigo }
            }
        });
    }

    /// <summary>Agrupa los índices únicos nuevos por familia a partir del sufijo de su
    /// nombre — evita repetir un caso por índice para <c>ux_areas_nombre_*</c>,
    /// <c>ux_marcas_nombre_*</c>, <c>ux_grupos_nombre_*</c>, <c>ux_medios_pago_nombre_*</c>,
    /// <c>ux_categorias_nombre_*</c>, <c>ux_alicuotas_iva_nombre</c>,
    /// <c>ux_condiciones_fiscales_codigo</c>, <c>ux_tipos_comprobante_codigo</c> y las dos de
    /// <c>parametros</c>. <c>ux_parametros_*</c> se resuelve antes que el resto porque no
    /// sigue el patrón "_nombre"/"_codigo".
    ///
    /// El match por sufijo es deliberadamente amplio a todo el esquema (judgment-day, slice 3
    /// ronda 2): no se restringe a una lista cerrada de índices, así que también cubre
    /// unicidades preexistentes solo de seed (p.ej. <c>ux_roles_nombre</c>) de forma inocua,
    /// porque esos índices nunca reciben un INSERT/UPDATE desde un camino de escritura de
    /// cliente. La familia <c>codigo_duplicado</c> de catálogos fiscales (alícuotas de IVA,
    /// condiciones fiscales, tipos de comprobante) hoy no tiene camino de escritura de cliente
    /// (son de solo lectura para un tenant), así que queda exenta de la prueba de carrera
    /// exigida por el punto de decisión del skill `db-error-backstops` — solo aplica el mapeo
    /// 23505, sin race test, hasta que exista un endpoint de alta/edición.</summary>
    private static (string Codigo, string Titulo)? ClasificarUnicidad(string nombreDeIndice)
    {
        if (nombreDeIndice is "ux_parametros_empresa" or "ux_parametros_punto_venta")
        {
            return ("parametro_duplicado", "Ya existe un parámetro con esa clave en este alcance.");
        }

        // stage-2-clientes-proveedores (task 1.12, backstop map): ux_proveedores_cuit —
        // spec "cuit Uniqueness Is Scoped Per Tenant". La exención de la prueba de carrera
        // (documentada en Slice 1/batch 4) cierra en la Slice 3: ServicioDeProveedores.
        // CrearAsync ya existe y ExigirCuitDisponibleAsync es un pre-chequeo best-effort, no
        // el backstop real — dos altas concurrentes con el mismo cuit pueden pasar las dos ese
        // chequeo y competir recién acá, en el SaveChangesAsync de la que pierde. La prueba de
        // carrera vive en ProveedoresEndpointsTests (task 3.5): a diferencia de
        // ux_clientes_numero, cuit es un valor provisto por el cliente HTTP (no asignado por
        // un contador atómico), así que no hay ningún lock de fila que serialice la carrera
        // por construcción — dos POST concurrentes con Task.WhenAll alcanzan (mismo patrón sin
        // rendezvous forzado que CatalogosTests, no el de ParametrosTests: acá el alta es un
        // INSERT incondicional con pre-chequeo, no un upsert).
        if (nombreDeIndice.Contains("_cuit", StringComparison.Ordinal))
        {
            return ("cuit_duplicado", "Ya existe un proveedor con ese CUIT en este tenant.");
        }

        // stage-2-clientes-proveedores (task 1.12, backstop map): ux_clientes_numero —
        // backstop del contador atómico de AsignadorDeNumeroCliente (spec: Atomic Per-Tenant
        // Numero Assignment); nunca debería chocar acá bajo operación normal.
        //
        // Ojo: la prueba de atomicidad y la prueba de backstop son cosas DISTINTAS (corregido,
        // judgment-day ronda 1). ClientesEndpointsTests.LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop
        // (Slice 2, task 2.5) prueba que dos altas concurrentes NUNCA disparan esta rama — el
        // lock de fila de `UPDATE ... RETURNING` sobre `numeraciones_clientes` ya serializa las
        // dos transacciones (mismo hallazgo que AsignadorDeNumeroClienteConcurrenciaTests,
        // Slice 1 batch 3), así que esa prueba demuestra la AUSENCIA de esta rama en operación
        // normal, no su backstop. El backstop en sí — que esta rama SÍ traduce el 23505 a 409
        // cuando algo bypassea el contador — lo prueba
        // BackstopClientesYProveedoresTests.UnaFilaConNumeroDuplicadoInsertadaPorFueraDelContadorViolaLaUnicidad
        // (INSERT crudo por SQL que fuerza el duplicado, sin pasar por el contador atómico).
        if (nombreDeIndice.Contains("_numero", StringComparison.Ordinal))
        {
            return ("numero_duplicado", "Ya existe un cliente con ese número en este tenant.");
        }

        if (nombreDeIndice.Contains("_nombre", StringComparison.Ordinal))
        {
            return ("nombre_duplicado", "Ya existe un registro con ese nombre en este alcance.");
        }

        // stage-3-articulos-y-precios (task 1.10, db-error-backstops, ordering care): las dos
        // ramas de acá ABAJO tienen que ir ANTES de la rama genérica "_codigo" — ambos nombres
        // de índice contienen esa substring ("ux_articulos_codigo_interno" arranca justo con
        // "_codigo_interno", "ux_codigos_barra_codigo_tenant" arranca con "_codigo" dentro de
        // "_codigos_barra") y el match por Contains es de arriba hacia abajo: sin este orden,
        // los dos caerían en silencio en la familia genérica codigo_duplicado en vez de su
        // propio código de dominio.
        //
        // stage-3-articulos-y-precios (Slice 2, task 2.8): la prueba de carrera de este
        // backstop ya no está exenta — ServicioDeArticulos.CrearAsync es el camino de
        // escritura real. A diferencia de `ux_clientes_numero` (contador atómico con lock de
        // fila) pero IGUAL que `ux_proveedores_cuit`, el valor autogenerado SÍ tiene un lock de
        // fila que serializa la carrera (numeraciones_articulos, design decision 6) — la
        // carrera GENUINA es la del `codigo_interno` provisto por el cliente HTTP (sin ningún
        // contador de por medio), probada en
        // ArticulosEndpointsTests.LaCreacionConcurrenteConElMismoCodigoInternoProvistoDaExactamenteUnGanador.
        if (nombreDeIndice.Contains("_codigo_interno", StringComparison.Ordinal))
        {
            return ("codigo_interno_duplicado", "Ya existe un artículo con ese código interno en este tenant.");
        }

        // stage-3-articulos-y-precios (Slice 2, task 2.9): la prueba de carrera de este
        // backstop ya no está exenta — ServicioDeArticulos.AgregarCodigoBarraAsync es el camino
        // de escritura real. `codigo` es siempre un valor provisto por el cliente HTTP, sin
        // contador ni lock de fila que serialice nada por construcción (misma familia que
        // `ux_proveedores_cuit`, no la de `ux_clientes_numero`), probada en
        // ArticulosEndpointsTests.LaCreacionConcurrenteConElMismoCodigoDeBarraDaExactamenteUnGanador.
        if (nombreDeIndice.Contains("codigos_barra", StringComparison.Ordinal))
        {
            return ("codigo_barra_duplicado", "Ya existe ese código de barras en este tenant.");
        }

        if (nombreDeIndice.Contains("_codigo", StringComparison.Ordinal))
        {
            return ("codigo_duplicado", "Ya existe un registro con ese código.");
        }

        // stage-3-articulos-y-precios (task 1.10): ux_precios_vigente — backstop de "at most
        // one pending future price" (design decisions 3/4). Sin colisión con ninguna otra
        // familia: "_vigente" no aparece en ningún otro nombre de índice del esquema.
        //
        // Slice 3 judgment-day ronda 1 (item 2), REEMPLAZA el comentario anterior sobre task
        // 3.11: ServicioDePrecios.AbrirNuevoPrecioAsync ahora toma un pg_advisory_xact_lock
        // determinístico por par (idArticulo, idListaPrecio) ANTES de leer nada de precios, así que
        // CUALQUIER escritura concurrente sobre el mismo par se serializa de verdad — el segundo
        // llamador espera el lock y, al retomarlo, ve el estado YA COMITEADO por el primero
        // (incluida la fila recién insertada si el primero fue el primer precio del par), y hace
        // un cierre-y-apertura legítimo en vez de chocar contra este índice. Por eso esta carrera
        // YA NO es alcanzable por el camino de servicio: el backstop sigue existiendo como
        // defensa de esquema, pero solo queda alcanzable por una escritura cruda/fuera de banda
        // que bypasee el servicio (misma familia que PK_articulos_empresas, Slice 2 judgment-day
        // ronda 2 — ver ArticulosEndpointsTests.UnaFilaDeSubsetDuplicadaInsertadaPorFueraDelServicioViolaLaPk).
        // La prueba HTTP de este par (antes "un ganador + un 409") se adaptó para probar la
        // serialización real en su lugar: PreciosEndpointsTests.
        // LaCreacionConcurrenteDeDosPrimerosPreciosSeSerializaYAmbosSuceden.
        if (nombreDeIndice.Contains("_vigente", StringComparison.Ordinal))
        {
            return ("precio_vigente_duplicado", "Ya existe un precio vigente para este artículo en esta lista.");
        }

        // ux_listas_precio_default_compartido/empresa (stage-2-clientes-proveedores, backstop
        // map): sembrado originalmente solo por provisioning/backfill (exento de race test en
        // esa etapa, sin camino de escritura de cliente). stage-3-articulos-y-precios (Slice 4,
        // task 4.1/db-error-backstops) cierra la exención: ServicioDeListasPrecio.
        // DesmarcarDefaultActualAsync es ahora el camino de escritura real del intercambio de
        // es_default — la carrera GENUINA (dos listas del mismo alcance compitiendo por
        // convertirse en la nueva default) queda probada en
        // ListasPrecioEndpointsTests.LaAsignacionConcurrenteDeEsDefaultAOtrasDosListasDaExactamenteUnGanador.
        if (nombreDeIndice.Contains("_default", StringComparison.Ordinal))
        {
            return ("default_duplicado", "Ya existe una lista de precios default en este alcance.");
        }

        return null;
    }

    /// <summary>Deja constancia en el log de qué FK disparó el backstop antes de traducirla a
    /// 400 (judgment-day, slice 3 ronda 2): como el match por prefijo también cubre FKs
    /// administradas por la plataforma, este warning es lo único que preserva observabilidad
    /// para ese caso — de otro modo pasaría de un 500 logueado a un 400 silencioso.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo) LogYClasificarReferenciaInvalida(
        string nombreDeFk, ILogger log)
    {
        log.LogWarning("Referencia inválida por la restricción {NombreDeFk}.", nombreDeFk);
        return (StatusCodes.Status400BadRequest, "La referencia indicada no existe.", "referencia_invalida");
    }

    /// <summary>stage-4-ofertas (Slice 1, task 1.7, tasks.md "Orchestrator Decisions Recorded
    /// This Phase" #1): switch por nombre EXACTO de las cuatro CHECKs de <c>ofertas</c> — el
    /// código de las dos exclusividades sigue el código pineado por <c>specs/ofertas/spec.md</c>
    /// (<c>oferta_alcance_invalido</c>/<c>oferta_beneficio_invalido</c>, no el nombre borrador
    /// de <c>design.md</c>), las otras dos siguen el nombre de <c>design.md</c> tal cual
    /// (ningún escenario de spec las pinea distinto).</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeOfertas(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_ofertas_alcance_exclusivo" =>
                (StatusCodes.Status400BadRequest,
                    "La oferta tiene que apuntar a exactamente un artículo, grupo o categoría.",
                    "oferta_alcance_invalido"),

            "ck_ofertas_beneficio_exclusivo" =>
                (StatusCodes.Status400BadRequest,
                    "La oferta tiene que definir exactamente un beneficio: precio unitario, porcentaje o importe fijo.",
                    "oferta_beneficio_invalido"),

            "ck_ofertas_ventana_valida" =>
                (StatusCodes.Status400BadRequest,
                    "La ventana de vigencia de la oferta es inválida.",
                    "ventana_de_oferta_invalida"),

            "ck_ofertas_dias_semana" =>
                (StatusCodes.Status400BadRequest,
                    "Los días de semana de la oferta tienen que ser valores de 1 a 7 sin repetir.",
                    "dias_semana_invalidos"),

            // Nombre inesperado detrás del guard de prefijo "ck_ofertas_" (p.ej. una CHECK nueva
            // agregada al esquema sin actualizar este switch): cae al mismo 500 genérico que
            // cualquier otro caso no mapeado (mismo patrón que ClasificarUnicidad — null en vez
            // de lanzar desde el exception handler).
            _ => null
        };

    /// <summary>stage-5-pos-ventas (Slice 3, task 3.12, tasks.md "Orchestrator Decisions
    /// Recorded This Phase" #2, design: Backstop Map): switch por nombre EXACTO de las tres
    /// CHECKs nuevas de <c>comprobantes_venta</c>/<c>pagos_comprobante</c>/
    /// <c>movimientos_stock</c> — sin prefijo compartido entre las tres tablas (a diferencia de
    /// <c>ClasificarCheckDeOfertas</c>, que sí puede filtrar por <c>"ck_ofertas_"</c> antes de
    /// llamar), así que el caso del switch de arriba llama directo a esta función.
    /// <c>vuelto_de_pago_negativo</c> se pinea DISTINTO del código de dominio
    /// <c>vuelto_invalido</c> de <c>ValidadorDePagos</c> (regla <c>Σ vuelto &gt; max(0, Σ
    /// importe − total)</c>): son dos familias de rechazo distintas — reusar el mismo texto de
    /// código las confundiría en un log o en el cliente.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeVentas(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_comprobantes_venta_numero_positivo" =>
                (StatusCodes.Status400BadRequest,
                    "El número del comprobante tiene que ser positivo.",
                    "numero_de_comprobante_invalido"),

            "ck_pagos_comprobante_vuelto_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El vuelto de un pago no puede ser negativo.",
                    "vuelto_de_pago_negativo"),

            "ck_movimientos_stock_cantidad_no_cero" =>
                (StatusCodes.Status400BadRequest,
                    "El movimiento de stock tiene que tener una cantidad distinta de cero.",
                    "movimiento_de_stock_sin_cantidad"),

            _ => null
        };
}
