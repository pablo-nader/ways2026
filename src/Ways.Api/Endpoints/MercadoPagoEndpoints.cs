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

    public const string RutaDelWebhook = "/api/integraciones/mercadopago/webhook";

    private const int LimiteDelCuerpoDelWebhook = 16 * 1024;

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

        var cobros = app.MapGroup("/api/pos/mercadopago/cobros")
            .WithTags("Mercado Pago")
            .RequireAuthorization(Politicas.OperacionDePos)
            .RequireAuthorization(Politicas.RequiereDispositivo);

        cobros.MapPost("/", async (ServicioDeCobrosMercadoPago servicio, SolicitudDeCobroMercadoPago solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.CrearAsync(solicitud, ct)))
        .WithSummary(
            "Dispositivo: cobra una venta con QR dinámico o con la terminal Point de Mercado Pago según el proveedor del medio (el punto de venta es el del dispositivo). Con un cobro pendiente " +
            "del mismo número, medio e importe devuelve ese mismo cobro. 400 cobro_mercadopago_medio_invalido|cobro_mercadopago_importe_invalido|" +
            "cobro_mercadopago_numero_invalido, 403 sin dispositivo vigente, 409 cobro_mercadopago_en_curso|cobro_mercadopago_ya_aprobado|" +
            "mercadopago_no_conectada|mercadopago_requiere_reconexion|mercadopago_caja_no_registrada|mercadopago_caja_sin_terminal|mercadopago_terminal_ocupada|punto_venta_modo_incompatible, " +
            "502 mercadopago_solicitud_rechazada|mercadopago_respuesta_invalida, 503 mercadopago_no_disponible.");

        cobros.MapGet("/{idCobro:int}", async (ServicioDeCobrosMercadoPago servicio, int idCobro, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerAsync(idCobro, ct)))
        .WithSummary(
            "Dispositivo: estado de un cobro de su punto de venta. Un cobro pendiente se refresca desde Mercado Pago a lo sumo una vez " +
            "cada 2 segundos. 404 si el cobro no existe o es de otro punto de venta, 403 sin dispositivo vigente.");

        cobros.MapPost("/{idCobro:int}/cancelar", async (ServicioDeCobrosMercadoPago servicio, int idCobro, CancellationToken ct) =>
            Results.Ok(await servicio.CancelarAsync(idCobro, ct)))
        .WithSummary(
            "Dispositivo: cancela un cobro pendiente. Si Mercado Pago ya no puede cancelarlo (409) lee la orden y devuelve su estado real, " +
            "que puede ser aprobado. 404 si el cobro no existe o es de otro punto de venta, 409 cobro_mercadopago_ya_aprobado si ya estaba aprobado, " +
            "409 mercadopago_cancelar_en_la_terminal si un cobro Point ya está en la terminal (se cancela ahí).");

        cobros.MapPost("/{idCobro:int}/apartar", async (ServicioDeCobrosMercadoPago servicio, int idCobro, CancellationToken ct) =>
            Results.Ok(await servicio.ApartarAsync(idCobro, ct)))
        .WithSummary(
            "Dispositivo: aparta un cobro cuya venta no se va a cerrar. Aprobado sin venta pasa a sin_venta y libera el número; pendiente " +
            "se cancela en Mercado Pago (o termina sin_venta si ya se pagó); ya sin_venta es idempotente. 404 si el cobro no existe o es de " +
            "otro punto de venta, 409 cobro_mercadopago_ya_usado si una venta ya lo usó, 409 cobro_mercadopago_pagado_sin_datos|" +
            "cobro_mercadopago_en_curso si todavía no se puede resolver.");

        app.MapGroup("/api/mercadopago/cobros-sin-venta")
            .WithTags("Mercado Pago")
            .RequireAuthorization(Politicas.LecturaDeReportes)
            .MapGet("/", (ServicioDeCobrosMercadoPago servicio, int idEmpresa, CancellationToken ct) =>
                servicio.ListarSinVentaAsync(idEmpresa, ct))
            .WithSummary(
                "Supervisor y Admin: los cobros de Mercado Pago apartados (sin_venta) de una empresa, del más reciente al más antiguo; el " +
                "dinero entró y ninguna venta lo usó. idEmpresa es obligatorio. 404 si la empresa no existe en el tenant.");

        app.MapGroup("/api/mercadopago/pagos-sin-verificar")
            .WithTags("Mercado Pago")
            .RequireAuthorization(Politicas.LecturaDeReportes)
            .MapGet("/", (
                ServicioDeCobrosMercadoPago servicio, int idEmpresa, DateOnly desde, DateOnly hasta, int? limite, CancellationToken ct) =>
                servicio.ListarPagosSinVerificarAsync(idEmpresa, desde, hasta, limite, ct))
            .WithSummary(
                "Supervisor y Admin: los pagos de venta aprobados a mano sin red (sin_verificar) de una empresa, del más reciente al más " +
                "antiguo; el cajero los dio por pagados y la conciliación los coteja. idEmpresa, desde y hasta son obligatorios; desde y " +
                "hasta son días (yyyy-MM-dd) inclusivos en la zona horaria de la empresa, de hasta 366 días (400 si hasta < desde o el " +
                "rango es mayor). limite (por defecto 200, máximo 500) acota las filas; truncado indica que había más. Cada fila trae " +
                "idCobroEnConflicto cuando la venta chocó con otro cobro de Mercado Pago del mismo número. 404 si la empresa no existe en el tenant.");

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

        app.MapPost(RutaDelWebhook, async (
            HttpContext contexto,
            ServicioDeAvisosDeMercadoPago servicio,
            CancellationToken ct) =>
        {
            var consulta = contexto.Request.Query;
            var tipo = consulta["type"].ToString();
            var aviso = new AvisoDeMercadoPago(
                contexto.Request.Headers["x-signature"].ToString(),
                contexto.Request.Headers["x-request-id"].ToString(),
                consulta["data.id"].ToString(),
                tipo.Length > 0 ? tipo : null,
                tokenCuerpo => LeerTipoDelCuerpoAsync(contexto.Request, tokenCuerpo));

            return await servicio.ProcesarAsync(aviso, ct) switch
            {
                ResultadoDelAviso.Atendido => Results.Ok(),
                ResultadoDelAviso.NoConfigurado => throw new ErrorDominio(
                    "mercadopago_no_configurado", "Los avisos de Mercado Pago no están configurados en este entorno.",
                    StatusCodes.Status503ServiceUnavailable),
                _ => throw new ErrorDominio(
                    "mercadopago_firma_invalida", "La firma del aviso de Mercado Pago no es válida.", StatusCodes.Status401Unauthorized)
            };
        })
        .AllowAnonymous()
        .WithSummary(
            "Aviso (webhook) de Mercado Pago, anónimo: la firma x-signature (HMAC-SHA256 de id, x-request-id y ts con el secreto de la " +
            "aplicación, ts dentro de 10 minutos atrás y 2 adelante) es la única credencial. Con firma válida refresca el cobro de la orden " +
            "avisada (solo el tópico order; una orden desconocida o de otro tópico se ignora) y contesta 200 aunque el refresco falle. " +
            "401 mercadopago_firma_invalida, 503 mercadopago_no_configurado.");

        return app;
    }

    /// <summary>El <c>type</c> del cuerpo JSON, solo cuando la URL no lo trae. Se invoca únicamente después de una firma válida, y el
    /// cuerpo se lee acotado: lo que no es un objeto JSON chico con un <c>type</c> de texto no tiene tipo.</summary>
    private static async Task<string?> LeerTipoDelCuerpoAsync(HttpRequest solicitud, CancellationToken ct)
    {
        var buffer = new byte[LimiteDelCuerpoDelWebhook + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var leidos = await solicitud.Body.ReadAsync(buffer.AsMemory(total), ct);
            if (leidos == 0)
            {
                break;
            }

            total += leidos;
        }

        if (total is 0 or > LimiteDelCuerpoDelWebhook)
        {
            return null;
        }

        try
        {
            using var documento = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, total));
            return documento.RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } raiz
                && raiz.TryGetProperty("type", out var tipo)
                && tipo.ValueKind == System.Text.Json.JsonValueKind.String
                    ? tipo.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
