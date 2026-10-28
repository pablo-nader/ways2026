using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E2-S3a: <c>ServicioDePagos</c> registra y edita pagos con varias líneas por medio. Los tests llaman al servicio
/// directo (las rutas HTTP siguen siendo de una sola línea) con un contenedor propio cuyo contexto de base y de
/// usuario son fijos; el turno se abre y se cierra por HTTP.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosConLineasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdAdmin, HttpClient Admin, int IdProveedor,
        int IdArea, int IdEfectivo, int IdElectronico, int IdTercero);

    private sealed class RelojDePrueba : IRelojDelSistema
    {
        public DateTimeOffset Ahora => DateTimeOffset.UtcNow;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idElectronico = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();
        var tercero = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = $"{nombre}-tercero", Orden = 50,
            Comportamiento = ComportamientoMedioPago.Electronico, AdmiteVuelto = false, RequiereReferencia = false,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(tercero);

        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var condicion = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        plataforma.Areas.Add(area);
        plataforma.CondicionesFiscales.Add(condicion);
        await plataforma.SaveChangesAsync();
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicion.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Proveedores.Add(proveedor);
        await plataforma.SaveChangesAsync();
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin,
            proveedor.Id, area.Id, idEfectivo, idElectronico, tercero.Id);
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    /// <summary>El servicio real con un contenedor que ata todo al mismo contexto de base y a un usuario fijo.</summary>
    private static async Task<T> ConServicioAsync<T>(Contexto ctx, WaysDbContext db, Func<ServicioDePagos, Task<T>> accion)
    {
        var servicios = new ServiceCollection();
        servicios.AgregarApplication();
        servicios.AddSingleton<IWaysDbContext>(db);
        servicios.AddSingleton<IRelojDelSistema>(new RelojDePrueba());
        servicios.AddSingleton<IContextoDeUsuario>(new ContextoFijo(ctx.IdTenant, ctx.IdAdmin));
        await using var proveedor = servicios.BuildServiceProvider();
        await using var alcance = proveedor.CreateAsyncScope();
        return await accion(alcance.ServiceProvider.GetRequiredService<ServicioDePagos>());
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private static async Task CerrarTurnoAsync(Contexto ctx, int idTurno, params ConteoDeclarado[] conteos)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre(conteos, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    private static ComandoDePago ComandoPos(Contexto ctx, params LineaDePago[] lineas) =>
        new(ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto con líneas", null, lineas, null);

    private static ComandoDePagoDeAdministracion ComandoAdmin(
        Contexto ctx, CategoriaPago categoria, int? idProveedor, params LineaDePago[] lineas) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), ctx.IdEmpresa, ctx.IdPuntoVenta, categoria, idProveedor,
            null, "Gasto admin con líneas", null, lineas, null);

    private static ComandoDeEdicionDePago Edicion(params LineaDePago[] lineas) =>
        new(CategoriaPago.Otros, null, null, "Concepto editado", null, lineas, null);

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString()!;

    private static async Task<List<ItemPago>> ItemsVivosAsync(WaysDbContext db, int idPago) =>
        await db.ItemsPago.Where(i => i.IdPago == idPago).OrderBy(i => i.Id).ToListAsync();

    // ---- POS --------------------------------------------------------------------------------

    /// <summary>El importe del pago es la suma, la cabecera repite el medio de la primera línea y el cierre resta de
    /// cada medio la suma de SUS líneas (efectivo aparece dos veces).</summary>
    [Fact]
    public async Task ElPagoDelPosConTresLineasSumaElTotalYElArqueoSeReparteConLasLineas()
    {
        var ctx = await PrepararAsync(nameof(ElPagoDelPosConTresLineasSumaElTotalYElArqueoSeReparteConLasLineas));
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        await using (var db = Db(ctx))
        {
            var pago = await ConServicioAsync(ctx, db, s => s.RegistrarAsync(ComandoPos(
                ctx,
                new LineaDePago(ctx.IdEfectivo, 60m, null),
                new LineaDePago(ctx.IdElectronico, 30m, " cupón 7 "),
                new LineaDePago(ctx.IdEfectivo, 10m, null))));

            Assert.Equal((100m, ctx.IdEfectivo), (pago.Importe, pago.IdMedioPago));
            var items = await ItemsVivosAsync(db, pago.Id);
            Assert.Equal(
                [(ctx.IdEfectivo, 60m, (string?)null), (ctx.IdElectronico, 30m, "cupón 7"), (ctx.IdEfectivo, 10m, null)],
                items.Select(i => (i.IdMedioPago, i.Importe, i.Referencia)));
        }

        await CerrarTurnoAsync(
            ctx, idTurno, new ConteoDeclarado(ctx.IdEfectivo, 930m), new ConteoDeclarado(ctx.IdElectronico, 0m));

        await using var lectura = Db(ctx);
        var filas = await lectura.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        Assert.Equal(930m, filas.Single(a => a.IdMedioPago == ctx.IdEfectivo).ImporteEsperado);
        Assert.Equal(-30m, filas.Single(a => a.IdMedioPago == ctx.IdElectronico).ImporteEsperado);
    }

    // ---- administración -----------------------------------------------------------------------

    /// <summary>La tesorería sigue teniendo UN movimiento por pago, por el total, y el proveedor baja el total.</summary>
    [Fact]
    public async Task ElPagoAdministrativoConDosLineasEgresaElTotalUnaSolaVezYBajaElSaldoDelProveedor()
    {
        var ctx = await PrepararAsync(nameof(ElPagoAdministrativoConDosLineasEgresaElTotalUnaSolaVezYBajaElSaldoDelProveedor));

        await using var db = Db(ctx);
        var pago = await ConServicioAsync(ctx, db, s => s.RegistrarDeAdministracionAsync(ComandoAdmin(
            ctx, CategoriaPago.Proveedor, ctx.IdProveedor,
            new LineaDePago(ctx.IdEfectivo, 70m, null), new LineaDePago(ctx.IdElectronico, 55m, null))));

        Assert.Equal(125m, pago.Importe);
        var movimiento = await db.MovimientosTesoreria.SingleAsync(m => m.IdPago == pago.Id);
        Assert.Equal((0m, 125m), (movimiento.Ingreso, movimiento.Egreso));
        Assert.Equal(-125m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
        Assert.Equal(2, (await ItemsVivosAsync(db, pago.Id)).Count);
    }

    /// <summary>Un medio de cuenta corriente en CUALQUIER línea se rechaza con el código de siempre y no se escribe nada.</summary>
    [Fact]
    public async Task UnMedioDeCuentaCorrienteEnCualquierLineaSeRechazaYNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnMedioDeCuentaCorrienteEnCualquierLineaSeRechazaYNoEscribeNada));
        int idCuentaCorriente;
        await using (var siembra = Db(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var medio = new MedioPago
            {
                IdTenant = ctx.IdTenant, Nombre = "CC-lineas", Orden = 60, Comportamiento = ComportamientoMedioPago.CuentaCorriente,
                AdmiteVuelto = false, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            siembra.MediosPago.Add(medio);
            await siembra.SaveChangesAsync();
            idCuentaCorriente = medio.Id;
        }

        await using var db = Db(ctx);
        var error = await Assert.ThrowsAsync<ErrorDominio>(() => ConServicioAsync(ctx, db, s => s.RegistrarDeAdministracionAsync(
            ComandoAdmin(ctx, CategoriaPago.Otros, null,
                new LineaDePago(ctx.IdEfectivo, 10m, null), new LineaDePago(idCuentaCorriente, 5m, null)))));

        Assert.Equal("gasto_medio_pago_cuenta_corriente_invalido", error.Codigo);
        Assert.Empty(await db.Pagos.ToListAsync());
    }

    // ---- pagar compra -------------------------------------------------------------------------

    private async Task<(int IdCompra, decimal Total)> CrearYConfirmarCompraAsync(Contexto ctx)
    {
        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuota = await plataforma.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idTipo = await plataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"lineas-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = ctx.IdArea, IdAlicuotaIva = idAlicuota, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Articulos.Add(articulo);
        await plataforma.SaveChangesAsync();

        var alta = await ctx.Admin.PostAsJsonAsync("/api/compras", new SolicitudDeCompra(
            ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, "0001-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(articulo.Id, "Item", 10m, null, null, 100m, 0m, idAlicuota, true)]));
        var cuerpo = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpo);
        var idCompra = JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!.Id;
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null)).StatusCode);

        await using var db = Db(ctx);
        return (idCompra, await db.ComprobantesCompra.Where(c => c.Id == idCompra).Select(c => c.Total).SingleAsync());
    }

    /// <summary>El saldo pendiente se compara con la SUMA de las líneas: una suma que lo supera se rechaza aunque cada
    /// línea sola entre, y una suma exacta lo deja en cero.</summary>
    [Fact]
    public async Task PagarUnaCompraConDosLineasComparaLaSumaContraElSaldoPendiente()
    {
        var ctx = await PrepararAsync(nameof(PagarUnaCompraConDosLineasComparaLaSumaContraElSaldoPendiente));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var mitad = Math.Round(total / 2m, 2);
        var fecha = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        await using var db = Db(ctx);
        var error = await Assert.ThrowsAsync<ErrorDominio>(() => ConServicioAsync(ctx, db, s => s.PagarCompraAsync(
            idCompra, new ComandoDePagoDeCompra(fecha,
                [new LineaDePago(ctx.IdEfectivo, mitad, null), new LineaDePago(ctx.IdElectronico, total - mitad + 1m, null)],
                null))));
        Assert.Equal("pago_excede_saldo_pendiente", error.Codigo);
        Assert.Empty(await db.Pagos.ToListAsync());

        var resultado = await ConServicioAsync(ctx, db, s => s.PagarCompraAsync(
            idCompra, new ComandoDePagoDeCompra(fecha,
                [new LineaDePago(ctx.IdEfectivo, mitad, null), new LineaDePago(ctx.IdElectronico, total - mitad, null)],
                null)));

        Assert.Equal((total, 0m), (resultado.Pagado, resultado.SaldoPendiente));
        Assert.Equal(total, resultado.Pago.Importe);
        Assert.Equal(2, (await ItemsVivosAsync(db, resultado.Pago.Id)).Count);
    }

    // ---- edición: reemplazo del conjunto de líneas ------------------------------------------------

    /// <summary>De 2 a 3 líneas y de 3 a 1 sobre un turno CERRADO: el arqueo se recalcula por medio, la cabecera sigue
    /// al total y a la primera línea, los ítems sobrantes quedan dados de baja y la auditoría registra las líneas.</summary>
    [Fact]
    public async Task EditarElConjuntoDeLineasDeUnPagoDeTurnoCerradoRecalculaElArqueoPorMedio()
    {
        var ctx = await PrepararAsync(nameof(EditarElConjuntoDeLineasDeUnPagoDeTurnoCerradoRecalculaElArqueoPorMedio));
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        await using var db = Db(ctx);
        var pago = await ConServicioAsync(ctx, db, s => s.RegistrarAsync(ComandoPos(
            ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdElectronico, 40m, null))));
        await CerrarTurnoAsync(
            ctx, idTurno, new ConteoDeclarado(ctx.IdEfectivo, 940m), new ConteoDeclarado(ctx.IdElectronico, 0m));

        await ConServicioAsync(ctx, db, s => s.EditarDeAdministracionAsync(pago.Id, Edicion(
            new LineaDePago(ctx.IdEfectivo, 50m, null), new LineaDePago(ctx.IdElectronico, 30m, null),
            new LineaDePago(ctx.IdTercero, 20m, "op 1"))));

        db.ChangeTracker.Clear();
        var tres = await ItemsVivosAsync(db, pago.Id);
        Assert.Equal(
            [(ctx.IdEfectivo, 50m), (ctx.IdElectronico, 30m), (ctx.IdTercero, 20m)],
            tres.Select(i => (i.IdMedioPago, i.Importe)));
        Assert.Equal(100m, await db.Pagos.Where(p => p.Id == pago.Id).Select(p => p.Importe).SingleAsync());
        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        Assert.Equal(950m, filas.Single(a => a.IdMedioPago == ctx.IdEfectivo).ImporteEsperado);
        Assert.Equal(-30m, filas.Single(a => a.IdMedioPago == ctx.IdElectronico).ImporteEsperado);
        Assert.Equal(-20m, filas.Single(a => a.IdMedioPago == ctx.IdTercero).ImporteEsperado);

        await ConServicioAsync(ctx, db, s => s.EditarDeAdministracionAsync(pago.Id, Edicion(
            new LineaDePago(ctx.IdTercero, 80m, null))));

        db.ChangeTracker.Clear();
        var uno = await ItemsVivosAsync(db, pago.Id);
        Assert.Equal((ctx.IdTercero, 80m), (uno.Single().IdMedioPago, uno.Single().Importe));
        var todos = await db.ItemsPago.IgnoreQueryFilters().Where(i => i.IdPago == pago.Id).ToListAsync();
        Assert.Equal(3, todos.Count);
        Assert.Equal(2, todos.Count(i => i.DeletedAt != null));
        var cabecera = await db.Pagos.SingleAsync(p => p.Id == pago.Id);
        Assert.Equal(80m, cabecera.Importe);
        filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        Assert.Equal(1000m, filas.Single(a => a.IdMedioPago == ctx.IdEfectivo).ImporteEsperado);
        Assert.Equal(0m, filas.Single(a => a.IdMedioPago == ctx.IdElectronico).ImporteEsperado);
        Assert.Equal(-80m, filas.Single(a => a.IdMedioPago == ctx.IdTercero).ImporteEsperado);

        var auditoria = await db.Auditoria.Where(a => a.Accion == "pago.edicion" && a.IdEntidad == pago.Id)
            .OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, auditoria.Count);
        Assert.Equal(3, JsonDocument.Parse(auditoria[0].ValorNuevo).RootElement.GetProperty("lineas").GetArrayLength());
        Assert.Equal(1, JsonDocument.Parse(auditoria[1].ValorNuevo).RootElement.GetProperty("lineas").GetArrayLength());
    }

    // ---- compatibilidad de las rutas actuales ------------------------------------------------------

    /// <summary>Las rutas actuales solo conocen un medio: editar por ahí un pago de dos ítems responde 409 y no cambia
    /// nada (ni cabecera, ni ítems, ni ajustes), en vez de colapsar las líneas.</summary>
    [Fact]
    public async Task LaRutaActualNoEditaUnPagoConVariosMediosYNoCambiaNada()
    {
        var ctx = await PrepararAsync(nameof(LaRutaActualNoEditaUnPagoConVariosMediosYNoCambiaNada));
        await AbrirTurnoAsync(ctx, 1000m);

        PagoRegistrado pago;
        await using (var db = Db(ctx))
        {
            pago = await ConServicioAsync(ctx, db, s => s.RegistrarAsync(ComandoPos(
                ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdElectronico, 40m, null))));
        }

        var edicion = new SolicitudDeEdicionDePago(CategoriaPago.Otros, null, null, "Otro concepto", null, ctx.IdEfectivo, null, 100m);
        foreach (var ruta in new[] { $"/api/gastos/{pago.Id}", $"/api/gastos/administracion/{pago.Id}" })
        {
            var respuesta = await ctx.Admin.PutAsJsonAsync(ruta, edicion);
            Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
            Assert.Equal("gasto_con_varios_medios", await CodigoAsync(respuesta));
        }

        await using var lectura = Db(ctx);
        var cabecera = await lectura.Pagos.SingleAsync(p => p.Id == pago.Id);
        Assert.Equal(("Gasto con líneas", 100m), (cabecera.Concepto, cabecera.Importe));
        Assert.Equal(
            [(ctx.IdEfectivo, 60m), (ctx.IdElectronico, 40m)],
            (await ItemsVivosAsync(lectura, pago.Id)).Select(i => (i.IdMedioPago, i.Importe)));
        Assert.Empty(await lectura.Auditoria.Where(a => a.Accion == "pago.edicion" && a.IdEntidad == pago.Id).ToListAsync());
    }

    // ---- E2-S3b: el medio vive solo en los ítems ---------------------------------------------

    /// <summary>El detalle del turno y el historial siguen exponiendo un solo medio por pago (el de la primera línea, el ítem
    /// vivo de menor id) y agregan <c>lineas</c> con el reparto completo, en el orden de los ítems.</summary>
    [Fact]
    public async Task ElListadoDeUnPagoDeDosLineasMuestraElMedioDeLaPrimeraYTodasLasLineas()
    {
        var ctx = await PrepararAsync(nameof(ElListadoDeUnPagoDeDosLineasMuestraElMedioDeLaPrimeraYTodasLasLineas));
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        int idPago;
        await using (var db = Db(ctx))
        {
            var pago = await ConServicioAsync(ctx, db, s => s.RegistrarAsync(ComandoPos(
                ctx,
                new LineaDePago(ctx.IdElectronico, 40m, "cupón 9"),
                new LineaDePago(ctx.IdEfectivo, 60m, null))));
            idPago = pago.Id;
            Assert.Equal(ctx.IdElectronico, pago.IdMedioPago);
        }

        var detalle = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/caja/turnos/{idTurno}/detalle");
        var enElDetalle = detalle.GetProperty("gastos").EnumerateArray().Single(g => g.GetProperty("id").GetInt32() == idPago);

        var historial = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/gastos?idPuntoVenta={ctx.IdPuntoVenta}");
        var enElHistorial = historial.GetProperty("items").EnumerateArray().Single(g => g.GetProperty("id").GetInt32() == idPago);

        await using var lectura = Db(ctx);
        var idsDeItems = (await ItemsVivosAsync(lectura, idPago)).Select(i => i.Id).ToList();

        foreach (var fila in new[] { enElDetalle, enElHistorial })
        {
            Assert.Equal(ctx.IdElectronico, fila.GetProperty("idMedioPago").GetInt32());
            Assert.Equal(100m, fila.GetProperty("importe").GetDecimal());

            var lineas = fila.GetProperty("lineas").EnumerateArray().ToList();
            Assert.Equal(idsDeItems, lineas.Select(l => l.GetProperty("idItem").GetInt32()));
            Assert.Equal([ctx.IdElectronico, ctx.IdEfectivo], lineas.Select(l => l.GetProperty("idMedioPago").GetInt32()));
            Assert.Equal([40m, 60m], lineas.Select(l => l.GetProperty("importe").GetDecimal()));
            Assert.Equal("cupón 9", lineas[0].GetProperty("referencia").GetString());
            Assert.Equal(JsonValueKind.Null, lineas[1].GetProperty("referencia").ValueKind);
            Assert.All(lineas, l => Assert.False(string.IsNullOrEmpty(l.GetProperty("nombreMedioPago").GetString())));
        }
    }

    /// <summary>Un medio usado solo por una línea de pago (la cabecera ya no guarda el medio) sigue bloqueando su baja con
    /// el rótulo "gastos" de <c>items_pago</c>.</summary>
    [Fact]
    public async Task UnMedioUsadoSoloPorUnaLineaDeUnPagoNoSePuedeDarDeBaja()
    {
        var ctx = await PrepararAsync(nameof(UnMedioUsadoSoloPorUnaLineaDeUnPagoNoSePuedeDarDeBaja));
        await AbrirTurnoAsync(ctx, 1000m);

        await using (var db = Db(ctx))
        {
            await ConServicioAsync(ctx, db, s => s.RegistrarAsync(ComandoPos(
                ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdTercero, 40m, null))));
        }

        var respuesta = await ctx.Admin.DeleteAsync($"/api/catalogos/medios-pago/{ctx.IdTercero}");
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("medio_pago_en_uso", cuerpo.GetProperty("codigo").GetString());
        Assert.Contains("gastos", cuerpo.GetProperty("title").GetString(), StringComparison.Ordinal);
    }
}
