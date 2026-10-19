using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Escritura de códigos sobre artículos existentes. Las dos herramientas aplican la misma política
/// que los endpoints del ABM (<see cref="Politicas.GestionDeCatalogo"/>) y escriben a través de
/// <see cref="ServicioDeArticulos"/>, el mismo camino que usa la web. El SDK publica los nombres de los
/// parámetros tal cual en el esquema de la herramienta; van en camelCase, como en el resto del conector.
/// </summary>
[McpServerToolType]
public sealed class HerramientasDeCatalogo
{
    private const string CodigoBarraDuplicado = "codigo_barra_duplicado";
    private const string CodigoProveedorDuplicado = "codigo_proveedor_duplicado";

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
        "se repita la llamada con confirmarCodigoNoEstandar en true. " +
        "Cuando asocia el código, o ya estaba asociado, devuelve los códigos de barras actuales del artículo.")]
    public static Task<CallToolResult> AsociarCodigoDeBarrasAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor contextoHttp,
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        ILogger<HerramientasDeCatalogo> log,
        [Description("Id del artículo en Ways al que se asocia el código.")] int idArticulo,
        [Description("Código de barras tal como se lee (hasta 50 caracteres); se recortan los espacios de los extremos.")] string codigo,
        [Description("Dejarlo en false salvo que el usuario haya confirmado que los dígitos leídos son correctos aunque no sean un GTIN estándar.")]
        bool confirmarCodigoNoEstandar = false,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await PermisoDelConector.ExigirAsync(
                autorizacion, contextoHttp, Politicas.GestionDeCatalogo, PermisoDelConector.MensajeSinRolAdminParaEscribir);

            return await AsociarCodigoDeBarrasComoAdminAsync(
                articulos, db, log, idArticulo, codigo, confirmarCodigoNoEstandar, ct);
        }, log);

    private static async Task<string> AsociarCodigoDeBarrasComoAdminAsync(
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        ILogger log,
        int idArticulo,
        string codigo,
        bool confirmarCodigoNoEstandar,
        CancellationToken ct)
    {
        var limpio = (codigo ?? string.Empty).Trim();
        if (limpio.Length == 0)
        {
            throw Error("El código de barras está vacío. No se escribió nada.");
        }

        var articulo = await BuscarArticuloAsync(db, idArticulo, ct);
        if (articulo is null)
        {
            throw Error($"No existe el artículo {idArticulo}. No se escribió nada.");
        }

        if (await BuscarDuenoDelCodigoDeBarrasAsync(db, limpio, ct) is { } dueno)
        {
            return await RespuestaPorDuenoDelCodigoDeBarrasAsync(articulos, idArticulo, articulo.Value, limpio, dueno, ct);
        }

        if (!confirmarCodigoNoEstandar && ReglaDeGtin.MotivoDeNoEstandar(limpio) is { } motivo)
        {
            throw Error(
                $"El código {limpio} no parece un código de barras estándar: {motivo} " +
                "No se escribió nada. Hay que pedirle al usuario que confirme los dígitos leídos de la foto; " +
                "si el código es correcto tal como está, se puede volver a llamar con confirmarCodigoNoEstandar en true.");
        }

        try
        {
            await articulos.AgregarCodigoBarraAsync(idArticulo, new AltaCodigoBarra(limpio), ct);
        }
        catch (ErrorDominio error) when (error.Codigo == CodigoBarraDuplicado)
        {
            return await RespuestaTrasCarreraDeCodigoDeBarrasAsync(articulos, db, idArticulo, articulo.Value, limpio, error.Message, ct);
        }
        catch (DbUpdateException error) when (ResultadoDeHerramienta.Clasificar(error, log) is { Codigo: CodigoBarraDuplicado } clasificado)
        {
            return await RespuestaTrasCarreraDeCodigoDeBarrasAsync(articulos, db, idArticulo, articulo.Value, limpio, clasificado.Titulo, ct);
        }
        catch (ErrorDominio error) when (error.EstadoHttp == 404)
        {
            throw Error($"No existe el artículo {idArticulo}. No se escribió nada.");
        }
        catch (ErrorDominio error)
        {
            throw Error($"No se pudo asociar el código: {error.Message} No se escribió nada.");
        }

        var actuales = await ListarCodigosDeBarrasAsync(articulos, idArticulo, ct);
        return
            $"Listo: se asoció el código de barras {limpio} al artículo {Describir(articulo.Value.Nombre, articulo.Value.CodigoInterno, idArticulo)}. " +
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
        ILogger<HerramientasDeCatalogo> log,
        [Description("Id del artículo en Ways al que se asocia el código.")] int idArticulo,
        [Description("Id del proveedor en Ways que imprime ese código.")] int idProveedor,
        [Description("Código del proveedor tal como figura en su factura (hasta 50 caracteres); se recortan los espacios de los extremos.")] string codigo,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await PermisoDelConector.ExigirAsync(
                autorizacion, contextoHttp, Politicas.GestionDeCatalogo, PermisoDelConector.MensajeSinRolAdminParaEscribir);

            return await AsociarCodigoDeProveedorComoAdminAsync(articulos, db, log, idArticulo, idProveedor, codigo, ct);
        }, log);

    private static async Task<string> AsociarCodigoDeProveedorComoAdminAsync(
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        ILogger log,
        int idArticulo,
        int idProveedor,
        string codigo,
        CancellationToken ct)
    {
        var articulo = await BuscarArticuloAsync(db, idArticulo, ct);
        if (articulo is null)
        {
            throw Error($"No existe el artículo {idArticulo}. No se escribió nada.");
        }

        var razonSocial = await db.Proveedores
            .AsNoTracking()
            .Where(p => p.Id == idProveedor)
            .Select(p => p.RazonSocial)
            .FirstOrDefaultAsync(ct);
        if (razonSocial is null)
        {
            throw Error($"No existe el proveedor {idProveedor}. No se escribió nada.");
        }

        var limpio = (codigo ?? string.Empty).Trim();

        bool creado;
        try
        {
            creado = (await articulos.AgregarCodigoProveedorAsync(
                idArticulo, new AltaCodigoProveedor(idProveedor, limpio), ct)).Creado;
        }
        catch (ErrorDominio error) when (error.Codigo == CodigoProveedorDuplicado)
        {
            return await RespuestaPorCodigoDeProveedorTomadoAsync(db, idArticulo, idProveedor, razonSocial, limpio, error.Message, ct);
        }
        catch (DbUpdateException error) when (ResultadoDeHerramienta.Clasificar(error, log) is { Codigo: CodigoProveedorDuplicado } clasificado)
        {
            return await RespuestaPorCodigoDeProveedorTomadoAsync(db, idArticulo, idProveedor, razonSocial, limpio, clasificado.Titulo, ct);
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

        var actuales = await ListarCodigosDeProveedorAsync(db, idArticulo, idProveedor, ct);
        var destino = $"{Describir(articulo.Value.Nombre, articulo.Value.CodigoInterno, idArticulo)} para el proveedor {razonSocial} (id {idProveedor})";

        return creado
            ? $"Listo: se asoció el código de proveedor {limpio} al artículo {destino}. Se escribió en la base. Códigos del artículo para ese proveedor: {actuales}."
            : $"El código de proveedor {limpio} ya estaba asociado al artículo {destino}. No se cambió nada. Códigos del artículo para ese proveedor: {actuales}.";
    }

    private static ErrorDeHerramienta Error(string texto) => new(texto);

    /// <summary>El código resultó tomado, por la verificación previa del servicio (<see cref="ErrorDominio"/> con
    /// <c>codigo_barra_duplicado</c> o <c>codigo_proveedor_duplicado</c>) o por el índice único (23505), pero la
    /// segunda búsqueda no ve a su dueño (por ejemplo, otra request lo asoció y alguien lo borró enseguida). En los
    /// dos casos el alta no llegó a escribir.</summary>
    private static ErrorDeHerramienta SinDuenoTrasLaCarrera(string codigo, string motivo) =>
        Error($"No se pudo asociar el código {codigo}. {motivo} No se pudo identificar a qué artículo pertenece. No se escribió nada.");

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
    /// buscar al dueño y se responde como si el chequeo lo hubiera visto. <paramref name="motivo"/> es el
    /// mensaje del servicio o el título que la API da al índice único, nunca el texto de la excepción.</summary>
    private static async Task<string> RespuestaTrasCarreraDeCodigoDeBarrasAsync(
        ServicioDeArticulos articulos,
        IWaysDbContext db,
        int idArticulo,
        (string Nombre, string CodigoInterno) articulo,
        string codigo,
        string motivo,
        CancellationToken ct)
    {
        var dueno = await BuscarDuenoDelCodigoDeBarrasAsync(db, codigo, ct);

        if (dueno is null)
        {
            throw SinDuenoTrasLaCarrera(codigo, motivo);
        }

        return await RespuestaPorDuenoDelCodigoDeBarrasAsync(articulos, idArticulo, articulo, codigo, dueno, ct);
    }

    private static async Task<string> RespuestaPorCodigoDeProveedorTomadoAsync(
        IWaysDbContext db,
        int idArticulo,
        int idProveedor,
        string nombreProveedor,
        string codigo,
        string motivo,
        CancellationToken ct)
    {
        var dueno = await BuscarDuenoDelCodigoDeProveedorAsync(db, idProveedor, codigo, ct);

        if (dueno is null)
        {
            throw SinDuenoTrasLaCarrera(codigo, motivo);
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
