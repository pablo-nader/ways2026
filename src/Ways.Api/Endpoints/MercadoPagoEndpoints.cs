using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.MercadoPago;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Api.Endpoints;

/// <summary>
/// Conexión de una empresa con Mercado Pago por OAuth. Inicio, lectura y desconexión son de Admin
/// (<see cref="Politicas.GestionDeCatalogo"/>); el callback es anónimo porque la URI de retorno registrada en Mercado Pago es
/// una sola y la sesión del comercio puede no existir en ese host: confía solo en el <c>state</c> protegido que armó el inicio.
/// </summary>
public static class MercadoPagoEndpoints
{
    public const string RutaDelCallback = "/api/integraciones/mercadopago/callback";

    /// <summary>Pantalla del back office donde el Admin ve y administra las empresas; vuelve acá con
    /// <c>?mercadopago=&lt;resultado&gt;&amp;empresa=&lt;id&gt;</c>.</summary>
    public const string RutaDeRetorno = "/organizacion/empresas";

    public static IEndpointRouteBuilder MapearMercadoPago(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/empresas/{idEmpresa:int}/mercadopago/conexion")
            .WithTags("Mercado Pago")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        admin.MapPost("/inicio", async (
            ServicioDeConexionMercadoPago servicio,
            int idEmpresa,
            SolicitudDeInicioDeConexion solicitud,
            HttpContext contexto,
            CancellationToken ct) =>
        {
            var origen = $"{contexto.Request.Scheme}://{contexto.Request.Host}";
            return Results.Ok(await servicio.IniciarAsync(idEmpresa, solicitud, origen, ct));
        })
        .WithSummary(
            "Arma la URL de autorización de Mercado Pago para la empresa; el navegador debe abrirla. 503 mercadopago_no_configurado " +
            "si el entorno no tiene credenciales.");

        admin.MapGet("/", (ServicioDeConexionMercadoPago servicio, int idEmpresa, CancellationToken ct) =>
            servicio.ObtenerAsync(idEmpresa, ct))
        .WithSummary("Estado de la conexión de la empresa con Mercado Pago. Nunca devuelve tokens.");

        admin.MapDelete("/", async (ServicioDeConexionMercadoPago servicio, int idEmpresa, CancellationToken ct) =>
        {
            await servicio.DesconectarAsync(idEmpresa, ct);
            return Results.NoContent();
        })
        .WithSummary("Desconecta la empresa de Mercado Pago y borra sus tokens cifrados.");

        app.MapGet(RutaDelCallback, async (
            ServicioDeConexionMercadoPago servicio,
            TenantActualDeSesion tenantActual,
            ILogger<ServicioDeConexionMercadoPago> log,
            string? code,
            string? state,
            string? error,
            CancellationToken ct) =>
        {
            var estado = servicio.LeerEstado(state);
            if (estado is null || !EsOrigenValido(estado.Origen))
            {
                return Results.Text(
                    "El enlace de conexión con Mercado Pago no es válido o venció. Volvé a Ways e iniciá la conexión de nuevo.",
                    "text/plain",
                    Encoding.UTF8,
                    StatusCodes.Status400BadRequest);
            }

            // El tenant sale del state protegido, nunca de un parámetro del navegador.
            tenantActual.Establecer(ModoDeAcceso.Tenant, estado.IdTenant);

            ResultadoDeConexion resultado;
            try
            {
                resultado = await servicio.CompletarAsync(estado, code, error, ct);
            }
            catch (DbUpdateException perdedora) when (ResultadoDeCarrera(perdedora, log) is not null)
            {
                resultado = ResultadoDeCarrera(perdedora, log)!.Value;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falló el cierre de la conexión con Mercado Pago de la empresa {IdEmpresa}.", estado.IdEmpresa);
                resultado = ResultadoDeConexion.Error;
            }

            return Results.Redirect(UrlDeRetorno(estado, resultado));
        })
        .AllowAnonymous()
        .WithSummary(
            "Retorno de OAuth de Mercado Pago: canjea el código, guarda la conexión cifrada y redirige a la pantalla de la empresa " +
            "con ?mercadopago=conectado|cancelado|error|cuenta_en_uso|ya_conectada.");

        return app;
    }

    /// <summary>Un 23505 de alguno de los dos índices únicos de la conexión, clasificado por <see cref="ManejadorDeErrores"/>
    /// como el resto de los backstops; cualquier otra excepción de base no es una carrera de esta conexión.</summary>
    internal static ResultadoDeConexion? ResultadoDeCarrera(DbUpdateException excepcion, ILogger log) =>
        excepcion.InnerException is PostgresException pg
        && ManejadorDeErrores.ClasificarPostgresException(pg, log) is { Codigo: var codigo }
            ? codigo switch
            {
                "mercadopago_ya_conectada" => ResultadoDeConexion.YaConectada,
                "mercadopago_cuenta_en_uso" => ResultadoDeConexion.CuentaEnUso,
                _ => null
            }
            : null;

    internal static string UrlDeRetorno(EstadoOAuth estado, ResultadoDeConexion resultado)
    {
        var etiqueta = resultado switch
        {
            ResultadoDeConexion.Conectado => "conectado",
            ResultadoDeConexion.Cancelado => "cancelado",
            ResultadoDeConexion.CuentaEnUso => "cuenta_en_uso",
            ResultadoDeConexion.YaConectada => "ya_conectada",
            _ => "error"
        };

        return $"{estado.Origen}{RutaDeRetorno}?mercadopago={etiqueta}&empresa={estado.IdEmpresa}";
    }

    private static bool EsOrigenValido(string origen) =>
        Uri.TryCreate(origen, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
