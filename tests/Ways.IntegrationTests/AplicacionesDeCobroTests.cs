using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Catalogos;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E7-S2 (aplicaciones de cobros a ventas): el esquema de <c>aplicaciones_cobro</c> con SQL crudo bajo <c>ways_app</c>
/// (CHECK, unicidad parcial y los constraint triggers diferidos, cada uno con su SQLSTATE y su <c>CONSTRAINT</c>),
/// <see cref="ServicioDeCobros"/> (aplicar, desaplicar, rechazos antes de escribir) y la liberación por anulación de la
/// venta o del recibo. Los comprobantes se siembran directo con EF: el recibo y la venta reales se prueban en
/// <c>PagosACuentaTests</c> y en las pruebas de ventas; acá importa solo su tipo, estado, cliente y cobros.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicacionesDeCobroTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private const string TriggerComprobantes = "tg_aplicaciones_cobro_comprobantes_validos";
    private const string TriggerCobro = "tg_aplicaciones_cobro_no_excede_cobro";
    private const string TriggerVenta = "tg_aplicaciones_cobro_no_excede_venta";
    private const string TriggerCliente = "tg_aplicaciones_cobro_mismo_cliente";

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdAdmin, int IdListaPrecio, int IdMedioEfectivo, int IdMedioCuentaCorriente,
        int IdCliente, int IdOtroCliente, HttpClient Admin);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "contexto-fijo";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => idTenant;
    }

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        // Nombre corto y único: los nombres de las pruebas superan el largo de la parte local de un mail.
        var unico = "cobros-" + Guid.NewGuid().ToString("N")[..12];
        var mailAdmin = $"{unico}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = ContextoDelTenant(resultado.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        Cliente NuevoCliente(string n) => new()
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = n,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = idListaPrecio, LimiteCredito = 0m,
            CreditoIlimitado = true, Saldo = 0m, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };

        var cliente = NuevoCliente(nombre);
        var otro = NuevoCliente(nombre + " otro");
        db.Clientes.AddRange(cliente, otro);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idListaPrecio, idMedioEfectivo,
            medioCc.Id, cliente.Id, otro.Id, admin);
    }

    private WaysDbContext ContextoDelTenant(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private static ServicioDeCobros Servicio(WaysDbContext db, Contexto ctx)
    {
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, ctx.IdAdmin);
        return new ServicioDeCobros(db, reloj, contexto, new ServicioDeAuditoria(db, reloj, contexto));
    }

    /// <summary>Un comprobante con sus cobros; el total es la suma de los importes.</summary>
    private async Task<int> SembrarComprobanteAsync(
        Contexto ctx, string codigoTipo, int idCliente, params (int IdMedio, decimal Importe, decimal Vuelto)[] cobros)
    {
        await using var db = ContextoDelTenant(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == codigoTipo).Select(t => t.Id).SingleAsync();
        var total = cobros.Sum(c => c.Importe - c.Vuelto);

        var comprobante = new ComprobanteVenta
        {
            IdTipoComprobante = idTipo, Numero = Random.Shared.Next(1, int.MaxValue), Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleado = ctx.IdAdmin, IdCliente = idCliente, Subtotal = total,
            DescuentoTotal = 0m, Total = total, Estado = EstadoComprobante.Emitido, FacturacionDecididaEn = ahora,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.Cobros.AddRange(cobros.Select(c => new Cobro
        {
            IdComprobanteVenta = comprobante.Id, IdMedioPago = c.IdMedio, Importe = c.Importe, Vuelto = c.Vuelto,
            CreatedAt = ahora, UpdatedAt = ahora
        }));
        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    private Task<int> SembrarReciboAsync(Contexto ctx, decimal importe, int? idCliente = null) =>
        SembrarComprobanteAsync(ctx, "RC", idCliente ?? ctx.IdCliente, (ctx.IdMedioEfectivo, importe, 0m));

    private Task<int> SembrarVentaEnCuentaCorrienteAsync(Contexto ctx, decimal enCuentaCorriente, decimal enEfectivo = 0m,
        int? idCliente = null) =>
        enEfectivo == 0m
            ? SembrarComprobanteAsync(ctx, "TX", idCliente ?? ctx.IdCliente, (ctx.IdMedioCuentaCorriente, enCuentaCorriente, 0m))
            : SembrarComprobanteAsync(
                ctx, "TX", idCliente ?? ctx.IdCliente,
                (ctx.IdMedioCuentaCorriente, enCuentaCorriente, 0m), (ctx.IdMedioEfectivo, enEfectivo, 0m));

    private static NpgsqlParameter P(object? valor) => new() { Value = valor ?? DBNull.Value };

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, NpgsqlTransaction? tx, string sql, params object?[] valores)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = tx;
        comando.CommandText = sql;
        foreach (var valor in valores)
        {
            comando.Parameters.Add(P(valor));
        }

        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql, params object?[] valores)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in valores)
        {
            comando.Parameters.Add(P(valor));
        }

        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private static Task<int> InsertarAplicacionAsync(
        NpgsqlConnection conexion, NpgsqlTransaction? tx, Contexto ctx, int idRecibo, int idVenta, decimal importe,
        DateTimeOffset? baja = null) =>
        EjecutarAsync(
            conexion, tx,
            "INSERT INTO aplicaciones_cobro (id_tenant, id_comprobante_cobro, id_comprobante_venta, importe, id_empleado, " +
            "fecha, created_at, updated_at, deleted_at) VALUES ($1, $2, $3, $4, $5, now(), now(), now(), $6)",
            ctx.IdTenant, idRecibo, idVenta, importe, ctx.IdAdmin, baja);

    private static async Task<PostgresException> LaTransaccionFallaAlComitearAsync(
        NpgsqlConnection conexion, Func<NpgsqlTransaction, Task> escribir)
    {
        await using var tx = await conexion.BeginTransactionAsync();
        await escribir(tx);
        return await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
    }

    private static async Task LaTransaccionComiteaAsync(NpgsqlConnection conexion, Func<NpgsqlTransaction, Task> escribir)
    {
        await using var tx = await conexion.BeginTransactionAsync();
        await escribir(tx);
        await tx.CommitAsync();
    }

    // ---- esquema: CHECK y unicidad -------------------------------------------------------------

    [Fact]
    public async Task UnaAplicacionConImporteCeroViolaLaCheckDeImportePositivo()
    {
        var ctx = await PrepararAsync(nameof(UnaAplicacionConImporteCeroViolaLaCheckDeImportePositivo));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarAplicacionAsync(cruda, null, ctx, recibo, venta, 0m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_aplicaciones_cobro_importe_positivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task DosAplicacionesVivasDelMismoParViolanLaUnicidadParcialYUnaDadaDeBajaNo()
    {
        var ctx = await PrepararAsync(nameof(DosAplicacionesVivasDelMismoParViolanLaUnicidadParcialYUnaDadaDeBajaNo));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 30m));

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarAplicacionAsync(cruda, null, ctx, recibo, venta, 20m));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_aplicaciones_cobro_cobro_venta", excepcion.ConstraintName);

        await LaTransaccionComiteaAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 20m, baja: DateTimeOffset.UtcNow));
    }

    // ---- constraint triggers -------------------------------------------------------------------

    /// <summary>LA CLÁUSULA: lo aplicado de un recibo contra su neto cobrado (importe − vuelto). El recibo cobró 120 con 20
    /// de vuelto: 100 comitea y 100,01 falla al comitear.</summary>
    [Fact]
    public async Task AplicarMasQueLoCobradoDelReciboFallaAlComitearConSuConstraint()
    {
        var ctx = await PrepararAsync(nameof(AplicarMasQueLoCobradoDelReciboFallaAlComitearConSuConstraint));
        var recibo = await SembrarComprobanteAsync(ctx, "RC", ctx.IdCliente, (ctx.IdMedioEfectivo, 120m, 20m));
        var ventaA = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m);
        var ventaB = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        var excepcion = await LaTransaccionFallaAlComitearAsync(cruda, async tx =>
        {
            await InsertarAplicacionAsync(cruda, tx, ctx, recibo, ventaA, 60m);
            await InsertarAplicacionAsync(cruda, tx, ctx, recibo, ventaB, 40.01m);
        });
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerCobro, excepcion.ConstraintName);

        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarAplicacionAsync(cruda, tx, ctx, recibo, ventaA, 60m);
            await InsertarAplicacionAsync(cruda, tx, ctx, recibo, ventaB, 40m);
        });
    }

    /// <summary>LA CLÁUSULA: lo aplicado de una venta contra su parte en cuenta corriente, no contra su total. La venta
    /// cobró 300 en cuenta corriente y 200 en efectivo: 300 comitea y 300,01 falla.</summary>
    [Fact]
    public async Task AplicarMasQueLaParteEnCuentaCorrienteDeLaVentaFallaAlComitearConSuConstraint()
    {
        var ctx = await PrepararAsync(nameof(AplicarMasQueLaParteEnCuentaCorrienteDeLaVentaFallaAlComitearConSuConstraint));
        var recibo = await SembrarReciboAsync(ctx, 1000m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 300m, enEfectivo: 200m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 300.01m));
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerVenta, excepcion.ConstraintName);

        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 300m));
    }

    [Fact]
    public async Task UnaVentaComoCobroOUnReciboComoVentaFallanAlComitear()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaComoCobroOUnReciboComoVentaFallanAlComitear));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var otroRecibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        var otraVenta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        var ventaComoCobro = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, venta, otraVenta, 10m));
        Assert.Equal("23514", ventaComoCobro.SqlState);
        Assert.Equal(TriggerComprobantes, ventaComoCobro.ConstraintName);

        var reciboComoVenta = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, otroRecibo, 10m));
        Assert.Equal("23514", reciboComoVenta.SqlState);
        Assert.Equal(TriggerComprobantes, reciboComoVenta.ConstraintName);
    }

    [Fact]
    public async Task UnaVentaDeOtroClienteFallaAlComitear()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaDeOtroClienteFallaAlComitear));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m, idCliente: ctx.IdOtroCliente);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 10m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerCliente, excepcion.ConstraintName);
    }

    /// <summary>LA CLÁUSULA: el trigger sobre <c>comprobantes_venta</c>. Anular el recibo o la venta sin liberar sus
    /// aplicaciones falla al comitear; liberarlas en la misma transacción comitea.</summary>
    [Fact]
    public async Task AnularUnComprobanteConAplicacionesVivasFallaAlComitearYLiberandolasComitea()
    {
        var ctx = await PrepararAsync(nameof(AnularUnComprobanteConAplicacionesVivasFallaAlComitearYLiberandolasComitea));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 50m));

        foreach (var anulado in new[] { recibo, venta })
        {
            var excepcion = await LaTransaccionFallaAlComitearAsync(
                cruda, tx => EjecutarAsync(
                    cruda, tx, "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1", anulado));
            Assert.Equal("23514", excepcion.SqlState);
            Assert.Equal(TriggerComprobantes, excepcion.ConstraintName);
        }

        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await EjecutarAsync(cruda, tx, "UPDATE comprobantes_venta SET estado = 'anulado' WHERE id_comprobante_venta = $1", venta);
            await EjecutarAsync(
                cruda, tx, "UPDATE aplicaciones_cobro SET deleted_at = now() WHERE id_comprobante_venta = $1", venta);
        });
    }

    /// <summary>LA CLÁUSULA: el trigger sobre <c>cobros</c>. Bajar el importe del cobro del recibo por debajo de lo aplicado
    /// falla al comitear.</summary>
    [Fact]
    public async Task BajarElCobroDelReciboPorDebajoDeLoAplicadoFallaAlComitear()
    {
        var ctx = await PrepararAsync(nameof(BajarElCobroDelReciboPorDebajoDeLoAplicadoFallaAlComitear));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, ctx, recibo, venta, 80m));

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE cobros SET importe = 50 WHERE id_comprobante_venta = $1", recibo));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerCobro, excepcion.ConstraintName);
    }

    // ---- servicio ------------------------------------------------------------------------------

    [Fact]
    public async Task AplicarParteDeUnReciboDejaElRestoDisponibleYBajaElSaldoDeLaVenta()
    {
        var ctx = await PrepararAsync(nameof(AplicarParteDeUnReciboDejaElRestoDisponibleYBajaElSaldoDeLaVenta));
        var recibo = await SembrarReciboAsync(ctx, 1000m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 600m, enEfectivo: 100m);

        await using var db = ContextoDelTenant(ctx.IdTenant);
        var estado = await Servicio(db, ctx).AplicarAsync(recibo, [new AplicacionDeCobroPedida(venta, 250m)]);

        Assert.Equal(1000m, estado.NetoCobrado);
        Assert.Equal(250m, estado.Aplicado);
        Assert.Equal(750m, estado.Disponible);
        var aplicacion = Assert.Single(estado.Aplicaciones);
        Assert.Equal((venta, 250m), (aplicacion.IdComprobanteVenta, aplicacion.Importe));

        await using var lectura = ContextoDelTenant(ctx.IdTenant);
        var saldo = (await LectorDeAplicacionesDeCobro.LeerSaldoPorVentaAsync(lectura, [venta], default))[venta];
        Assert.Equal(new SaldoDeVenta(venta, 600m, 250m, 350m), saldo);
        Assert.Equal(
            1, await lectura.Auditoria.CountAsync(a => a.Accion == "cobro.aplicacion" && a.IdEntidad == recibo));
    }

    [Fact]
    public async Task UnReciboSeAplicaAVariasVentasYAplicarDeNuevoAgrandaLaMismaFila()
    {
        var ctx = await PrepararAsync(nameof(UnReciboSeAplicaAVariasVentasYAplicarDeNuevoAgrandaLaMismaFila));
        var recibo = await SembrarReciboAsync(ctx, 1000m);
        var ventaA = await SembrarVentaEnCuentaCorrienteAsync(ctx, 400m);
        var ventaB = await SembrarVentaEnCuentaCorrienteAsync(ctx, 400m);

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            await Servicio(db, ctx).AplicarAsync(
                recibo, [new AplicacionDeCobroPedida(ventaB, 300m), new AplicacionDeCobroPedida(ventaA, 400m)]);
        }

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            var estado = await Servicio(db, ctx).AplicarAsync(recibo, [new AplicacionDeCobroPedida(ventaB, 100m)]);

            Assert.Equal(800m, estado.Aplicado);
            Assert.Equal(200m, estado.Disponible);
            Assert.Equal(
                [(ventaA, 400m), (ventaB, 400m)],
                estado.Aplicaciones.OrderBy(a => a.IdComprobanteVenta).Select(a => (a.IdComprobanteVenta, a.Importe)));
        }

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        Assert.Equal(2L, await EscalarAsync<long>(
            cruda, "SELECT count(*) FROM aplicaciones_cobro WHERE id_comprobante_cobro = $1", recibo));
    }

    [Fact]
    public async Task ElServicioRechazaAntesDeEscribirConLosMismosCodigosQueLosTriggers()
    {
        var ctx = await PrepararAsync(nameof(ElServicioRechazaAntesDeEscribirConLosMismosCodigosQueLosTriggers));
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m);
        var ventaAjena = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m, idCliente: ctx.IdOtroCliente);
        var otroRecibo = await SembrarReciboAsync(ctx, 100m);

        async Task<ErrorDominio> Rechazo(int idRecibo, int idVenta, decimal importe)
        {
            await using var db = ContextoDelTenant(ctx.IdTenant);
            return await Assert.ThrowsAsync<ErrorDominio>(
                () => Servicio(db, ctx).AplicarAsync(idRecibo, [new AplicacionDeCobroPedida(idVenta, importe)]));
        }

        Assert.Equal("cobro_excede_disponible", (await Rechazo(recibo, venta, 100.01m)).Codigo);
        Assert.Equal("aplicacion_cobro_otro_cliente", (await Rechazo(recibo, ventaAjena, 10m)).Codigo);
        Assert.Equal("aplicacion_cobro_comprobante_invalido", (await Rechazo(recibo, otroRecibo, 10m)).Codigo);
        Assert.Equal("aplicacion_cobro_comprobante_invalido", (await Rechazo(venta, ventaAjena, 10m)).Codigo);
        Assert.Equal(404, (await Rechazo(recibo, 999_999, 10m)).EstadoHttp);

        var ventaChica = await SembrarVentaEnCuentaCorrienteAsync(ctx, 40m);
        Assert.Equal("aplicacion_excede_saldo_de_venta", (await Rechazo(recibo, ventaChica, 40.01m)).Codigo);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        Assert.Equal(0L, await EscalarAsync<long>(
            cruda, "SELECT count(*) FROM aplicaciones_cobro WHERE id_comprobante_cobro = $1", recibo));
    }

    [Fact]
    public async Task DesaplicarDevuelveLoAplicadoAlDisponibleYDejaRastro()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarDevuelveLoAplicadoAlDisponibleYDejaRastro));
        var recibo = await SembrarReciboAsync(ctx, 500m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m);

        int idAplicacion;
        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            var aplicado = await Servicio(db, ctx).AplicarAsync(recibo, [new AplicacionDeCobroPedida(venta, 200m)]);
            idAplicacion = aplicado.Aplicaciones.Single().Id;
        }

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            var estado = await Servicio(db, ctx).DesaplicarAsync(recibo, idAplicacion);

            Assert.Equal(0m, estado.Aplicado);
            Assert.Equal(500m, estado.Disponible);
            Assert.Empty(estado.Aplicaciones);
        }

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            var otraVez = await Assert.ThrowsAsync<ErrorDominio>(() => Servicio(db, ctx).DesaplicarAsync(recibo, idAplicacion));
            Assert.Equal(404, otraVez.EstadoHttp);
        }

        await using var lectura = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(
            1, await lectura.Auditoria.CountAsync(a => a.Accion == "cobro.desaplicacion" && a.IdEntidad == recibo));
        Assert.Equal(
            500m, (await LectorDeAplicacionesDeCobro.LeerSaldoPorVentaAsync(lectura, [venta], default))[venta].SaldoPendiente);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnularElReciboOLaVentaLiberaSusAplicacionesConRastro(bool anularElRecibo)
    {
        var ctx = await PrepararAsync(nameof(AnularElReciboOLaVentaLiberaSusAplicacionesConRastro) + anularElRecibo);
        var recibo = await SembrarReciboAsync(ctx, 500m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m);
        var otraVenta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 500m);

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            await Servicio(db, ctx).AplicarAsync(
                recibo, [new AplicacionDeCobroPedida(venta, 200m), new AplicacionDeCobroPedida(otraVenta, 100m)]);
        }

        var anulado = anularElRecibo ? recibo : venta;
        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{anulado}/anulacion", null);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var vivasEsperadas = anularElRecibo ? 0L : 1L;
        Assert.Equal(vivasEsperadas, await EscalarAsync<long>(
            cruda, "SELECT count(*) FROM aplicaciones_cobro WHERE id_comprobante_cobro = $1 AND deleted_at IS NULL", recibo));
        Assert.Equal(anularElRecibo ? 2L : 1L, await EscalarAsync<long>(
            cruda,
            "SELECT count(*) FROM auditoria WHERE accion = 'cobro.desaplicacion' AND id_entidad = $1 " +
            "AND valor_nuevo ->> 'id_comprobante_anulado' = $2",
            recibo, anulado.ToString()));

        await using var lectura = ContextoDelTenant(ctx.IdTenant);
        var disponible = (await LectorDeAplicacionesDeCobro.LeerDisponiblePorReciboAsync(lectura, [recibo], default))[recibo];
        Assert.Equal(anularElRecibo ? 0m : 400m, disponible.Disponible);
    }

    // ---- aislamiento de tenant -----------------------------------------------------------------

    /// <summary>LA CLÁUSULA: la política RLS de <c>aplicaciones_cobro</c>, por SQL crudo bajo <c>ways_app</c> con el GUC de
    /// otro tenant (sin el filtro de EF delante); y el servicio de otro tenant responde 404 sobre el recibo ajeno.</summary>
    [Fact]
    public async Task OtroTenantNoVeNiAplicaLosCobrosAjenos()
    {
        var ctx = await PrepararAsync(nameof(OtroTenantNoVeNiAplicaLosCobrosAjenos));
        var ajeno = await PrepararAsync(nameof(OtroTenantNoVeNiAplicaLosCobrosAjenos) + "Ajeno");
        var recibo = await SembrarReciboAsync(ctx, 100m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 100m);
        var ventaAjena = await SembrarVentaEnCuentaCorrienteAsync(ajeno, 100m);

        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            await Servicio(db, ctx).AplicarAsync(recibo, [new AplicacionDeCobroPedida(venta, 50m)]);
        }

        await using (var crudaAjena = await fixture.AbrirConexionCrudaAsync("tenant", ajeno.IdTenant))
        {
            Assert.Equal(0L, await EscalarAsync<long>(
                crudaAjena, "SELECT count(*) FROM aplicaciones_cobro WHERE id_comprobante_cobro = $1", recibo));
        }

        await using var dbAjeno = ContextoDelTenant(ajeno.IdTenant);
        var rechazo = await Assert.ThrowsAsync<ErrorDominio>(
            () => Servicio(dbAjeno, ajeno).AplicarAsync(recibo, [new AplicacionDeCobroPedida(ventaAjena, 10m)]));
        Assert.Equal(404, rechazo.EstadoHttp);
    }

    // ---- carrera ------------------------------------------------------------------------------

    /// <summary>db-error-backstops: dos aplicaciones concurrentes del mismo recibo que juntas superan lo disponible. El
    /// <c>FOR UPDATE</c> del recibo las serializa y la segunda ve lo que la primera aplicó; si no lo hiciera, el trigger
    /// diferido la frenaría al comitear. Lo observable en los dos casos: una sola gana, la otra es 409
    /// <c>cobro_excede_disponible</c> y lo aplicado nunca supera lo cobrado.</summary>
    [Fact]
    public async Task DosAplicacionesConcurrentesDelMismoReciboNuncaSuperanLoDisponible()
    {
        var ctx = await PrepararAsync(nameof(DosAplicacionesConcurrentesDelMismoReciboNuncaSuperanLoDisponible));
        var recibo = await SembrarReciboAsync(ctx, 500m);
        var ventas = new[] { await SembrarVentaEnCuentaCorrienteAsync(ctx, 400m), await SembrarVentaEnCuentaCorrienteAsync(ctx, 400m) };

        using var puerta = new Barrier(2);
        var resultados = await Task.WhenAll(ventas.Select(venta => Task.Run(async () =>
        {
            await using var db = ContextoDelTenant(ctx.IdTenant);
            var servicio = Servicio(db, ctx);
            puerta.SignalAndWait(TimeSpan.FromSeconds(10));
            try
            {
                await servicio.AplicarAsync(recibo, [new AplicacionDeCobroPedida(venta, 300m)]);
                return (string?)null;
            }
            catch (ErrorDominio error)
            {
                return $"{error.EstadoHttp}:{error.Codigo}";
            }
            catch (PostgresException error) when (error.ConstraintName == TriggerCobro)
            {
                return "409:cobro_excede_disponible";
            }
        })));

        Assert.Single(resultados, r => r is null);
        Assert.Single(resultados, r => r == "409:cobro_excede_disponible");

        await using var lectura = ContextoDelTenant(ctx.IdTenant);
        var disponible = (await LectorDeAplicacionesDeCobro.LeerDisponiblePorReciboAsync(lectura, [recibo], default))[recibo];
        Assert.Equal(300m, disponible.Aplicado);
        Assert.Equal(200m, disponible.Disponible);
    }

    // ---- comportamiento del medio --------------------------------------------------------------

    private static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("codigo").GetString()!;

    private static MedioPagoAlta MedioCuentaCorrienteComo(ComportamientoMedioPago comportamiento) =>
        new("Cuenta corriente", null, 3, comportamiento, false, false, null);

    /// <summary>LA CLÁUSULA: <c>ServicioDeMediosPago</c> no deja que un medio con cobros salga de cuenta corriente. Sin la
    /// guarda el PUT da 200 y la parte en cuenta corriente de la venta pasa a 0 con dos recibos aplicados: anular uno
    /// dejaría viva la aplicación del otro por encima de esa parte y la anulación fallaría al comitear. Un id inexistente
    /// sigue dando 404 antes que el 409.</summary>
    [Fact]
    public async Task UnMedioConCobrosNoSaleDeCuentaCorrienteYLaAnulacionSigueAndando()
    {
        var ctx = await PrepararAsync(nameof(UnMedioConCobrosNoSaleDeCuentaCorrienteYLaAnulacionSigueAndando));
        var recibo = await SembrarReciboAsync(ctx, 500m);
        var otroRecibo = await SembrarReciboAsync(ctx, 500m);
        var venta = await SembrarVentaEnCuentaCorrienteAsync(ctx, 400m);
        await using (var db = ContextoDelTenant(ctx.IdTenant))
        {
            await Servicio(db, ctx).AplicarAsync(recibo, [new AplicacionDeCobroPedida(venta, 200m)]);
            await Servicio(db, ctx).AplicarAsync(otroRecibo, [new AplicacionDeCobroPedida(venta, 100m)]);
        }

        var inexistente = await ctx.Admin.PutAsJsonAsync(
            "/api/catalogos/medios-pago/999999999", MedioCuentaCorrienteComo(ComportamientoMedioPago.Electronico));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);

        var cambio = await ctx.Admin.PutAsJsonAsync(
            $"/api/catalogos/medios-pago/{ctx.IdMedioCuentaCorriente}", MedioCuentaCorrienteComo(ComportamientoMedioPago.Electronico));
        Assert.Equal(HttpStatusCode.Conflict, cambio.StatusCode);
        Assert.Equal("medio_pago_comportamiento_en_uso", await CodigoDeErrorAsync(cambio));

        await using (var lectura = ContextoDelTenant(ctx.IdTenant))
        {
            Assert.Equal(
                ComportamientoMedioPago.CuentaCorriente,
                await lectura.MediosPago.Where(m => m.Id == ctx.IdMedioCuentaCorriente).Select(m => m.Comportamiento).SingleAsync());
        }

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{recibo}/anulacion", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnMedioSinCobrosPuedeSalirDeCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(UnMedioSinCobrosPuedeSalirDeCuentaCorriente));

        var cambio = await ctx.Admin.PutAsJsonAsync(
            $"/api/catalogos/medios-pago/{ctx.IdMedioCuentaCorriente}", MedioCuentaCorrienteComo(ComportamientoMedioPago.Electronico));

        Assert.True(cambio.StatusCode == HttpStatusCode.OK, await cambio.Content.ReadAsStringAsync());
    }
}
