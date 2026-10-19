using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Escritura de códigos sobre artículos existentes. Las dos herramientas aplican la misma política
/// que los endpoints del ABM (<see cref="Politicas.GestionDeCatalogo"/>) y escriben a través de
/// <see cref="ServicioDeArticulos"/>, el mismo camino que usa la web. Los nombres de los parámetros
/// están en snake_case porque el SDK los publica tal cual en el esquema de la herramienta.
/// </summary>
[McpServerToolType]
public sealed class HerramientasDeCatalogo
{
    private const string IndiceCodigoBarra = "ux_codigos_barra_codigo_tenant";
    private const string IndiceCodigoProveedor = "ux_codigos_proveedor_proveedor_codigo";

    private sealed record DuenoDelCodigo(int Id, string Nombre, string CodigoInterno, bool DadoDeBaja);

    [McpServerTool(
        Name = "asociar_codigo_de_barras",
        Title = "Asociar código de barras a un artículo",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Asocia un código de barras a un artículo de Ways. Requiere el rol Admin. " +
        "Si el código ya estaba asociado a ese artículo no cambia nada; si pertenece a otro artículo no escribe y lo informa. " +
        "Si el código no es un GTIN estándar (EAN-8, UPC-A, EAN-13 o GTIN-14 con dígito verificador correcto) no escribe hasta que " +
        "se repita la llamada con confirmar_codigo_no_estandar en true. " +
        "Cuando asocia el código, o ya estaba asociado, devuelve los códigos de barras actuales del artículo.")]
    public static Task<CallToolResult> AsociarCodigoDeBarrasAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor contextoHttp,
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        [Description("Id del artículo en Ways al que se asocia el código.")] int id_articulo,
        [Description("Código de barras tal como se lee (hasta 50 caracteres); se recortan los espacios de los extremos.")] string codigo,
        [Description("Dejarlo en false salvo que el usuario haya confirmado que los dígitos leídos son correctos aunque no sean un GTIN estándar.")]
        bool confirmar_codigo_no_estandar = false,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await ExigirAdminAsync(autorizacion, contextoHttp);

            return await AsociarCodigoDeBarrasComoAdminAsync(
                articulos, db, id_articulo, codigo, confirmar_codigo_no_estandar, ct);
        });

    private static async Task<string> AsociarCodigoDeBarrasComoAdminAsync(
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        int id_articulo,
        string codigo,
        bool confirmar_codigo_no_estandar,
        CancellationToken ct)
    {
        var limpio = (codigo ?? string.Empty).Trim();
        if (limpio.Length == 0)
        {
            throw Error("El código de barras está vacío. No se escribió nada.");
        }

        var articulo = await BuscarArticuloAsync(db, id_articulo, ct);
        if (articulo is null)
        {
            throw Error($"No existe el artículo {id_articulo}. No se escribió nada.");
        }

        if (await BuscarDuenoDelCodigoDeBarrasAsync(db, limpio, ct) is { } dueno)
        {
            return await RespuestaPorDuenoDelCodigoDeBarrasAsync(articulos, id_articulo, articulo.Value, limpio, dueno, ct);
        }

        if (!confirmar_codigo_no_estandar && ReglaDeGtin.MotivoDeNoEstandar(limpio) is { } motivo)
        {
            throw Error(
                $"El código {limpio} no parece un código de barras estándar: {motivo} " +
                "No se escribió nada. Hay que pedirle al usuario que confirme los dígitos leídos de la foto; " +
                "si el código es correcto tal como está, se puede volver a llamar con confirmar_codigo_no_estandar en true.");
        }

        try
        {
            await articulos.AgregarCodigoBarraAsync(id_articulo, new AltaCodigoBarra(limpio), ct);
        }
        catch (ErrorDominio error) when (error.Codigo == "codigo_barra_duplicado")
        {
            return await RespuestaTrasCarreraDeCodigoDeBarrasAsync(articulos, db, id_articulo, articulo.Value, limpio, error, ct);
        }
        catch (DbUpdateException error) when (EsViolacionUnica(error, IndiceCodigoBarra))
        {
            return await RespuestaTrasCarreraDeCodigoDeBarrasAsync(articulos, db, id_articulo, articulo.Value, limpio, error, ct);
        }
        catch (ErrorDominio error) when (error.EstadoHttp == 404)
        {
            throw Error($"No existe el artículo {id_articulo}. No se escribió nada.");
        }
        catch (ErrorDominio error)
        {
            throw Error($"No se pudo asociar el código: {error.Message} No se escribió nada.");
        }

        var actuales = await ListarCodigosDeBarrasAsync(articulos, id_articulo, ct);
        return
            $"Listo: se asoció el código de barras {limpio} al artículo {Describir(articulo.Value.Nombre, articulo.Value.CodigoInterno, id_articulo)}. " +
            $"Se escribió en la base. Códigos de barras actuales del artículo: {actuales}.";
    }

    [McpServerTool(
        Name = "asociar_codigo_de_proveedor",
        Title = "Asociar código de proveedor a un artículo",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Asocia a un artículo de Ways el código con que un proveedor lo imprime en su factura. Requiere el rol Admin. " +
        "Si ese código ya estaba asociado al mismo artículo para ese proveedor no cambia nada; si pertenece a otro artículo " +
        "del proveedor no escribe y lo informa. Cuando lo asocia, o ya estaba asociado, devuelve los códigos del artículo para ese proveedor.")]
    public static Task<CallToolResult> AsociarCodigoDeProveedorAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor contextoHttp,
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        [Description("Id del artículo en Ways al que se asocia el código.")] int id_articulo,
        [Description("Id del proveedor en Ways que imprime ese código.")] int id_proveedor,
        [Description("Código del proveedor tal como figura en su factura (hasta 50 caracteres); se recortan los espacios de los extremos.")] string codigo,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await ExigirAdminAsync(autorizacion, contextoHttp);

            return await AsociarCodigoDeProveedorComoAdminAsync(articulos, db, id_articulo, id_proveedor, codigo, ct);
        });

    private static async Task<string> AsociarCodigoDeProveedorComoAdminAsync(
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        int id_articulo,
        int id_proveedor,
        string codigo,
        CancellationToken ct)
    {
        var articulo = await BuscarArticuloAsync(db, id_articulo, ct);
        if (articulo is null)
        {
            throw Error($"No existe el artículo {id_articulo}. No se escribió nada.");
        }

        var razonSocial = await db.Proveedores
            .AsNoTracking()
            .Where(p => p.Id == id_proveedor)
            .Select(p => p.RazonSocial)
            .FirstOrDefaultAsync(ct);
        if (razonSocial is null)
        {
            throw Error($"No existe el proveedor {id_proveedor}. No se escribió nada.");
        }

        var limpio = (codigo ?? string.Empty).Trim();

        bool creado;
        try
        {
            creado = (await articulos.AgregarCodigoProveedorAsync(
                id_articulo, new AltaCodigoProveedor(id_proveedor, limpio), ct)).Creado;
        }
        catch (ErrorDominio error) when (error.Codigo == "codigo_proveedor_duplicado")
        {
            return await RespuestaPorCodigoDeProveedorTomadoAsync(db, id_articulo, id_proveedor, razonSocial, limpio, error, ct);
        }
        catch (DbUpdateException error) when (EsViolacionUnica(error, IndiceCodigoProveedor))
        {
            return await RespuestaPorCodigoDeProveedorTomadoAsync(db, id_articulo, id_proveedor, razonSocial, limpio, error, ct);
        }
        catch (ErrorDominio error) when (error.EstadoHttp == 404)
        {
            throw Error($"{error.Message} No se escribió nada.");
        }
        catch (ErrorDominio error) when (error.EstadoHttp == 400)
        {
            throw Error($"El código no es válido: {error.Message} No se escribió nada.");
        }
        catch (ErrorDominio error)
        {
            throw Error($"No se pudo asociar el código: {error.Message} No se escribió nada.");
        }

        var actuales = await ListarCodigosDeProveedorAsync(db, id_articulo, id_proveedor, ct);
        var destino = $"{Describir(articulo.Value.Nombre, articulo.Value.CodigoInterno, id_articulo)} para el proveedor {razonSocial} (id {id_proveedor})";

        return creado
            ? $"Listo: se asoció el código de proveedor {limpio} al artículo {destino}. Se escribió en la base. Códigos del artículo para ese proveedor: {actuales}."
            : $"El código de proveedor {limpio} ya estaba asociado al artículo {destino}. No se cambió nada. Códigos del artículo para ese proveedor: {actuales}.";
    }

    private static async Task ExigirAdminAsync(IAuthorizationService autorizacion, IHttpContextAccessor contextoHttp)
    {
        var usuario = contextoHttp.HttpContext?.User ?? new ClaimsPrincipal();
        if (!(await autorizacion.AuthorizeAsync(usuario, Politicas.GestionDeCatalogo)).Succeeded)
        {
            throw new ErrorDeHerramienta(
                "Esta herramienta requiere el rol Admin y el usuario que autorizó la conexión no lo tiene. No se escribió nada.");
        }
    }

    private static ErrorDeHerramienta Error(string texto) => new(texto);

    private static string Describir(string nombre, string codigoInterno, int id) =>
        $"{nombre} (código interno {codigoInterno}, id {id})";

    private static async Task<(string Nombre, string CodigoInterno)?> BuscarArticuloAsync(
        IWaysDbContext db, int idArticulo, CancellationToken ct)
    {
        var fila = await db.Articulos
            .AsNoTracking()
            .Where(a => a.Id == idArticulo)
            .Select(a => new { a.Nombre, a.CodigoInterno })
            .FirstOrDefaultAsync(ct);

        return fila is null ? null : (fila.Nombre, fila.CodigoInterno);
    }

    private static bool EsViolacionUnica(DbUpdateException error, string indice) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        pg.ConstraintName == indice;

    /// <summary>Un artículo dado de baja conserva sus códigos de barras vivos, que siguen bloqueando
    /// la reutilización: por eso el dueño se busca ignorando solo el filtro de baja lógica.</summary>
    private static async Task<DuenoDelCodigo?> BuscarDuenoAsync(
        IWaysDbContext db, IQueryable<int> idsDeArticulo, CancellationToken ct)
    {
        var idDueno = await idsDeArticulo.Select(id => (int?)id).FirstOrDefaultAsync(ct);
        if (idDueno is null)
        {
            return null;
        }

        return await db.Articulos
            .IgnoreQueryFilters(["BajaLogica"])
            .AsNoTracking()
            .Where(a => a.Id == idDueno)
            .Select(a => new DuenoDelCodigo(a.Id, a.Nombre, a.CodigoInterno, a.DeletedAt != null))
            .FirstOrDefaultAsync(ct);
    }

    private static Task<DuenoDelCodigo?> BuscarDuenoDelCodigoDeBarrasAsync(
        IWaysDbContext db, string codigo, CancellationToken ct) =>
        BuscarDuenoAsync(
            db,
            db.CodigosBarra.AsNoTracking().Where(c => c.Codigo == codigo).Select(c => c.IdArticulo),
            ct);

    private static Task<DuenoDelCodigo?> BuscarDuenoDelCodigoDeProveedorAsync(
        IWaysDbContext db, int idProveedor, string codigo, CancellationToken ct) =>
        BuscarDuenoAsync(
            db,
            db.CodigosProveedor
                .AsNoTracking()
                .Where(c => c.IdProveedor == idProveedor && c.Codigo == codigo)
                .Select(c => c.IdArticulo),
            ct);

    private static string DescribirDueno(DuenoDelCodigo dueno) =>
        Describir(dueno.Nombre, dueno.CodigoInterno, dueno.Id) + (dueno.DadoDeBaja ? " [dado de baja]" : string.Empty);

    private static async Task<string> ListarCodigosDeBarrasAsync(
        ServicioDeArticulos articulos, int idArticulo, CancellationToken ct)
    {
        var codigos = await articulos.ListarCodigosBarraAsync(idArticulo, ct);
        return codigos.Count == 0 ? "ninguno" : string.Join(", ", codigos.Select(c => c.Codigo));
    }

    private static async Task<string> ListarCodigosDeProveedorAsync(
        IWaysDbContext db, int idArticulo, int idProveedor, CancellationToken ct)
    {
        var codigos = await db.CodigosProveedor
            .AsNoTracking()
            .Where(c => c.IdArticulo == idArticulo && c.IdProveedor == idProveedor)
            .OrderBy(c => c.Id)
            .Select(c => c.Codigo)
            .ToListAsync(ct);

        return codigos.Count == 0 ? "ninguno" : string.Join(", ", codigos);
    }

    private static async Task<string> RespuestaPorDuenoDelCodigoDeBarrasAsync(
        ServicioDeArticulos articulos,
        int idArticulo,
        (string Nombre, string CodigoInterno) articulo,
        string codigo,
        DuenoDelCodigo dueno,
        CancellationToken ct)
    {
        if (dueno.Id != idArticulo)
        {
            throw Error(
                $"El código de barras {codigo} ya está asociado a otro artículo: {DescribirDueno(dueno)}. " +
                "No se escribió nada. El usuario tiene que decidir qué hacer con ese código.");
        }

        var actuales = await ListarCodigosDeBarrasAsync(articulos, idArticulo, ct);
        return
            $"El código de barras {codigo} ya estaba asociado al artículo {Describir(articulo.Nombre, articulo.CodigoInterno, idArticulo)}. " +
            $"No se cambió nada. Códigos de barras actuales del artículo: {actuales}.";
    }

    /// <summary>Otra request ganó la carrera por el código entre el chequeo y el alta: se vuelve a
    /// buscar al dueño y se responde como si el chequeo lo hubiera visto.</summary>
    private static async Task<string> RespuestaTrasCarreraDeCodigoDeBarrasAsync(
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        int idArticulo,
        (string Nombre, string CodigoInterno) articulo,
        string codigo,
        Exception error,
        CancellationToken ct)
    {
        var dueno = await BuscarDuenoDelCodigoDeBarrasAsync(db, codigo, ct);

        if (dueno is null)
        {
            throw Error($"No se pudo asociar el código {codigo}: {error.Message} No se escribió nada.");
        }

        return await RespuestaPorDuenoDelCodigoDeBarrasAsync(articulos, idArticulo, articulo, codigo, dueno, ct);
    }

    private static async Task<string> RespuestaPorCodigoDeProveedorTomadoAsync(
        IWaysDbContext db,
        int idArticulo,
        int idProveedor,
        string nombreProveedor,
        string codigo,
        Exception error,
        CancellationToken ct)
    {
        var dueno = await BuscarDuenoDelCodigoDeProveedorAsync(db, idProveedor, codigo, ct);

        if (dueno is null)
        {
            throw Error($"No se pudo asociar el código {codigo}: {error.Message} No se escribió nada.");
        }

        if (dueno.Id == idArticulo)
        {
            return
                $"El código de proveedor {codigo} ya estaba asociado al artículo {Describir(dueno.Nombre, dueno.CodigoInterno, dueno.Id)} " +
                $"para el proveedor {nombreProveedor} (id {idProveedor}). No se cambió nada.";
        }

        throw Error(
            $"El código {codigo} del proveedor {nombreProveedor} (id {idProveedor}) ya está asociado a otro artículo: {DescribirDueno(dueno)}. " +
            "No se escribió nada. El usuario tiene que decidir qué hacer con ese código.");
    }
}
