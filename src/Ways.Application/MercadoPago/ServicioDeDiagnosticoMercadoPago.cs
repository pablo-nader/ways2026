using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Diagnóstico de solo lectura (Admin): qué devuelve Mercado Pago, con el token de la conexión de la empresa, para los pagos recibidos
/// en las últimas <c>horas</c> (los primeros 100; <c>Total</c> trae el conteo de Mercado Pago) y para la lista de reportes de
/// transacciones. Solo hace GET y no crea reportes; como cualquier llamada a Mercado Pago, un 401 marca la conexión para reconectar. Si la lista de reportes
/// falla con un error de Mercado Pago, los pagos igual se devuelven y la sección de reportes queda en <c>null</c> con el código; un
/// fallo de los pagos se propaga con el error de siempre (404 empresa, 409 sin conexión, 502/503 de Mercado Pago).
/// </summary>
public class ServicioDeDiagnosticoMercadoPago(IWaysDbContext db, IClienteApiMercadoPago api, IRelojDelSistema reloj)
{
    public const int HorasPorDefecto = 24;

    public const int HorasMaximas = 168;

    public const string CodigoDeHorasInvalidas = "horas_invalidas";

    public static int ValidarHoras(int? horas)
    {
        var valor = horas ?? HorasPorDefecto;
        if (valor is < 1 or > HorasMaximas)
        {
            throw new ErrorDominio(CodigoDeHorasInvalidas, $"Las horas deben estar entre 1 y {HorasMaximas}.", 400);
        }

        return valor;
    }

    public async Task<DiagnosticoDePagosRecibidosMercadoPago> PagosRecibidosAsync(
        int idEmpresa, int? horas, CancellationToken ct = default)
    {
        // El filtro de tenant de EF esconde las empresas de otros tenants y las dadas de baja.
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }

        var valor = ValidarHoras(horas);
        var hasta = reloj.Ahora;
        var desde = hasta.AddHours(-valor);

        var pagos = await api.BuscarPagosRecibidosAsync(idEmpresa, desde, hasta, ct);

        IReadOnlyList<ReporteDeTransaccionesMercadoPago>? reportes = null;
        string? errorReportes = null;
        try
        {
            reportes = await api.ListarReportesDeTransaccionesAsync(idEmpresa, ct);
        }
        catch (ErrorDominio error)
        {
            errorReportes = error.Codigo;
        }

        return new DiagnosticoDePagosRecibidosMercadoPago(desde, hasta, pagos.Total, pagos.Pagos, reportes, errorReportes);
    }
}
