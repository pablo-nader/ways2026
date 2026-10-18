using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;

namespace Ways.Api.ConectorMcp;

/// <summary>Lecturas para resolver los ids de una factura de proveedor antes de cargarla como compra.
/// Todas pasan por <see cref="PermisoDeCompras"/> y leen con los servicios de la request, acotados al
/// tenant del usuario por el filtro de EF y RLS.</summary>
[McpServerToolType]
public sealed class HerramientasDeCompras
{
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

    private static string SiNo(bool valor) => valor ? "sí" : "no";

    private static string Porcentaje(decimal? valor) =>
        valor is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) + " %" : "sin valor";
}
