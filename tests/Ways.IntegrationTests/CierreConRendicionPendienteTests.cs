using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Pos;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// La guarda de rendición de cola en los DOS modos de cierre (<c>POST …/cierre</c> y
/// <c>POST …/cierre-por-retiro</c>): un turno no se cierra mientras un dispositivo de escritorio
/// del punto de venta tenga ventas sin drenar, o no pueda probar que no las tiene.
///
/// El defecto que cierra: la guarda anterior era SOLO del cliente (<c>Pos.tsx</c> /
/// <c>CierreDeCaja.tsx</c>) y leía el IndexedDB LOCAL, así que cerrar desde OTRA máquina o desde la
/// web leía un almacén vacío y pasaba. Todos los tests de acá cierran con un actor WEB
/// (<c>admin</c>/<c>supervisor</c>/<c>vendedor</c> logueados por mail), nunca con el dispositivo:
/// es exactamente el camino que antes no tenía guarda ninguna.
///
/// Los bloques de <c>reservas_numeracion</c> se siembran por SQL crudo para poder expresar estados
/// que el endpoint de rendición no produce (un reporte vencido, un bloque abandonado) — mismo
/// criterio y mismo motivo que <c>ReservaDeNumeracionEndpointsTests.SembrarReservaDirectaAsync</c>.
/// El camino real (dispositivo → <c>POST /api/pos/rendicion-de-cola</c> → cierre) tiene su propio
/// test al final.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CierreConRendicionPendienteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordDelRol = "una-contraseña-larga";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdCliente, int IdTipoComprobanteTx,
        int IdDispositivo, string NombreDispositivo, string CookieDelDispositivo, HttpClient Admin);

    // ---- siembra -------------------------------------------------------------------------------

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var nombreDispositivo = "Caja de escritorio";
        var altaDispositivo = await admin.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, nombreDispositivo));
        Assert.Equal(HttpStatusCode.Created, altaDispositivo.StatusCode);
        var vinculado = (await altaDispositivo.Content.ReadFromJsonAsync<DispositivoVinculado>())!;
        var cookieDelDispositivo = ExtraerCookieDeDispositivo(altaDispositivo);

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipoComprobanteTx,
            vinculado.Datos.Id, nombreDispositivo, cookieDelDispositivo, admin);
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            respuesta.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Inserta el bloque vivo con el estado EXACTO que cada test necesita — incluido el
    /// que el endpoint de rendición nunca escribiría (un <c>reportado_at</c> viejo).</summary>
    private Task SembrarBloqueAsync(
        Contexto ctx, long desde, long hasta, long? entregadoHasta, int? pendientes, DateTimeOffset? reportadoAt,
        DateTimeOffset? abandonadaAt = null) =>
        SembrarBloqueEnPuntoVentaAsync(
            ctx, ctx.IdPuntoVenta, ctx.IdDispositivo, desde, hasta, entregadoHasta, pendientes, reportadoAt,
            abandonadaAt);

    private async Task SembrarBloqueEnPuntoVentaAsync(
        Contexto ctx, int idPuntoVenta, int idDispositivo, long desde, long hasta, long? entregadoHasta,
        int? pendientes, DateTimeOffset? reportadoAt, DateTimeOffset? abandonadaAt = null)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO reservas_numeracion " +
            "(id_tenant, id_punto_venta, tipo_comprobante, id_dispositivo, desde, hasta, abandonada_at, " +
            " entregado_hasta, pendientes, reportado_at, created_at, updated_at) " +
            "VALUES ($1, $2, 'TX', $3, $4, $5, $6, $7, $8, $9, now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idDispositivo });
        comando.Parameters.Add(new NpgsqlParameter { Value = desde });
        comando.Parameters.Add(new NpgsqlParameter { Value = hasta });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)abandonadaAt ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)entregadoHasta ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)pendientes ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)reportadoAt ?? DBNull.Value });
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Un comprobante SIN pagos: la guarda cuenta filas de <c>comprobantes_venta</c> en el
    /// rango, no importes, y sin pagos el turno no tiene ningún medio arqueable — así el cierre
    /// clásico acepta <c>Conteos: []</c> y el test mide la guarda y nada más.
    ///
    /// <paramref name="codigoTipo"/> y <paramref name="idPuntoVenta"/> existen para los tests que
    /// aíslan los conjuntos de la subconsulta de conteo: un comprobante de OTRA serie o de OTRO
    /// punto de venta no tiene que tapar un hueco.</summary>
    private async Task SembrarComprobanteAsync(
        Contexto ctx, int? idTurno, long numero, string codigoTipo = "TX", int? idPuntoVenta = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idTipoComprobante = codigoTipo == "TX"
            ? ctx.IdTipoComprobanteTx
            : await db.TiposComprobante.Where(t => t.Codigo == codigoTipo).Select(t => t.Id).FirstAsync();

        db.ComprobantesVenta.Add(new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = idTipoComprobante,
            Numero = numero,
            Fecha = ahora,
            IdPuntoVenta = idPuntoVenta ?? ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> CerrarAsync(
        HttpClient cliente, bool porRetiro, int idTurno, bool forzar = false, string? motivo = null) =>
        porRetiro
            ? cliente.PostAsJsonAsync(
                $"/api/caja/turnos/{idTurno}/cierre-por-retiro",
                new SolicitudDeCierrePorRetiro(0m, null, forzar, motivo))
            : cliente.PostAsJsonAsync(
                $"/api/caja/turnos/{idTurno}/cierre",
                new SolicitudDeCierre([], null, forzar, motivo));

    private async Task<HttpClient> CrearActorWebAsync(Contexto ctx, string nombre, RolConocido rol)
    {
        var mail = $"{nombre.ToLowerInvariant()}-{rol}@ways.test".ToLowerInvariant();
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{nombre}-{rol}".ToLowerInvariant(), mail, (int)rol, PasswordDelRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordDelRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Un SEGUNDO punto de venta Escritorio del mismo tenant, con su propio dispositivo
    /// (<c>ux_dispositivos_punto_venta</c> admite uno por punto de venta) — el hermano sin el cual
    /// los conjuntos de punto de venta de la guarda no se pueden aislar.</summary>
    private async Task<(int IdPuntoVenta, int IdDispositivo)> AgregarPuntoVentaConDispositivoAsync(
        Contexto ctx, string nombre)
    {
        int idPuntoVenta;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var idEmpresa = await db.Empresas.Select(e => e.Id).FirstAsync();
            var ahora = DateTimeOffset.UtcNow;
            var puntoVenta = new PuntoVenta
            {
                IdEmpresa = idEmpresa, Nombre = nombre, Modo = ModoPuntoVenta.Escritorio,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            db.PuntosVenta.Add(puntoVenta);
            await db.SaveChangesAsync();
            idPuntoVenta = puntoVenta.Id;
        }

        var alta = await ctx.Admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, nombre));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vinculado = (await alta.Content.ReadFromJsonAsync<DispositivoVinculado>())!;

        return (idPuntoVenta, vinculado.Datos.Id);
    }

    private async Task<EstadoTurno> LeerEstadoAsync(Contexto ctx, int idTurno)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.Estado).FirstAsync();
    }

    /// <summary>El cuerpo del ProblemDetails leído UNA vez: <c>codigo</c> es la extensión que
    /// <c>ManejadorDeErrores</c> agrega y <c>title</c> es el mensaje del <c>ErrorDominio</c>.</summary>
    private static async Task<(string Codigo, string Mensaje)> ProblemaAsync(HttpResponseMessage respuesta)
    {
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        return (
            documento.RootElement.GetProperty("codigo").GetString()!,
            documento.RootElement.GetProperty("title").GetString()!);
    }

    // ---- los cuatro motivos de bloqueo, en los DOS modos de cierre -----------------------------

    /// <summary>Disyunto (a) de <see cref="ReglaDeRendicionDeCola"/>: fail-closed. El bloque está
    /// vivo y nunca rindió. Se afirma además que el turno sigue ABIERTO: la guarda corre DENTRO de
    /// la transacción del cierre, después del UPDATE guardado, así que el rechazo tiene que
    /// deshacer ese UPDATE — un turno que quedara cerrado igual sería el peor resultado
    /// posible.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreSeBloqueaCuandoElDispositivoNuncaRindio(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(ElCierreSeBloqueaCuandoElDispositivoNuncaRindio)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(ctx, desde: 1, hasta: 10, entregadoHasta: null, pendientes: null, reportadoAt: null);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("rendicion_de_dispositivo_pendiente", codigo);
        Assert.Contains(ctx.NombreDispositivo, mensaje, StringComparison.Ordinal);
        Assert.Contains("no reportó", mensaje, StringComparison.Ordinal);

        Assert.Equal(EstadoTurno.Abierto, await LeerEstadoAsync(ctx, turno.Id));
    }

    /// <summary>Disyunto (b): el reporte declara cero pendientes y sin hueco, pero es más viejo que
    /// <see cref="ReglaDeRendicionDeCola.VentanaDeFrescura"/> — no prueba nada sobre lo que el
    /// dispositivo vendió después de mandarlo.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreSeBloqueaCuandoElReporteEstaVencido(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(ElCierreSeBloqueaCuandoElReporteEstaVencido)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 0, pendientes: 0,
            reportadoAt: DateTimeOffset.UtcNow - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromMinutes(1));

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("rendicion_de_dispositivo_pendiente", codigo);
        Assert.Contains("vencido", mensaje, StringComparison.Ordinal);
    }

    /// <summary>Disyunto (c): el dispositivo declara 3 ventas sin llegar. El mensaje tiene que decir
    /// CUÁNTAS — para eso existe la columna <c>pendientes</c>, y ese 3 es el valor discriminante que
    /// un mutante que mande una constante o el motivo equivocado no puede producir.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreSeBloqueaCuandoHayVentasSinSincronizar(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(ElCierreSeBloqueaCuandoHayVentasSinSincronizar)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 0, pendientes: 3, reportadoAt: DateTimeOffset.UtcNow);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("rendicion_de_dispositivo_pendiente", codigo);
        Assert.Contains("3 venta(s) sin sincronizar", mensaje, StringComparison.Ordinal);
    }

    /// <summary>Disyunto (d), el que hace el reporte VERIFICABLE: el dispositivo declara cero
    /// pendientes y haber entregado 1..3, pero solo dos de esos tres comprobantes llegaron. El
    /// reporte se contradice con los hechos y la guarda le cree a los hechos.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreSeBloqueaCuandoFaltanComprobantesDelRangoEntregado(bool porRetiro)
    {
        var ctx = await PrepararAsync(
            $"{nameof(ElCierreSeBloqueaCuandoFaltanComprobantesDelRangoEntregado)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 3, pendientes: 0, reportadoAt: DateTimeOffset.UtcNow);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 1);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 2);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("rendicion_de_dispositivo_pendiente", codigo);
        Assert.Contains("faltan comprobantes", mensaje, StringComparison.Ordinal);
        Assert.Contains("1-3", mensaje, StringComparison.Ordinal);
    }

    /// <summary>El otro lado del MISMO borde que el test de arriba: con los TRES comprobantes del
    /// rango, no hay hueco y el cierre avanza. Los dos juntos son el kill de la comparación del
    /// hueco a nivel integración (la aritmética ya la fija
    /// <c>ReglaDeRendicionDeColaTests</c>).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreAvanzaCuandoLlegaronTodosLosComprobantesDelRango(bool porRetiro)
    {
        var ctx = await PrepararAsync(
            $"{nameof(ElCierreAvanzaCuandoLlegaronTodosLosComprobantesDelRango)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 3, pendientes: 0, reportadoAt: DateTimeOffset.UtcNow);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 1);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 2);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 3);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(EstadoTurno.Cerrado, await LeerEstadoAsync(ctx, turno.Id));
    }

    // ---- las dos válvulas de escape ------------------------------------------------------------

    /// <summary>LA VÁLVULA DE ESCAPE de la guarda, y el kill del conjunto <c>d.deleted_at IS NULL</c>
    /// del join a <c>dispositivos</c>: el bloque sigue vivo y declara 5 ventas pendientes, pero el
    /// dispositivo fue REVOCADO — un dispositivo revocado ya no puede sincronizar nunca, así que
    /// dejarlo bloqueando sería un turno inmortal. Borrar ese conjunto deja este test en 409.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreAvanzaCuandoElDispositivoFueRevocado(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(ElCierreAvanzaCuandoElDispositivoFueRevocado)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 0, pendientes: 5, reportadoAt: DateTimeOffset.UtcNow);

        // Antes de revocar, el mismo cierre está bloqueado — sin esta mitad, el test pasaría igual
        // con la guarda entera borrada.
        var bloqueado = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);
        Assert.Equal(HttpStatusCode.Conflict, bloqueado.StatusCode);

        var revocacion = await ctx.Admin.DeleteAsync($"/api/dispositivos/{ctx.IdDispositivo}");
        Assert.Equal(HttpStatusCode.NoContent, revocacion.StatusCode);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(EstadoTurno.Cerrado, await LeerEstadoAsync(ctx, turno.Id));
    }

    /// <summary>Kill del conjunto <c>r.abandonada_at IS NULL</c> de la consulta de la guarda: un
    /// bloque ABANDONADO (el dispositivo pidió uno nuevo) declara 5 pendientes y NO bloquea — ese
    /// bloque ya no reparte números, y el vivo que lo reemplazó es el único que importa. Borrar ese
    /// conjunto deja este test en 409.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElCierreAvanzaCuandoElUnicoBloqueConPendientesFueAbandonado(bool porRetiro)
    {
        var ctx = await PrepararAsync(
            $"{nameof(ElCierreAvanzaCuandoElUnicoBloqueConPendientesFueAbandonado)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 0, pendientes: 5, reportadoAt: DateTimeOffset.UtcNow,
            abandonadaAt: DateTimeOffset.UtcNow);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(EstadoTurno.Cerrado, await LeerEstadoAsync(ctx, turno.Id));
    }

    // ---- el override supervisado ---------------------------------------------------------------

    /// <summary>El override, por un SUPERVISOR: el cierre avanza y queda el rastro auditable
    /// <c>caja.forzado</c> con el motivo declarado, el dispositivo y el rango sin rendir — el
    /// round-trip de <c>MotivoSinRendicion</c> que <c>dto-contract-honesty</c> exige.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnSupervisorFuerzaElCierreYQuedaAuditado(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(UnSupervisorFuerzaElCierreYQuedaAuditado)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 2, pendientes: 4, reportadoAt: DateTimeOffset.UtcNow);

        using var supervisor = await CrearActorWebAsync(ctx, $"sup{porRetiro}", RolConocido.Supervisor);

        var respuesta = await CerrarAsync(
            supervisor, porRetiro, turno.Id, forzar: true, motivo: "La tablet se rompió y no vuelve.");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(EstadoTurno.Cerrado, await LeerEstadoAsync(ctx, turno.Id));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "caja.forzado");

        Assert.Equal("turno_caja", fila.Entidad);
        Assert.Equal(turno.Id, fila.IdEntidad);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);
        Assert.Null(fila.ValorAnterior);

        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal("La tablet se rompió y no vuelve.", nuevo.GetProperty("motivo").GetString());

        var bloqueo = Assert.Single(nuevo.GetProperty("bloqueos").EnumerateArray().ToList());
        Assert.Equal(ctx.IdDispositivo, bloqueo.GetProperty("id_dispositivo").GetInt32());
        Assert.Equal(ctx.NombreDispositivo, bloqueo.GetProperty("dispositivo").GetString());
        Assert.Equal("TX", bloqueo.GetProperty("tipo_comprobante").GetString());
        Assert.Equal(nameof(MotivoDeRendicionPendiente.VentasSinLlegar), bloqueo.GetProperty("bloqueo").GetString());
        Assert.Equal(4, bloqueo.GetProperty("pendientes").GetInt32());
        Assert.Equal(1, bloqueo.GetProperty("desde").GetInt64());
        Assert.Equal(2, bloqueo.GetProperty("entregado_hasta").GetInt64());
    }

    /// <summary>El forzado SIN nada pendiente también deja rastro: es lo que garantiza que el motivo
    /// declarado nunca quede aceptado-y-descartado (<c>dto-contract-honesty</c>), y "un supervisor
    /// apretó el override sin necesidad" es en sí mismo un dato. <c>bloqueos</c> vacío es el valor
    /// discriminante: un mutante que solo audite cuando encontró algo deja este test sin
    /// fila.</summary>
    [Fact]
    public async Task ElForzadoSinNadaPendienteTambienDejaAuditoria()
    {
        var ctx = await PrepararAsync(nameof(ElForzadoSinNadaPendienteTambienDejaAuditoria));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await CerrarAsync(
            ctx.Admin, porRetiro: false, turno.Id, forzar: true, motivo: "Por si acaso.");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "caja.forzado");
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;

        Assert.Equal("Por si acaso.", nuevo.GetProperty("motivo").GetString());
        Assert.Empty(nuevo.GetProperty("bloqueos").EnumerateArray().ToList());
    }

    /// <summary>El gate de rol del override: un VENDEDOR no puede forzar, aunque sí pueda cerrar
    /// (las dos rutas están bajo <c>OperacionDePos</c> justamente para eso). Sin el chequeo del
    /// servicio, cualquier cajero se saltaría la guarda solo.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnVendedorNoPuedeForzarElCierre(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(UnVendedorNoPuedeForzarElCierre)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 0, pendientes: 2, reportadoAt: DateTimeOffset.UtcNow);

        using var vendedor = await CrearActorWebAsync(ctx, $"ven{porRetiro}", RolConocido.Vendedor);

        var respuesta = await CerrarAsync(
            vendedor, porRetiro, turno.Id, forzar: true, motivo: "Quiero irme a casa.");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        Assert.Equal("prohibido", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(EstadoTurno.Abierto, await LeerEstadoAsync(ctx, turno.Id));
    }

    /// <summary>dto-contract-honesty, primer destino de <c>MotivoSinRendicion</c>: forzar sin motivo
    /// se rechaza, nunca se fuerza en silencio.</summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "   ")]
    [InlineData(true, null)]
    [InlineData(true, "   ")]
    public async Task ForzarSinMotivoEs400(bool porRetiro, string? motivo)
    {
        var ctx = await PrepararAsync($"{nameof(ForzarSinMotivoEs400)}{porRetiro}{motivo?.Length ?? 0}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro, turno.Id, forzar: true, motivo: motivo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("motivo_requerido", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(EstadoTurno.Abierto, await LeerEstadoAsync(ctx, turno.Id));
    }

    /// <summary>dto-contract-honesty, segundo destino del MISMO campo: un motivo sin forzado se
    /// rechaza en vez de aceptarse y descartarse — el defecto exacto que ese skill existe para
    /// frenar (y que ya pasó dos veces en este repo con <c>Observaciones</c>).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnMotivoSinForzadoEs400(bool porRetiro)
    {
        var ctx = await PrepararAsync($"{nameof(UnMotivoSinForzadoEs400)}{porRetiro}");
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await CerrarAsync(
            ctx.Admin, porRetiro, turno.Id, forzar: false, motivo: "Un motivo que nadie pidió.");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("motivo_sin_forzado", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(EstadoTurno.Abierto, await LeerEstadoAsync(ctx, turno.Id));
    }

    // ---- los conjuntos restantes de la consulta de la guarda -----------------------------------

    /// <summary>Kill del conjunto <c>r.id_punto_venta = $2</c> de la consulta de la guarda: el
    /// dispositivo de OTRO punto de venta tiene 5 ventas pendientes y eso no puede bloquear el
    /// cierre de este turno. Borrar ese conjunto convierte cada cierre del tenant en hostage de
    /// cualquier dispositivo de cualquier sucursal.</summary>
    [Fact]
    public async Task ElCierreNoSeBloqueaPorElDispositivoDeOtroPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElCierreNoSeBloqueaPorElDispositivoDeOtroPuntoDeVenta));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);

        var (idPuntoVentaAjeno, idDispositivoAjeno) = await AgregarPuntoVentaConDispositivoAsync(ctx, "Local 2");
        await SembrarBloqueEnPuntoVentaAsync(
            ctx, idPuntoVentaAjeno, idDispositivoAjeno, desde: 1, hasta: 10, entregadoHasta: 0, pendientes: 5,
            reportadoAt: DateTimeOffset.UtcNow);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(EstadoTurno.Cerrado, await LeerEstadoAsync(ctx, turno.Id));
    }

    /// <summary>Kill del conjunto <c>tc.codigo = r.tipo_comprobante</c> de la subconsulta de conteo:
    /// cada serie numera INDEPENDIENTE, así que tres NCX numeradas 1..3 no prueban nada sobre las
    /// TX 1..3 que el dispositivo dice haber entregado. Sin ese conjunto el hueco queda tapado y el
    /// cierre pasa sobre tres ventas que nunca llegaron.</summary>
    [Fact]
    public async Task ElHuecoNoSeTapaConComprobantesDeOtraSerie()
    {
        var ctx = await PrepararAsync(nameof(ElHuecoNoSeTapaConComprobantesDeOtraSerie));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 3, pendientes: 0, reportadoAt: DateTimeOffset.UtcNow);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 1, codigoTipo: "NCX");
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 2, codigoTipo: "NCX");
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 3, codigoTipo: "NCX");

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("rendicion_de_dispositivo_pendiente", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>Kill del conjunto <c>cv.id_punto_venta = r.id_punto_venta</c> de la subconsulta:
    /// cada punto de venta numera su propia serie, así que las TX 1..3 de OTRA sucursal no tapan el
    /// hueco de este bloque.</summary>
    [Fact]
    public async Task ElHuecoNoSeTapaConComprobantesDeOtroPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElHuecoNoSeTapaConComprobantesDeOtroPuntoDeVenta));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 3, pendientes: 0, reportadoAt: DateTimeOffset.UtcNow);

        var (idPuntoVentaAjeno, _) = await AgregarPuntoVentaConDispositivoAsync(ctx, "Local 3");
        await SembrarComprobanteAsync(ctx, idTurno: null, numero: 1, idPuntoVenta: idPuntoVentaAjeno);
        await SembrarComprobanteAsync(ctx, idTurno: null, numero: 2, idPuntoVenta: idPuntoVentaAjeno);
        await SembrarComprobanteAsync(ctx, idTurno: null, numero: 3, idPuntoVenta: idPuntoVentaAjeno);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("rendicion_de_dispositivo_pendiente", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>Kill del límite SUPERIOR de la subconsulta
    /// (<c>cv.numero &lt;= COALESCE(r.entregado_hasta, r.desde - 1)</c>): el dispositivo dice haber
    /// entregado 1..3 y llegaron la 1, la 2 y la 9. La 9 está dentro del BLOQUE pero fuera de lo
    /// entregado, así que no cuenta — sin el límite el conteo daría 3 y taparía el hueco de la
    /// 3.</summary>
    [Fact]
    public async Task ElHuecoNoSeTapaConUnComprobantePorArribaDeLoEntregado()
    {
        var ctx = await PrepararAsync(nameof(ElHuecoNoSeTapaConUnComprobantePorArribaDeLoEntregado));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 1, hasta: 10, entregadoHasta: 3, pendientes: 0, reportadoAt: DateTimeOffset.UtcNow);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 1);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 2);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 9);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("rendicion_de_dispositivo_pendiente", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>Kill del límite INFERIOR de la subconsulta (<c>cv.numero &gt;= r.desde</c>): el
    /// bloque arranca en 5 y entregó 5..7; llegaron la 5, la 6 y una 1 de un bloque anterior. Esa 1
    /// no cuenta — sin el límite el conteo daría 3 y taparía el hueco de la 7.</summary>
    [Fact]
    public async Task ElHuecoNoSeTapaConUnComprobantePorDebajoDelBloque()
    {
        var ctx = await PrepararAsync(nameof(ElHuecoNoSeTapaConUnComprobantePorDebajoDelBloque));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarBloqueAsync(
            ctx, desde: 5, hasta: 10, entregadoHasta: 7, pendientes: 0, reportadoAt: DateTimeOffset.UtcNow);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 1);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 5);
        await SembrarComprobanteAsync(ctx, turno.Id, numero: 6);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("rendicion_de_dispositivo_pendiente", (await ProblemaAsync(respuesta)).Codigo);
    }

    // ---- el camino real, punta a punta ---------------------------------------------------------

    /// <summary>El circuito completo sin ninguna siembra cruda: el dispositivo reserva un bloque (y
    /// con eso el cierre queda bloqueado por fail-closed), rinde su cola limpia por
    /// <c>POST /api/pos/rendicion-de-cola</c>, y el cierre WEB pasa. Es la prueba de que las dos
    /// piezas están cableadas entre sí y no solo funcionan por separado.</summary>
    [Fact]
    public async Task DespuesDeQueElDispositivoRindeLimpioElCierreWebPasa()
    {
        var ctx = await PrepararAsync(nameof(DespuesDeQueElDispositivoRindeLimpioElCierreWebPasa));
        using var _admin = ctx.Admin;
        var turno = await AbrirTurnoAsync(ctx);

        using var cajero = await LoguearComoCajeroDeDispositivoAsync(ctx);

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion",
            new Ways.Application.Ventas.SolicitudDeReservaDeNumeracion(ctx.IdPuntoVenta, "TX", 10));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);
        var bloque = (await reserva.Content.ReadFromJsonAsync<Ways.Application.Ventas.BloqueDeNumeracionReservado>())!;

        var bloqueado = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);
        Assert.Equal(HttpStatusCode.Conflict, bloqueado.StatusCode);
        Assert.Equal("rendicion_de_dispositivo_pendiente", (await ProblemaAsync(bloqueado)).Codigo);

        var rendicion = await cajero.PostAsJsonAsync(
            "/api/pos/rendicion-de-cola", new SolicitudDeRendicionDeCola("TX", bloque.Desde - 1, 0));
        Assert.Equal(HttpStatusCode.NoContent, rendicion.StatusCode);

        var respuesta = await CerrarAsync(ctx.Admin, porRetiro: false, turno.Id);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(EstadoTurno.Cerrado, await LeerEstadoAsync(ctx, turno.Id));
    }

    /// <summary>Loguea un cajero Vendedor contra el dispositivo que <see cref="PrepararAsync"/> ya
    /// vinculó — <c>ux_dispositivos_punto_venta</c> admite un solo dispositivo vigente por punto de
    /// venta, así que la cookie de ese alta es la única vía.</summary>
    private async Task<HttpClient> LoguearComoCajeroDeDispositivoAsync(Contexto ctx)
    {
        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = ctx.IdTenant,
                NombreUsuario = "cajero-rendicion",
                Mail = $"cajero-rendicion-{ctx.IdTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-rendicion", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={ctx.CookieDelDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cajero;
    }
}
