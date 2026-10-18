using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
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

    private static string SiNo(bool valor) => valor ? "sí" : "no";

    private static string Porcentaje(decimal? valor) =>
        valor is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) + " %" : "sin valor";
}
