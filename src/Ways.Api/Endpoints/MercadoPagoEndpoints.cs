using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.MercadoPago;
using Ways.Domain.Common;

namespace Ways.Api.Endpoints;

/// <summary>
/// Conexión de una empresa con Mercado Pago por OAuth y registro de sus sucursales como locales. Inicio, completar, lectura, desconexión y el local de cada sucursal son de Admin
/// (<see cref="Politicas.GestionDeCatalogo"/>); el callback es anónimo porque la URI de retorno registrada en Mercado Pago es
/// una sola y la sesión del comercio puede no existir en ese host, por eso no canjea nada: solo reenvía el code y el state a la
/// web del comercio, donde la sesión que inició el flujo completa el canje.
/// </summary>
public static class MercadoPagoEndpoints
{
    public const string RutaDelCallback = "/api/integraciones/mercadopago/callback";

    /// <summary>Pantalla del back office donde el Admin ve y administra las empresas; vuelve acá con
    /// <c>#mercadopago=&lt;completar|cancelado&gt;&amp;empresa=&lt;id&gt;</c>.</summary>
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
        .WithSummary("Desconecta la empresa de Mercado Pago (conexión activa o pendiente de reconexión), borra sus tokens cifrados y registra quién lo hizo. 404 si no tiene ninguna.");

        admin.MapPost("/completar", async (
            ServicioDeConexionMercadoPago servicio,
            ILogger<ServicioDeConexionMercadoPago> log,
            int idEmpresa,
            SolicitudDeCompletarConexion solicitud,
            CancellationToken ct) =>
        {
            ResultadoDeConexion resultado;
            try
            {
                resultado = await servicio.CompletarEnSesionAsync(idEmpresa, solicitud, ct);
            }
            catch (DbUpdateException perdedora) when (ResultadoDeCarrera(perdedora, log) is { } carrera)
            {
                resultado = carrera;
            }
            catch (Exception e) when (e is not (OperationCanceledException or ErrorDominio))
            {
                log.LogError(e, "Falló el cierre de la conexión con Mercado Pago de la empresa {IdEmpresa}.", idEmpresa);
                resultado = ResultadoDeConexion.Error;
            }

            return resultado switch
            {
                ResultadoDeConexion.Conectado => Results.Ok(await servicio.ObtenerAsync(idEmpresa, ct)),
                ResultadoDeConexion.YaConectada => throw ErrorDominio.Conflicto(
                    "mercadopago_ya_conectada", "La empresa ya tiene Mercado Pago conectado."),
                ResultadoDeConexion.CuentaEnUso => throw ErrorDominio.Conflicto(
                    "mercadopago_cuenta_en_uso", "Esa cuenta de Mercado Pago ya está conectada a otra empresa."),
                _ => throw new ErrorDominio(
                    "mercadopago_intercambio_fallido",
                    "No se pudo completar la conexión con Mercado Pago. Intentá de nuevo.",
                    StatusCodes.Status502BadGateway)
            };
        })
        .WithSummary(
            "Completa la conexión dentro de la sesión que la inició: canjea el code y guarda la conexión cifrada. 400 " +
            "mercadopago_estado_invalido, 403 mercadopago_estado_ajeno, 409 mercadopago_ya_conectada|mercadopago_cuenta_en_uso, " +
            "502 mercadopago_intercambio_fallido.");

        var locales = app.MapGroup("/api/sucursales/{idSucursal:int}/mercadopago")
            .WithTags("Mercado Pago")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        locales.MapGet("/", (ServicioDeLocalesMercadoPago servicio, int idSucursal, CancellationToken ct) =>
            servicio.ObtenerAsync(idSucursal, ct))
        .WithSummary(
            "Estado de la sucursal como local de Mercado Pago en la cuenta conectada a su empresa: sin_conexion, no_registrada, " +
            "registrada o desactualizada, con los datos de dirección que le faltan. 404 si la sucursal no existe.");

        locales.MapPost("/", (ServicioDeLocalesMercadoPago servicio, int idSucursal, CancellationToken ct) =>
            servicio.RegistrarAsync(idSucursal, ct))
        .WithSummary(
            "Registra la sucursal como local de Mercado Pago, adopta el que ya exista con su external_id o actualiza el existente " +
            "con los datos actuales. 404 sucursal inexistente, 400 direccion_incompleta_para_mercadopago, 409 " +
            "mercadopago_no_conectada|mercadopago_requiere_reconexion, 502 mercadopago_solicitud_rechazada, 503 mercadopago_no_disponible.");

        var cajas = app.MapGroup("/api/puntos-venta/{idPuntoVenta:int}/mercadopago")
            .WithTags("Mercado Pago")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        cajas.MapGet("/", (ServicioDeCajasMercadoPago servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.ObtenerAsync(idPuntoVenta, ct))
        .WithSummary(
            "Estado del punto de venta como caja de Mercado Pago en la cuenta conectada a su empresa: sin_conexion, " +
            "local_no_registrado, no_registrada, registrada o desactualizada. 404 si el punto de venta no existe.");

        cajas.MapPost("/", (ServicioDeCajasMercadoPago servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.RegistrarAsync(idPuntoVenta, ct))
        .WithSummary(
            "Registra el punto de venta como caja de Mercado Pago bajo el local de su sucursal, adopta la que ya exista con su " +
            "external_id o actualiza la existente (también la reapunta al local de la sucursal actual si el punto de venta se movió). " +
            "404 punto de venta inexistente, 400 nombre_invalido_para_mercadopago, 409 mercadopago_no_conectada|" +
            "mercadopago_requiere_reconexion|mercadopago_local_no_registrado, 502 mercadopago_solicitud_rechazada, " +
            "503 mercadopago_no_disponible.");

        cajas.MapGet("/terminales", (ServicioDeTerminalesMercadoPago servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, ct))
        .WithSummary(
            "Terminales Point que Mercado Pago tiene vinculadas a la caja del punto de venta (id, modo, si es la asignada); con la lista " +
            "vacía trae las instrucciones para vincularla en la app de Mercado Pago. 404 punto de venta inexistente, 409 " +
            "mercadopago_no_conectada|mercadopago_requiere_reconexion|mercadopago_caja_no_registrada, 502 mercadopago_solicitud_rechazada, " +
            "503 mercadopago_no_disponible.");

        cajas.MapPost("/terminal", (ServicioDeTerminalesMercadoPago servicio, int idPuntoVenta, SolicitudDeTerminalMercadoPago? solicitud, CancellationToken ct) =>
            servicio.AsignarAsync(idPuntoVenta, solicitud, ct))
        .WithSummary(
            "Asigna la terminal Point (solo una vinculada a la caja) al punto de venta y la pasa a modo PDV si no lo estaba. 404 punto de " +
            "venta inexistente, 400 terminal_point_invalida, 409 mercadopago_no_conectada|mercadopago_requiere_reconexion|" +
            "mercadopago_caja_no_registrada|mercadopago_terminal_no_vinculada|mercadopago_terminal_modelo_no_admitido|" +
            "mercadopago_caja_con_terminal_en_pdv, 502 mercadopago_solicitud_rechazada, 503 mercadopago_no_disponible.");

        cajas.MapDelete("/terminal", (ServicioDeTerminalesMercadoPago servicio, int idPuntoVenta, CancellationToken ct) =>
            servicio.QuitarAsync(idPuntoVenta, ct))
        .WithSummary(
            "Quita la terminal asignada al punto de venta en Ways; no toca Mercado Pago. 404 punto de venta inexistente, 409 " +
            "mercadopago_no_conectada|mercadopago_requiere_reconexion|mercadopago_caja_no_registrada.");

        app.MapGet(RutaDelCallback, (
            ServicioDeConexionMercadoPago servicio,
            string? code,
            string? state,
            string? error) =>
        {
            var estado = servicio.LeerEstado(state);
            var hayCodigo = !string.IsNullOrWhiteSpace(code);
            if (estado is null || !EsOrigenValido(estado.Origen) || (!hayCodigo && string.IsNullOrWhiteSpace(error)))
            {
                return Results.Text(
                    "El enlace de conexión con Mercado Pago no es válido o venció. Volvé a Ways e iniciá la conexión de nuevo.",
                    "text/plain",
                    Encoding.UTF8,
                    StatusCodes.Status400BadRequest);
            }

            return Results.Redirect(UrlDeRetorno(estado, string.IsNullOrWhiteSpace(error) ? code : null, state));
        })
        .AllowAnonymous()
        .WithSummary(
            "Retorno de OAuth de Mercado Pago: no canjea nada. Redirige a la pantalla de la empresa con el code y el state en el " +
            "fragmento de la URL (#mercadopago=completar|cancelado), para que la sesión de Ways complete el canje.");

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

    /// <summary>La URL de destino lleva el código y el state en el fragmento, no en la consulta: el navegador no manda el fragmento
    /// al servidor ni en el Referer, así que no quedan en los logs del destino. No es una garantía sobre todo el recorrido: la URL
    /// del propio callback (<see cref="RutaDelCallback"/>) sí los trae en la consulta y puede quedar en los logs de Ways y de sus
    /// proxies, y el encabezado <c>Location</c> de esta redirección los lleva en el fragmento. Con <paramref name="codigo"/> nulo es
    /// la vuelta de un usuario que denegó el acceso.</summary>
    internal static string UrlDeRetorno(EstadoOAuth estado, string? codigo, string? state) =>
        codigo is null
            ? $"{estado.Origen}{RutaDeRetorno}#mercadopago=cancelado&empresa={estado.IdEmpresa}"
            : $"{estado.Origen}{RutaDeRetorno}#mercadopago=completar&empresa={estado.IdEmpresa}" +
              $"&code={Uri.EscapeDataString(codigo)}&state={Uri.EscapeDataString(state!)}";

    private static bool EsOrigenValido(string origen) =>
        Uri.TryCreate(origen, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
