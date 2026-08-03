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

            // Backstop genérico (judgment-day, slice 3 ronda 1) para las ~10 unicidades nuevas
            // de catálogos/parámetros/catálogos fiscales: mismo mecanismo de carrera que los
            // dos casos de arriba, pero agrupado por familia (a partir del nombre del índice,
            // que ya codifica qué se duplicó) en vez de repetir un caso por índice.
            DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: string ux } }
                when ClasificarUnicidad(ux) is { } familia =>
                (StatusCodes.Status409Conflict, familia.Titulo, familia.Codigo),

            // Backstop de la constraint que cierra la baja irreversible del Consumidor Final
            // (stage-2-clientes-proveedores, design decision 4, task 1.12): ReglaDeClientes.
            // ValidarNoConsumidorFinal ya bloquea el camino normal de ServicioDeClientes —
            // esto es el backstop ante un UPDATE/DELETE que la esquive directamente.
            DbUpdateException { InnerException: PostgresException { SqlState: "23514", ConstraintName: "ck_clientes_cf_protegido" } } =>
                (StatusCodes.Status409Conflict, "El cliente Consumidor Final no se puede editar ni eliminar.", "consumidor_final_protegido"),

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
            DbUpdateException { InnerException: PostgresException { SqlState: "23503", ConstraintName: string fk } }
                when fk.StartsWith("fk_", StringComparison.Ordinal) =>
                LogYClasificarReferenciaInvalida(fk, log),

            // Backstop genérico (db-error-backstops, judgment-day slice 3 ronda 1): cualquier
            // valor numérico que desborda la precisión/escala de su columna (p.ej. un margen o
            // un límite de crédito por encima de lo que valida la capa de servicio) llega acá
            // como 22003 en vez de dejar pasar un 500 — no está atado a una constraint puntual
            // porque numeric_value_out_of_range aplica por igual a cualquier columna numeric(p,s).
            DbUpdateException { InnerException: PostgresException { SqlState: "22003" } } =>
                (StatusCodes.Status400BadRequest, "El valor numérico está fuera de rango.", "valor_fuera_de_rango"),

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
        // familia: "_vigente" no aparece en ningún otro nombre de índice del esquema. Exenta de
        // la prueba de carrera exigida por `db-error-backstops` hasta la Slice 3
        // (ServicioDePrecios, task 3.11), que es donde aterriza el camino de escritura.
        if (nombreDeIndice.Contains("_vigente", StringComparison.Ordinal))
        {
            return ("precio_vigente_duplicado", "Ya existe un precio vigente para este artículo en esta lista.");
        }

        // ux_listas_precio_default_compartido/empresa (stage-2-clientes-proveedores, backstop
        // map): sin camino de escritura de cliente esta etapa (spec: listas_precio ABM Is Out
        // of Scope This Stage) — sembrado solo por provisioning/backfill, exento de race test
        // por el mismo motivo que la familia codigo_duplicado de los catálogos fiscales.
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
}
