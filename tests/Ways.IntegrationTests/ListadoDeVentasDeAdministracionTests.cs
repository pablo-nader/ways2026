using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Ventas;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.RetirosDeAdministracionTests;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/ventas/administracion</c>: un filtro por columna y un orden por columna sobre un escenario fijo de once ventas
/// (sembradas directo, con fechas, turnos y conciliación controlados), más datos que nunca aparecen: una venta dada de baja, las
/// facturas espejo y una venta de otro tenant.
///
/// <list type="bullet">
/// <item>a: turno A (PV1, cerrado el 10-02, conciliado hasta el 10-03), Admin, José Pérez, efectivo, 10-01.</item>
/// <item>i: turno A, Admin, consumidor final, efectivo, 10-04: su fecha supera la marca pero el cierre no, así que cuenta el cierre.</item>
/// <item>b: turno C (PV1, cerrado el 10-14), Vendedor, Ana Gómez, efectivo y Zeta Pay (más un cobro dado de baja), 10-10.</item>
/// <item>f / g: turno C, con CAE / con la factura en trámite. k: turno C, sin cobros.</item>
/// <item>c: turno B (PV2, abierto), Vendedor, Alfa Pay y efectivo, 10-20. h: turno B, cuenta corriente, 10-21.</item>
/// <item>d: sin turno, Zeta Pay, 09-15. e: sin turno, PV2, anulada, 12-01. j: nota de crédito sin turno, 09-20.</item>
/// </list>
///
/// Evidencia de mutación (mutation-proof-tests), registrada al escribir las pruebas: (1) con todas las cláusulas de
/// <c>ServicioDeVentasDeAdministracion.Filtrar</c> borradas a la vez, cada fila de <see cref="CadaFiltroDejaSoloSusVentas"/> falla
/// (cada fila pasa un solo filtro); (2) con el <c>switch</c> de <c>Ordenar</c> reemplazado por el orden por id, cada fila de
/// <see cref="CadaColumnaOrdenaTodoElConjunto"/> falla (el escenario está armado para que ningún orden pedido coincida con el de
/// los ids, y la prueba lo afirma); (3) sin <c>SinEspejosFiscales</c>, <c>c.DeletedAt == null</c> o <c>co.DeletedAt == null</c>,
/// fallan respectivamente el total del listado sin filtros y el filtro por medio; (4) con la regla <c>min(cierre, fecha)</c>
/// reemplazada por la fecha de la venta, falla <see cref="LaConciliacionUsaLaMenorEntreElCierreYLaFechaDeLaVenta"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ListadoDeVentasDeAdministracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Escenario(
        Contexto Ctx, int IdPuntoVentaDos, int IdTurnoA, int IdTipoNcx, int IdVendedor, int IdZeta, int IdAlfa,
        long NumeroDeB, IReadOnlyDictionary<string, int> Ids, IReadOnlySet<int> Excluidas);

    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Escenario? _escenario;
    private static long _numero = 7_000_000;

    private async Task<Escenario> EscenarioAsync()
    {
        await Candado.WaitAsync();
        try
        {
            return _escenario ??= await SembrarAsync();
        }
        finally
        {
            Candado.Release();
        }
    }

    private WaysDbContext Db(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private static DateTimeOffset Dia(string fecha) => DateTimeOffset.Parse($"{fecha}T12:00:00Z");

    private async Task<int> SembrarTurnoAsync(Contexto ctx, int idPuntoVenta, string apertura, string? cierre)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var turno = new TurnoCaja
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = idPuntoVenta, IdEmpleadoApertura = ctx.IdAdmin, FechaApertura = Dia(apertura),
            FondoInicial = 0m, Estado = cierre is null ? EstadoTurno.Abierto : EstadoTurno.Cerrado,
            FechaCierre = cierre is null ? null : Dia(cierre), IdEmpleadoCierre = cierre is null ? null : ctx.IdAdmin,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();
        return turno.Id;
    }

    private async Task<int> SembrarMedioAsync(Contexto ctx, string nombre, ComportamientoMedioPago comportamiento)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 70, Comportamiento = comportamiento, AdmiteVuelto = false,
            RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre, string apellido)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre, Apellido = apellido,
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            IdListaPrecio = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync(),
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return cliente.Id;
    }

    private sealed record VentaASembrar(
        int IdTipo, int IdPuntoVenta, int? IdTurno, int IdEmpleado, int IdCliente, string Fecha, decimal Total,
        EstadoComprobante Estado = EstadoComprobante.Emitido, int? IdAsociado = null, bool Eliminada = false);

    private async Task<int> SembrarVentaAsync(
        Contexto ctx, VentaASembrar venta, params (int IdMedio, decimal Importe, bool Eliminado)[] cobros)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = venta.IdTipo, Numero = Interlocked.Increment(ref _numero),
            Fecha = Dia(venta.Fecha), IdPuntoVenta = venta.IdPuntoVenta, IdTurnoCaja = venta.IdTurno, IdEmpleado = venta.IdEmpleado,
            IdCliente = venta.IdCliente, IdComprobanteAsociado = venta.IdAsociado, Subtotal = venta.Total, DescuentoTotal = 0m,
            Total = venta.Total, Estado = venta.Estado, DeletedAt = venta.Eliminada ? ahora : null, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        foreach (var (idMedio, importe, eliminado) in cobros)
        {
            db.Cobros.Add(new Cobro
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedio, Importe = importe,
                Vuelto = 0m, DeletedAt = eliminado ? ahora : null, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    private async Task SembrarEspejoAsync(Contexto ctx, int idVenta, ResultadoFiscal resultado)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var aprobado = resultado is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones;
        db.ComprobantesVenta.Add(new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = await db.TiposComprobante.Where(t => t.Codigo == "FB").Select(t => t.Id).FirstAsync(),
            Numero = Interlocked.Increment(ref _numero), Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleado = ctx.IdAdmin,
            IdCliente = await db.Clientes.OrderBy(c => c.Numero).Select(c => c.Id).FirstAsync(), Subtotal = 100m, Total = 100m,
            Estado = EstadoComprobante.Emitido, IdVentaOrigen = idVenta, ResultadoFiscal = resultado,
            Cae = aprobado ? "12345678901234" : null, CaeVencimiento = aprobado ? new DateOnly(2027, 5, 20) : null,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<Escenario> SembrarAsync()
    {
        var ctx = await PrepararAsync(fixture, "listado-ventas");
        var dos = await ctx.Admin.PostAsJsonAsync("/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "Caja Dos", ModoPuntoVenta.Web));
        Assert.True(dos.StatusCode == HttpStatusCode.Created, await dos.Content.ReadAsStringAsync());
        var pv2 = JsonSerializer.Deserialize<PuntoVentaListado>(await dos.Content.ReadAsStringAsync(), OpcionesJson)!.Id;
        var (_, idVendedor) = await CrearUsuarioAsync(fixture, ctx, RolConocido.Vendedor, "vendedor");

        var idZeta = await SembrarMedioAsync(ctx, "Zeta Pay", ComportamientoMedioPago.Electronico);
        var idAlfa = await SembrarMedioAsync(ctx, "Alfa Pay", ComportamientoMedioPago.Electronico);
        var idCuentaCorriente = await SembrarMedioAsync(ctx, "Cuenta corriente X", ComportamientoMedioPago.CuentaCorriente);
        var jose = await SembrarClienteAsync(ctx, "José", "Pérez");
        var ana = await SembrarClienteAsync(ctx, "Ana", "Gómez");

        int tx, ncx, consumidorFinal;
        await using (var db = Db(ctx.IdTenant))
        {
            tx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
            ncx = await db.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).FirstAsync();
            consumidorFinal = await db.Clientes.OrderBy(c => c.Numero).Select(c => c.Id).FirstAsync();
        }

        var turnoA = await SembrarTurnoAsync(ctx, ctx.IdPuntoVenta, "2026-09-30", "2026-10-02");
        var turnoC = await SembrarTurnoAsync(ctx, ctx.IdPuntoVenta, "2026-10-08", "2026-10-14");
        var turnoB = await SembrarTurnoAsync(ctx, pv2, "2026-10-18", null);
        await using (var db = Db(ctx.IdTenant))
        {
            db.ConciliacionesTesoreria.Add(new ConciliacionTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Hasta = Dia("2026-10-03"), IdUsuario = ctx.IdAdmin,
                CreadoEl = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var pv1 = ctx.IdPuntoVenta;
        var admin = ctx.IdAdmin;
        var ef = ctx.IdEfectivo;
        var a = await SembrarVentaAsync(ctx, new(tx, pv1, turnoA, admin, jose, "2026-10-01", 100m), (ef, 100m, false));
        var b = await SembrarVentaAsync(
            ctx, new(tx, pv1, turnoC, idVendedor, ana, "2026-10-10", 250m),
            (ef, 100m, false), (idZeta, 150m, false), (idAlfa, 250m, true));
        var c = await SembrarVentaAsync(
            ctx, new(tx, pv2, turnoB, idVendedor, consumidorFinal, "2026-10-20", 100m), (idAlfa, 40m, false), (ef, 60m, false));
        var d = await SembrarVentaAsync(ctx, new(tx, pv1, null, admin, jose, "2026-09-15", 500m), (idZeta, 500m, false));
        var e = await SembrarVentaAsync(
            ctx, new(tx, pv2, null, admin, ana, "2026-12-01", 30m, EstadoComprobante.Anulado), (ef, 30m, false));
        var f = await SembrarVentaAsync(ctx, new(tx, pv1, turnoC, admin, consumidorFinal, "2026-10-11", 70m), (ef, 70m, false));
        var g = await SembrarVentaAsync(ctx, new(tx, pv1, turnoC, idVendedor, jose, "2026-10-12", 80m), (ef, 80m, false));
        var h = await SembrarVentaAsync(
            ctx, new(tx, pv2, turnoB, idVendedor, ana, "2026-10-21", 90m), (idCuentaCorriente, 90m, false));
        var i = await SembrarVentaAsync(ctx, new(tx, pv1, turnoA, admin, consumidorFinal, "2026-10-04", 20m), (ef, 20m, false));
        var j = await SembrarVentaAsync(
            ctx, new(ncx, pv1, null, admin, jose, "2026-09-20", 15m, IdAsociado: a), (idZeta, 15m, false));
        var k = await SembrarVentaAsync(ctx, new(tx, pv1, turnoC, idVendedor, ana, "2026-10-13", 10m));
        var eliminada = await SembrarVentaAsync(
            ctx, new(tx, pv1, null, admin, jose, "2026-10-05", 999m, Eliminada: true), (ef, 999m, false));
        await SembrarEspejoAsync(ctx, f, ResultadoFiscal.Aprobado);
        await SembrarEspejoAsync(ctx, g, ResultadoFiscal.Pendiente);

        var otro = await PrepararAsync(fixture, "listado-ventas-otro");
        int txOtro, clienteOtro;
        await using (var db = Db(otro.IdTenant))
        {
            txOtro = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
            clienteOtro = await db.Clientes.OrderBy(cl => cl.Numero).Select(cl => cl.Id).FirstAsync();
        }

        var deOtroTenant = await SembrarVentaAsync(
            otro, new(txOtro, otro.IdPuntoVenta, null, otro.IdAdmin, clienteOtro, "2026-10-06", 55m), (otro.IdEfectivo, 55m, false));

        long numeroDeB;
        await using (var db = Db(ctx.IdTenant))
        {
            numeroDeB = await db.ComprobantesVenta.Where(v => v.Id == b).Select(v => v.Numero).SingleAsync();
        }

        return new Escenario(
            ctx, pv2, turnoA, ncx, idVendedor, idZeta, idAlfa, numeroDeB,
            new Dictionary<string, int>
            {
                ["a"] = a, ["b"] = b, ["c"] = c, ["d"] = d, ["e"] = e, ["f"] = f, ["g"] = g, ["h"] = h, ["i"] = i, ["j"] = j,
                ["k"] = k
            },
            new HashSet<int> { eliminada, deOtroTenant });
    }

    private static async Task<PaginaDeVentasDeAdministracion> ListarAsync(Escenario e, string consulta, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? e.Ctx.Admin).GetAsync($"/api/ventas/administracion?{consulta}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDeVentasDeAdministracion>(cuerpo, OpcionesJson)!;
    }

    private static string Nombres(Escenario e, PaginaDeVentasDeAdministracion pagina) =>
        string.Join(",", pagina.Items.Select(f => e.Ids.Single(p => p.Value == f.Id).Key));

    private static FilaDeVentaDeAdministracion Fila(Escenario e, PaginaDeVentasDeAdministracion pagina, string nombre) =>
        pagina.Items.Single(f => f.Id == e.Ids[nombre]);

    [Fact]
    public async Task SinFiltrosListaLasVentasVivasDelTenantSinEspejosYNadaMas()
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, "tamanio=200");

        Assert.Equal(11, pagina.Total);
        Assert.Equal("e,h,c,k,g,f,b,i,a,j,d", Nombres(e, pagina));
        Assert.DoesNotContain(pagina.Items, f => e.Excluidas.Contains(f.Id));

        var a = Fila(e, pagina, "a");
        Assert.Equal(
            ("TX", e.Ctx.IdPuntoVenta, "Local 1", e.IdTurnoA, true, "José Pérez", e.Ctx.IdAdmin, 100m, EstadoComprobante.Emitido,
                "sin_factura", "Efectivo", true),
            (a.CodigoTipo, a.IdPuntoVenta, a.PuntoVenta, a.IdTurnoCaja, a.TurnoCerrado, a.Cliente, a.IdEmpleado, a.Total, a.Estado,
                a.EstadoFiscal, a.Medios, a.Conciliado));
        Assert.Equal($"{e.Ctx.IdPuntoVenta:D4}-{a.Numero:D8}", a.NumeroVisible);
        Assert.Equal(Dia("2026-10-01"), a.Fecha);

        var b = Fila(e, pagina, "b");
        Assert.Equal("Efectivo, Zeta Pay", b.Medios);
        Assert.Equal((e.IdVendedor, "Ana Gómez", false, true), (b.IdEmpleado, b.Cliente, b.Conciliado, b.TurnoCerrado));
        Assert.Null(Fila(e, pagina, "k").Medios);
        Assert.Equal("Alfa Pay, Efectivo", Fila(e, pagina, "c").Medios);
        Assert.Equal((e.IdPuntoVentaDos, "Caja Dos", false), (Fila(e, pagina, "c").IdPuntoVenta, Fila(e, pagina, "c").PuntoVenta,
            Fila(e, pagina, "c").TurnoCerrado));
        Assert.Null(Fila(e, pagina, "d").TurnoCerrado);
        Assert.Equal("con_cae", Fila(e, pagina, "f").EstadoFiscal);
        Assert.Equal("en_tramite", Fila(e, pagina, "g").EstadoFiscal);
    }

    [Theory]
    [InlineData("desde=2026-10-15T00:00:00Z", "e,h,c")]
    [InlineData("hasta=2026-10-11T23:59:59Z", "f,b,i,a,j,d")]
    [InlineData("numero={numeroB}", "b")]
    [InlineData("idTipoComprobante={ncx}", "j")]
    [InlineData("idPuntoVenta={pv2}", "e,h,c")]
    [InlineData("idTurnoCaja={turnoA}", "i,a")]
    [InlineData("cliente=JOSE", "g,a,j,d")]
    [InlineData("cliente=gomez", "e,h,k,b")]
    [InlineData("idEmpleado={vendedor}", "h,c,k,g,b")]
    [InlineData("idMedioPago={zeta}", "b,j,d")]
    [InlineData("idMedioPago={alfa}", "c")]
    [InlineData("totalDesde=250", "b,d")]
    [InlineData("totalHasta=30", "e,k,i,j")]
    [InlineData("estado=Anulado", "e")]
    [InlineData("estadoFiscal=con_cae", "f")]
    [InlineData("estadoFiscal=en_tramite", "g")]
    [InlineData("estadoFiscal=sin_factura", "e,h,c,k,b,i,a,j,d")]
    [InlineData("turnoCerrado=true", "k,g,f,b,i,a")]
    [InlineData("turnoCerrado=false", "h,c")]
    [InlineData("conciliado=true", "i,a,j,d")]
    [InlineData("conciliado=false", "e,h,c,k,g,f,b")]
    public async Task CadaFiltroDejaSoloSusVentas(string filtro, string esperados)
    {
        var e = await EscenarioAsync();
        var consulta = filtro
            .Replace("{numeroB}", e.NumeroDeB.ToString())
            .Replace("{ncx}", e.IdTipoNcx.ToString())
            .Replace("{pv2}", e.IdPuntoVentaDos.ToString())
            .Replace("{turnoA}", e.IdTurnoA.ToString())
            .Replace("{vendedor}", e.IdVendedor.ToString())
            .Replace("{zeta}", e.IdZeta.ToString())
            .Replace("{alfa}", e.IdAlfa.ToString());

        var pagina = await ListarAsync(e, consulta);

        Assert.Equal(esperados, Nombres(e, pagina));
        Assert.Equal(pagina.Items.Count, pagina.Total);
    }

    private static readonly IReadOnlyDictionary<string, Func<FilaDeVentaDeAdministracion, IComparable?>> Claves =
        new Dictionary<string, Func<FilaDeVentaDeAdministracion, IComparable?>>
        {
            ["fecha"] = f => f.Fecha,
            ["numero"] = f => f.NumeroVisible,
            ["tipo"] = f => f.Tipo,
            ["puntoVenta"] = f => f.PuntoVenta,
            ["turno"] = f => f.IdTurnoCaja,
            ["cliente"] = f => f.Cliente,
            ["empleado"] = f => f.Empleado,
            ["medios"] = f => f.Medios,
            ["total"] = f => f.Total,
            ["estado"] = f => f.Estado.ToString(),
            ["estadoFiscal"] = f => f.EstadoFiscal switch { "sin_factura" => 0, "en_tramite" => 1, _ => 2 },
            ["estadoTurno"] = f => f.TurnoCerrado,
            ["conciliado"] = f => f.Conciliado,
        };

    private static int Comparar(IComparable? x, IComparable? y) =>
        (x, y) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (string sx, string sy) => string.CompareOrdinal(sx, sy),
            _ => x.CompareTo(y),
        };

    /// <summary>Oráculo: ordena en memoria las once filas por la clave de la columna (los vacíos siempre al final), con el id
    /// ascendente como desempate. Los textos del escenario ordenan igual en la base y en ordinal.</summary>
    private static string Esperado(Escenario e, PaginaDeVentasDeAdministracion todas, string orden, bool descendente)
    {
        var clave = Claves[orden];
        var filas = todas.Items.ToList();
        filas.Sort((x, y) =>
        {
            var kx = clave(x);
            var ky = clave(y);
            var c = kx is null || ky is null ? Comparar(kx, ky) : descendente ? Comparar(ky, kx) : Comparar(kx, ky);
            return c != 0 ? c : x.Id.CompareTo(y.Id);
        });
        return string.Join(",", filas.Select(f => e.Ids.Single(p => p.Value == f.Id).Key));
    }

    [Theory]
    [InlineData("fecha")]
    [InlineData("numero")]
    [InlineData("tipo")]
    [InlineData("puntoVenta")]
    [InlineData("turno")]
    [InlineData("cliente")]
    [InlineData("empleado")]
    [InlineData("medios")]
    [InlineData("total")]
    [InlineData("estado")]
    [InlineData("estadoFiscal")]
    [InlineData("estadoTurno")]
    [InlineData("conciliado")]
    public async Task CadaColumnaOrdenaTodoElConjuntoEnLasDosDirecciones(string orden)
    {
        var e = await EscenarioAsync();
        var todas = await ListarAsync(e, "tamanio=200");
        var porId = string.Join(",", todas.Items.OrderBy(f => f.Id).Select(f => e.Ids.Single(p => p.Value == f.Id).Key));

        foreach (var (direccion, descendente) in new[] { ("ascendente", false), ("descendente", true) })
        {
            var esperado = Esperado(e, todas, orden, descendente);
            Assert.NotEqual(porId, esperado);

            var pagina = await ListarAsync(e, $"orden={orden}&direccion={direccion}&tamanio=200");

            Assert.Equal(esperado, Nombres(e, pagina));
        }
    }

    [Fact]
    public async Task ElOrdenSinDireccionEsAscendente()
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, "orden=total&tamanio=200");

        Assert.Equal("k,j,i,e,f,g,h,a,c,b,d", Nombres(e, pagina));
    }

    [Fact]
    public async Task ElOrdenSeAplicaAntesDePaginar()
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, "orden=total&direccion=descendente&pagina=2&tamanio=3");

        Assert.Equal((11, 2, 3), (pagina.Total, pagina.Pagina, pagina.Tamanio));
        Assert.Equal("c,h,g", Nombres(e, pagina));
    }

    [Fact]
    public async Task ElTamanioTieneTopeYLaPaginaPorDefectoEsLaPrimera()
    {
        var e = await EscenarioAsync();

        var porDefecto = await ListarAsync(e, "");
        var enorme = await ListarAsync(e, "tamanio=5000&pagina=0");

        Assert.Equal((25, 1), (porDefecto.Tamanio, porDefecto.Pagina));
        Assert.Equal((200, 1), (enorme.Tamanio, enorme.Pagina));
    }

    [Theory]
    [InlineData("orden=saldo", "orden_invalido")]
    [InlineData("orden=total&direccion=arriba", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    [InlineData("estadoFiscal=aprobada", "estado_fiscal_invalido")]
    public async Task UnOrdenOUnEstadoFiscalDesconocidoSeRechaza(string consulta, string codigo)
    {
        var e = await EscenarioAsync();

        var respuesta = await e.Ctx.Admin.GetAsync($"/api/ventas/administracion?{consulta}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        using var problema = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(codigo, problema.RootElement.GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData("a", false, "periodo_conciliado")]
    [InlineData("b", true, null)]
    [InlineData("c", true, null)]
    [InlineData("d", false, "periodo_conciliado")]
    [InlineData("e", false, "venta_anulada")]
    [InlineData("f", false, "venta_con_cae")]
    [InlineData("g", false, "venta_con_factura_en_tramite")]
    [InlineData("h", false, "cambio_de_medios_con_cuenta_corriente")]
    [InlineData("i", false, "periodo_conciliado")]
    [InlineData("j", false, "comprobante_sin_cambio_de_medios")]
    [InlineData("k", true, null)]
    public async Task ElMedioEsEditableSoloSiNingunaGuardaLoBloquea(string venta, bool editable, string? motivo)
    {
        var e = await EscenarioAsync();

        var fila = Fila(e, await ListarAsync(e, "tamanio=200"), venta);

        Assert.Equal((editable, motivo), (fila.MedioEditable, fila.MotivoMedioNoEditable));
    }

    /// <summary>La grilla no redeclara las guardas: el motivo que informa es el código con el que el endpoint de cambio de medios
    /// rechaza esa misma venta.</summary>
    [Theory]
    [InlineData("a")]
    [InlineData("d")]
    [InlineData("e")]
    [InlineData("f")]
    [InlineData("g")]
    [InlineData("h")]
    [InlineData("i")]
    [InlineData("j")]
    public async Task ElMotivoDeLaGrillaEsElCodigoConElQueLaGuardaRealRechazaElCambio(string venta)
    {
        var e = await EscenarioAsync();
        var fila = Fila(e, await ListarAsync(e, "tamanio=200"), venta);
        var nuevoMedio = fila.Medios == "Zeta Pay" ? e.Ctx.IdEfectivo : e.IdZeta;

        var respuesta = await e.Ctx.Admin.PutAsJsonAsync(
            $"/api/ventas/administracion/{fila.Id}/medios",
            new SolicitudDeCambioDeMedios([new PagoDeVenta(nuevoMedio, fila.Total, null, 0m)]));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        using var problema = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(fila.MotivoMedioNoEditable, problema.RootElement.GetProperty("codigo").GetString());
    }

    /// <summary>La venta <c>i</c> es de un turno cerrado el 10-02 y está fechada el 10-04: la marca (10-03) alcanza al cierre pero
    /// no a la venta. Cuenta la menor de las dos, como la guarda del cambio de medios.</summary>
    [Fact]
    public async Task LaConciliacionUsaLaMenorEntreElCierreYLaFechaDeLaVenta()
    {
        var e = await EscenarioAsync();

        var pagina = await ListarAsync(e, "tamanio=200");

        Assert.True(Fila(e, pagina, "i").Conciliado);
        Assert.False(Fila(e, pagina, "b").Conciliado);
        Assert.True(Fila(e, pagina, "d").Conciliado);
        Assert.False(Fila(e, pagina, "e").Conciliado);
    }

    [Fact]
    public async Task SoloSupervisorYAdminLeenLaGrillaYSoloElAdminCambiaLosMedios()
    {
        var e = await EscenarioAsync();
        var (supervisor, _) = await CrearUsuarioAsync(fixture, e.Ctx, RolConocido.Supervisor, "supervisor-ventas");
        var (vendedor, _) = await CrearUsuarioAsync(fixture, e.Ctx, RolConocido.Vendedor, "vendedor-ventas");

        Assert.Equal(11, (await ListarAsync(e, "tamanio=200", supervisor)).Total);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.GetAsync("/api/ventas/administracion")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await supervisor.PutAsJsonAsync(
                $"/api/ventas/administracion/{e.Ids["b"]}/medios",
                new SolicitudDeCambioDeMedios([new PagoDeVenta(e.IdZeta, 250m, null, 0m)]))).StatusCode);
        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/ventas/administracion")).StatusCode);
    }

    /// <summary>dangling-fk-read-models: un cliente, un empleado y un punto de venta dados de baja (sellados directo, como un dato
    /// heredado) se proyectan con id y nombre <c>null</c> y sus filtros dejan de encontrar la venta, que sigue en el listado con su
    /// número y con la conciliación de su empresa.</summary>
    [Fact]
    public async Task UnClienteUnEmpleadoYUnPuntoDeVentaDadosDeBajaSeMuestranVaciosYSusFiltrosNoLosEncuentran()
    {
        var ctx = await PrepararAsync(fixture, "listado-ventas-bajas");
        var (_, idVendedor) = await CrearUsuarioAsync(fixture, ctx, RolConocido.Vendedor, "vendedor");
        var ana = await SembrarClienteAsync(ctx, "Ana", "Gómez");
        int tx;
        await using (var db = Db(ctx.IdTenant))
        {
            tx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();
            db.ConciliacionesTesoreria.Add(new ConciliacionTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Hasta = Dia("2026-10-03"), IdUsuario = ctx.IdAdmin,
                CreadoEl = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var venta = await SembrarVentaAsync(
            ctx, new(tx, ctx.IdPuntoVenta, null, idVendedor, ana, "2026-10-01", 40m), (ctx.IdEfectivo, 40m, false));

        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await EjecutarAsync(owner, "UPDATE usuarios SET deleted_at = now() WHERE id_usuario = $1", idVendedor);
            await EjecutarAsync(owner, "UPDATE clientes SET deleted_at = now() WHERE id_cliente = $1", ana);
            await EjecutarAsync(owner, "UPDATE puntos_venta SET deleted_at = now() WHERE id_punto_venta = $1", ctx.IdPuntoVenta);
        }

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDeVentasDeAdministracion>("/api/ventas/administracion", OpcionesJson);
        var fila = Assert.Single(pagina!.Items);
        Assert.Equal(
            (venta, null, null, null, null, null, null, true, "periodo_conciliado"),
            (fila.Id, fila.IdPuntoVenta, fila.PuntoVenta, fila.IdCliente, fila.Cliente, fila.IdEmpleado, fila.Empleado, fila.Conciliado,
                fila.MotivoMedioNoEditable));
        Assert.Equal($"{ctx.IdPuntoVenta:D4}-{fila.Numero:D8}", fila.NumeroVisible);
        Assert.Equal("Efectivo", fila.Medios);

        foreach (var consulta in new[]
                 {
                     $"idPuntoVenta={ctx.IdPuntoVenta}", $"idEmpleado={idVendedor}", "cliente=gomez"
                 })
        {
            var filtrada = await ctx.Admin.GetFromJsonAsync<PaginaDeVentasDeAdministracion>(
                $"/api/ventas/administracion?{consulta}", OpcionesJson);
            Assert.Equal(0, filtrada!.Total);
        }
    }
}
