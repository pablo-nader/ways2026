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

            // Backstop genérico para las FKs compuestas nuevas (fk_*_empresa, fk_categorias_padre,
            // fk_parametros_punto_venta, …): una referencia a una fila que no existe (o que
            // pertenece a otro tenant, invisible bajo RLS) llega acá como 23503 en vez de
            // dejar pasar un 500 — p.ej. un IdCategoriaPadre de otro tenant.
            DbUpdateException { InnerException: PostgresException { SqlState: "23503", ConstraintName: string fk } }
                when fk.StartsWith("fk_", StringComparison.Ordinal) =>
                (StatusCodes.Status400BadRequest, "La referencia indicada no existe.", "referencia_invalida"),

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
    /// sigue el patrón "_nombre"/"_codigo".</summary>
    private static (string Codigo, string Titulo)? ClasificarUnicidad(string nombreDeIndice)
    {
        if (nombreDeIndice is "ux_parametros_empresa" or "ux_parametros_punto_venta")
        {
            return ("parametro_duplicado", "Ya existe un parámetro con esa clave en este alcance.");
        }

        if (nombreDeIndice.Contains("_nombre", StringComparison.Ordinal))
        {
            return ("nombre_duplicado", "Ya existe un registro con ese nombre en este alcance.");
        }

        if (nombreDeIndice.Contains("_codigo", StringComparison.Ordinal))
        {
            return ("codigo_duplicado", "Ya existe un registro con ese código.");
        }

        return null;
    }
}
