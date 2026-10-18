using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Proveedores;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Api.ConectorMcp;

/// <summary>Lecturas para resolver los ids de una factura de proveedor antes de cargarla como compra.
/// Todas pasan por <see cref="PermisoDeCompras"/> y leen con los servicios y el <see cref="IWaysDbContext"/>
/// de la request, acotados al tenant del usuario por el filtro de EF y RLS.</summary>
[McpServerToolType]
public sealed class HerramientasDeCompras
{
    public const int MaximoDeProveedores = 10;

    public const int MaximoDeLineas = 100;

    public const int MaximoDeCandidatos = 5;

    private const int MaximoDePalabras = 6;

    [McpServerTool(
        Name = "contexto_de_compras",
        Title = "Contexto de compras",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Devuelve lo necesario para cargar una compra en Ways: los puntos de venta con su empresa, las alícuotas " +
        "de percepción por defecto de cada empresa, los tipos de comprobante de compra activos, las alícuotas de " +
        "IVA activas y el rol del usuario conectado. Solo para administradores.")]
    public static Task<CallToolResult> ContextoDeComprasAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor accesor,
        IContextoDeUsuario actual,
        ServicioDeOrganizacion organizacion,
        ServicioDeCatalogosFiscales catalogosFiscales,
        CancellationToken ct) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await PermisoDeCompras.ExigirAsync(autorizacion, accesor);

            var puntosVenta = await organizacion.ListarPuntosVentaAsync(ct);
            var empresas = await organizacion.ListarEmpresasAsync(ct);
            var tipos = await catalogosFiscales.ListarTiposComprobanteAsync(ct);
            var alicuotas = await catalogosFiscales.ListarAlicuotasIvaAsync(ct);

            List<string> lineas = [$"Rol del usuario: {actual.Rol}", string.Empty, "Puntos de venta (id | nombre | empresa):"];
            lineas.AddRange(puntosVenta.Select(p => $"- {p.Id} | {p.Nombre} | {p.IdEmpresa} {p.RazonSocialEmpresa}"));

            lineas.Add(string.Empty);
            lineas.Add("Empresas (id | razón social | percepción IIBB por defecto | percepción IVA por defecto):");
            lineas.AddRange(empresas.Select(e =>
                $"- {e.Id} | {e.RazonSocial} | {Porcentaje(e.AlicuotaPercepcionIibb)} | {Porcentaje(e.AlicuotaPercepcionIva)}"));

            lineas.Add(string.Empty);
            lineas.Add("Tipos de comprobante de compra (id | código | nombre | registra libro IVA | discrimina IVA):");
            lineas.AddRange(tipos
                .Where(t => t.Clase == ClaseComprobante.Compra)
                .Select(t => $"- {t.Id} | {t.Codigo} | {t.Nombre} | {SiNo(t.RegistraLibroIva)} | {SiNo(t.DiscriminaIva)}"));

            lineas.Add(string.Empty);
            lineas.Add("Alícuotas de IVA (id | nombre | porcentaje):");
            lineas.AddRange(alicuotas.Select(a => $"- {a.Id} | {a.Nombre} | {Porcentaje(a.Porcentaje)}"));

            return string.Join(Environment.NewLine, lineas);
        });

    [McpServerTool(
        Name = "buscar_proveedor",
        Title = "Buscar proveedor",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Busca proveedores de Ways por CUIT (ignorando guiones, puntos y espacios, en lo buscado y en lo guardado) " +
        "o, si no se pasa CUIT, por parte de la razón social o del nombre de fantasía. Devuelve id, razón social, " +
        "nombre de fantasía, CUIT, si está activo, si percibe IIBB, si percibe IVA, si sus precios incluyen IVA y " +
        "la empresa a la que pertenece (o todas). Solo para administradores.")]
    public static Task<CallToolResult> BuscarProveedorAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor accesor,
        IWaysDbContext db,
        [Description("CUIT del proveedor, con o sin guiones, puntos o espacios. Si viene, la búsqueda es solo por CUIT.")]
        string? cuit = null,
        [Description("Parte de la razón social o del nombre de fantasía, sin distinguir mayúsculas ni acentos. Se usa si no viene cuit.")]
        string? nombre = null,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await PermisoDeCompras.ExigirAsync(autorizacion, accesor);

            IQueryable<Proveedor> consulta;
            string criterio;
            if (!string.IsNullOrWhiteSpace(cuit))
            {
                var digitos = NormalizacionDeCuit.Digitos(cuit) ?? throw new ErrorDeHerramienta(
                    $"El CUIT \"{cuit}\" no es válido: tiene que tener {NormalizacionDeCuit.CantidadDeDigitos} dígitos " +
                    "(se aceptan guiones, puntos y espacios).");
                consulta = db.Proveedores.Where(p =>
                    p.Cuit != null && p.Cuit.Replace("-", "").Replace(".", "").Replace(" ", "") == digitos);
                criterio = $"el CUIT {digitos}";
            }
            else if (nombre?.Trim() is { Length: >= 2 } termino)
            {
                var patron = PatronDeContiene(termino);
                consulta = db.Proveedores.Where(p => Coincide(p.RazonSocial, patron) || Coincide(p.NombreFantasia, patron));
                criterio = $"un nombre que contenga \"{termino}\"";
            }
            else
            {
                throw new ErrorDeHerramienta("Pasá un cuit o un nombre de al menos 2 caracteres.");
            }

            var proveedores = await consulta
                .AsNoTracking()
                .OrderBy(p => p.RazonSocial)
                .ThenBy(p => p.Id)
                .Take(MaximoDeProveedores + 1)
                .Select(p => new
                {
                    p.Id,
                    p.RazonSocial,
                    p.NombreFantasia,
                    p.Cuit,
                    p.Activo,
                    p.PercibeIibb,
                    p.PercibeIva,
                    p.PreciosIncluyenIva,
                    p.IdEmpresa
                })
                .ToListAsync(ct);

            if (proveedores.Count == 0)
            {
                return $"No hay proveedores con {criterio}.";
            }

            List<string> lineas =
            [
                "Proveedores (id | razón social | fantasía | CUIT | activo | percibe IIBB | percibe IVA | precios incluyen IVA | empresa):"
            ];
            lineas.AddRange(proveedores.Take(MaximoDeProveedores).Select(p =>
                $"- {p.Id} | {p.RazonSocial} | {p.NombreFantasia ?? "-"} | {p.Cuit ?? "-"} | {SiNo(p.Activo)} | " +
                $"{SiNo(p.PercibeIibb)} | {SiNo(p.PercibeIva)} | {SiNo(p.PreciosIncluyenIva)} | " +
                $"{p.IdEmpresa?.ToString(CultureInfo.InvariantCulture) ?? "todas"}"));

            if (proveedores.Count > MaximoDeProveedores)
            {
                lineas.Add($"Hay más de {MaximoDeProveedores} coincidencias: afiná la búsqueda.");
            }

            return string.Join(Environment.NewLine, lineas);
        });

    [McpServerTool(
        Name = "buscar_articulos",
        Title = "Buscar artículos de una factura",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Resuelve en una sola llamada las líneas de una factura de un proveedor contra los artículos de Ways. Por " +
        "cada línea prueba, en orden y quedándose con la primera que encuentra algo: el código del proveedor " +
        "exacto (sin distinguir mayúsculas), el código de barras exacto y, por último, los artículos cuyo nombre " +
        "contiene todas las palabras de la descripción (las primeras 6 de al menos 2 letras). Devuelve por línea " +
        "la fuente del match y hasta 5 candidatos ordenados por nombre, con id, nombre, código interno, unidad de " +
        "venta, alícuota de IVA y si están inactivos. Hasta 100 líneas. Solo para administradores.")]
    public static Task<CallToolResult> BuscarArticulosAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor accesor,
        IWaysDbContext db,
        [Description("Id del proveedor de la factura, el que devolvió buscar_proveedor.")]
        int idProveedor,
        [Description("Las líneas de la factura, en el orden en que aparecen. Cada una necesita al menos un dato.")]
        IReadOnlyList<LineaDeFactura> lineas,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await PermisoDeCompras.ExigirAsync(autorizacion, accesor);

            if (lineas is not { Count: > 0 and <= MaximoDeLineas })
            {
                throw new ErrorDeHerramienta($"Pasá entre 1 y {MaximoDeLineas} líneas.");
            }

            if (lineas.Any(l => l is null))
            {
                throw new ErrorDeHerramienta("Cada línea tiene que ser un objeto con codigoProveedor, codigoBarras o descripcion.");
            }

            if (!await db.Proveedores.AnyAsync(p => p.Id == idProveedor, ct))
            {
                throw new ErrorDeHerramienta($"No existe el proveedor {idProveedor}. Buscalo antes con buscar_proveedor.");
            }

            List<string> salida = ["Candidatos (id | nombre | código interno | unidad | alícuota de IVA):"];
            for (var i = 0; i < lineas.Count; i++)
            {
                var (fuente, candidatos) = await ResolverLineaAsync(db, idProveedor, lineas[i], ct);
                salida.Add($"Línea {i + 1}: {fuente}");
                salida.AddRange(candidatos.Select(c =>
                    $"- {c.Id} | {EnUnaLinea(c.Nombre)} | {EnUnaLinea(c.CodigoInterno)} | {c.Unidad} | " +
                    (c.Alicuota is null ? "sin alícuota" : $"{EnUnaLinea(c.Alicuota)} (id {c.IdAlicuota})") +
                    (c.Activo ? string.Empty : " | inactivo")));
            }

            return string.Join(Environment.NewLine, salida);
        });

    private sealed record Candidato(
        int Id, string Nombre, string CodigoInterno, UnidadVenta Unidad, int? IdAlicuota, string? Alicuota, bool Activo);

    private static async Task<(string Fuente, List<Candidato> Candidatos)> ResolverLineaAsync(
        IWaysDbContext db, int idProveedor, LineaDeFactura linea, CancellationToken ct)
    {
        var codigoProveedor = linea.CodigoProveedor?.Trim();
        var codigoBarras = linea.CodigoBarras?.Trim();
        var palabras = (linea.Descripcion ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length >= 2)
            .Take(MaximoDePalabras)
            .ToList();

        if (string.IsNullOrEmpty(codigoProveedor) && string.IsNullOrEmpty(codigoBarras) && palabras.Count == 0)
        {
            return ("sin datos para buscar (hace falta un código o una palabra de al menos 2 letras)", []);
        }

        if (!string.IsNullOrEmpty(codigoProveedor))
        {
            var porCodigoProveedor = await ProyectarAsync(
                db,
                db.Articulos.Where(a => db.CodigosProveedor.Any(c =>
                    c.IdArticulo == a.Id && c.IdProveedor == idProveedor && c.Codigo == codigoProveedor)),
                ct);
            if (porCodigoProveedor.Count > 0)
            {
                return ("código del proveedor", porCodigoProveedor);
            }
        }

        if (!string.IsNullOrEmpty(codigoBarras))
        {
            var porCodigoBarras = await ProyectarAsync(
                db,
                db.Articulos.Where(a => db.CodigosBarra.Any(c => c.IdArticulo == a.Id && c.Codigo == codigoBarras)),
                ct);
            if (porCodigoBarras.Count > 0)
            {
                return ("código de barras", porCodigoBarras);
            }
        }

        if (palabras.Count > 0)
        {
            var porNombre = db.Articulos.AsQueryable();
            foreach (var palabra in palabras)
            {
                var patron = PatronDeContiene(palabra);
                porNombre = porNombre.Where(a => Coincide(a.Nombre, patron));
            }

            var candidatos = await ProyectarAsync(db, porNombre, ct);
            if (candidatos.Count > 0)
            {
                return ("nombre", candidatos);
            }
        }

        return ("sin coincidencias", []);
    }

    private static Task<List<Candidato>> ProyectarAsync(IWaysDbContext db, IQueryable<Articulo> articulos, CancellationToken ct) =>
        (from a in articulos
         join iva in db.AlicuotasIva on a.IdAlicuotaIva equals iva.Id into alicuotas
         from iva in alicuotas.DefaultIfEmpty()
         orderby a.Nombre, a.Id
         select new Candidato(
             a.Id, a.Nombre, a.CodigoInterno, a.UnidadVenta, iva != null ? iva.Id : null, iva != null ? iva.Nombre : null, a.Activo))
        .Take(MaximoDeCandidatos)
        .AsNoTracking()
        .ToListAsync(ct);

    private static string SiNo(bool valor) => valor ? "sí" : "no";

    /// <summary>La salida es una línea por dato: un salto de línea dentro de un nombre no puede
    /// abrir una línea nueva que Claude lea como otra línea de factura o como otro candidato.</summary>
    private static string EnUnaLinea(string texto) => texto.ReplaceLineEndings(" ");

    private static string Porcentaje(decimal? valor) =>
        valor is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) + " %" : "sin valor";
}

/// <summary>Una línea de la factura tal como la lee Claude; sirve cualquier combinación de datos.</summary>
public sealed record LineaDeFactura(
    [property: Description("Código del artículo según el proveedor, como figura en la factura.")] string? CodigoProveedor = null,
    [property: Description("Código de barras del artículo, si figura en la factura.")] string? CodigoBarras = null,
    [property: Description("Descripción del artículo en la factura.")] string? Descripcion = null);
