using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Caja;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary><c>GET /api/reportes/cajas?orden=&amp;direccion=</c> contra Postgres. Posiciones (1 a 5, en orden de alta):
/// <code>
/// pos pv        apertura cierre esperado declarado diferencia
///  1  Zeta      05-03    05-04  100      90        -10
///  2  Alfa      05-01    05-09  300      310       10
///  3  (baja)    05-02    05-05  50       50        0
///  4  (baja)    05-05    05-06  -        -         -        (sin arqueos: los tres totales son 0)
///  5  Zeta      05-04    05-03  200      400       200
/// </code>
/// Un turno cerrado exige fecha y empleado de cierre (CHECK de la tabla); un punto de venta dado de baja sigue ahí y
/// el filtro global lo deja invisible, igual que a una FK colgante.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class HistoricoDeCajasOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/reportes/cajas";

    private async Task<(EscenarioDeDocumentos E, int[] Ids)> SembrarAsync()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "hca");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var zeta = await db.PuntosVenta.SingleAsync(p => p.Id == e.PvA);
        zeta.Nombre = "Zeta";
        var baja = new PuntoVenta
        {
            IdTenant = e.IdTenant, IdEmpresa = zeta.IdEmpresa, Nombre = "ZZ-baja", Modo = zeta.Modo,
            DeletedAt = SembradoDeDocumentos.Ahora, CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
        };
        db.PuntosVenta.Add(baja);
        await db.SaveChangesAsync();

        var medio = await db.MediosPago.Where(m => m.IdTenant == e.IdTenant).OrderBy(m => m.Id).Select(m => m.Id).FirstAsync();

        (int Pv, DateTimeOffset Apertura, DateTimeOffset Cierre, (decimal Esperado, decimal Declarado, decimal Diferencia)? Arqueo)[] filas =
        [
            (zeta.Id, SembradoDeDocumentos.Dia(5, 3), SembradoDeDocumentos.Dia(5, 4), (100m, 90m, -10m)),
            (e.PvB, SembradoDeDocumentos.Dia(5, 1), SembradoDeDocumentos.Dia(5, 9), (300m, 310m, 10m)),
            (baja.Id, SembradoDeDocumentos.Dia(5, 2), SembradoDeDocumentos.Dia(5, 5), (50m, 50m, 0m)),
            (baja.Id, SembradoDeDocumentos.Dia(5, 5), SembradoDeDocumentos.Dia(5, 6), null),
            (zeta.Id, SembradoDeDocumentos.Dia(5, 4), SembradoDeDocumentos.Dia(5, 3), (200m, 400m, 200m)),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var turno = new TurnoCaja
            {
                IdTenant = e.IdTenant, IdPuntoVenta = f.Pv, IdEmpleadoApertura = e.IdEmpleado, IdEmpleadoCierre = e.IdEmpleado,
                FechaApertura = f.Apertura, FechaCierre = f.Cierre, Estado = EstadoTurno.Cerrado,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.TurnosCaja.Add(turno);
            await db.SaveChangesAsync();
            ids[i] = turno.Id;

            if (f.Arqueo is { } a)
            {
                db.ArqueosTurno.Add(new ArqueoTurno
                {
                    IdTenant = e.IdTenant, IdTurnoCaja = turno.Id, IdMedioPago = medio, ImporteEsperado = a.Esperado,
                    ImporteDeclarado = a.Declarado, Diferencia = a.Diferencia
                });
                await db.SaveChangesAsync();
            }
        }

        return (e, ids);
    }

    [Theory]
    [InlineData("turno", "ascendente", "1,2,3,4,5")]
    [InlineData("turno", "descendente", "5,4,3,2,1")]
    [InlineData("puntoVenta", "ascendente", "2,1,5,3,4")]
    [InlineData("puntoVenta", "descendente", "1,5,2,3,4")]
    [InlineData("apertura", "ascendente", "2,3,1,5,4")]
    [InlineData("apertura", "descendente", "4,5,1,3,2")]
    [InlineData("cierre", "ascendente", "5,1,3,4,2")]
    [InlineData("cierre", "descendente", "2,4,3,1,5")]
    [InlineData("esperado", "ascendente", "4,3,1,5,2")]
    [InlineData("esperado", "descendente", "2,5,1,3,4")]
    [InlineData("declarado", "ascendente", "4,3,1,2,5")]
    [InlineData("declarado", "descendente", "5,2,1,3,4")]
    [InlineData("diferencia", "ascendente", "1,3,4,2,5")]
    [InlineData("diferencia", "descendente", "5,2,3,4,1")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (e, ids) = await SembrarAsync();

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"orden={orden}&direccion={direccion}", propiedadDelId: "idTurnoCaja");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorCierreDescendenteYUnFiltroConviveConElOrden()
    {
        var (e, ids) = await SembrarAsync();

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "2,4,3,1,5"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "", propiedadDelId: "idTurnoCaja"));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "1,5"),
            await SembradoDeDocumentos.RecorrerAsync(
                e.Admin, Ruta, $"idPuntoVenta={e.PvA}&orden=turno&direccion=ascendente", propiedadDelId: "idTurnoCaja"));
    }

    [Theory]
    [InlineData("orden=estado", "orden_invalido")]
    [InlineData("orden=turno&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosDan400(string query, string codigo)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "hca");

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, Ruta, query, codigo);
    }
}

/// <summary><c>GET /api/caja/turnos?orden=&amp;direccion=</c> contra Postgres. Posiciones (1 a 5, en orden de alta):
/// <code>
/// pos pv  estado  apertura cierre
///  1  A   cerrado 05-03    05-04
///  2  A   abierto 05-01    -
///  3  B   cerrado 05-05    05-06
///  4  B   abierto 05-02    -
///  5  A   cerrado 05-04    05-02
/// </code>
/// Hay a lo sumo un turno abierto por punto de venta (índice único parcial): por eso el 2 y el 4 viven en puntos de
/// venta distintos.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TurnosOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/caja/turnos";

    private async Task<(EscenarioDeDocumentos E, int[] Ids)> SembrarAsync()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "trn");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        (int Pv, DateTimeOffset Apertura, DateTimeOffset? Cierre)[] filas =
        [
            (e.PvA, SembradoDeDocumentos.Dia(5, 3), SembradoDeDocumentos.Dia(5, 4)),
            (e.PvA, SembradoDeDocumentos.Dia(5, 1), null),
            (e.PvB, SembradoDeDocumentos.Dia(5, 5), SembradoDeDocumentos.Dia(5, 6)),
            (e.PvB, SembradoDeDocumentos.Dia(5, 2), null),
            (e.PvA, SembradoDeDocumentos.Dia(5, 4), SembradoDeDocumentos.Dia(5, 2)),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var turno = new TurnoCaja
            {
                IdTenant = e.IdTenant, IdPuntoVenta = f.Pv, IdEmpleadoApertura = e.IdEmpleado, FechaApertura = f.Apertura,
                FechaCierre = f.Cierre, IdEmpleadoCierre = f.Cierre is null ? null : e.IdEmpleado,
                Estado = f.Cierre is null ? EstadoTurno.Abierto : EstadoTurno.Cerrado,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.TurnosCaja.Add(turno);
            await db.SaveChangesAsync();
            ids[i] = turno.Id;
        }

        return (e, ids);
    }

    [Theory]
    [InlineData("turno", "ascendente", "1,2,3,4,5")]
    [InlineData("turno", "descendente", "5,4,3,2,1")]
    [InlineData("apertura", "ascendente", "2,4,1,5,3")]
    [InlineData("apertura", "descendente", "3,5,1,4,2")]
    [InlineData("cierre", "ascendente", "5,1,3,2,4")]
    [InlineData("cierre", "descendente", "3,1,5,2,4")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (e, ids) = await SembrarAsync();

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"orden={orden}&direccion={direccion}", propiedadDelId: "id");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorAperturaDescendenteYElEstadoConviveConElOrden()
    {
        var (e, ids) = await SembrarAsync();

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "3,5,1,4,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "", propiedadDelId: "id"));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "5,1,3"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "estado=Cerrado&orden=cierre&direccion=ascendente", propiedadDelId: "id"));
    }

    [Theory]
    [InlineData("orden=estado", "orden_invalido")]
    [InlineData("orden=turno&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosDan400(string query, string codigo)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "trn");

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, Ruta, query, codigo);
    }
}

/// <summary><c>GET /api/proveedores/{id}/cuenta-corriente?orden=&amp;direccion=</c> contra Postgres. Posiciones (1 a 6,
/// en orden de alta); cA &lt; cB &lt; cC son compras borrador reales (la FK las exige):
/// <code>
/// pos fecha tipo               compra importe saldo detalle
///  1  05-03 Compra             cB      100     100   b
///  2  05-01 Apertura           -       0       0     -
///  3  05-05 Compra             cA      -40     60    a
///  4  05-02 Ajuste (contramov) cC      -100    -40   c
///  5  05-04 Ajuste (manual)    -       25      -15   -
///  6  05-06 Compra             cC      50      35    d
/// </code>
/// El saldo es el guardado de cada fila, sin relación con los importes: ordenar nunca lo recalcula. La apertura no
/// lleva punto de venta ni empleado y el resto sí (CHECK de la tabla). Las filas de pago, que referencian un gasto, las
/// cubre el test de servicio.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteDeProveedorOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(EscenarioDeDocumentos E, int IdProveedor, int[] Ids)> SembrarAsync()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "ccp");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var proveedor = SembradoDeDocumentos.Proveedor(e, "Proveedor del libro");
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var compras = new int[3];
        for (var i = 0; i < compras.Length; i++)
        {
            var compra = new ComprobanteCompra
            {
                IdTenant = e.IdTenant, IdPuntoVenta = e.PvA, IdProveedor = proveedor.Id, IdTipoComprobante = e.TiposCompra[0],
                IdEmpleado = e.IdEmpleado, Estado = EstadoCompra.Borrador, Total = 10m, Subtotal = 10m,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.ComprobantesCompra.Add(compra);
            await db.SaveChangesAsync();
            compras[i] = compra.Id;
        }

        var (cA, cB, cC) = (compras[0], compras[1], compras[2]);
        (DateTimeOffset Fecha, TipoMovimientoCcProveedor Tipo, int? Compra, decimal Importe, decimal Saldo, string? Detalle)[] filas =
        [
            (SembradoDeDocumentos.Dia(5, 3), TipoMovimientoCcProveedor.Compra, cB, 100m, 100m, "b"),
            (SembradoDeDocumentos.Dia(5, 1), TipoMovimientoCcProveedor.Apertura, null, 0m, 0m, null),
            (SembradoDeDocumentos.Dia(5, 5), TipoMovimientoCcProveedor.Compra, cA, -40m, 60m, "a"),
            (SembradoDeDocumentos.Dia(5, 2), TipoMovimientoCcProveedor.Ajuste, cC, -100m, -40m, "c"),
            (SembradoDeDocumentos.Dia(5, 4), TipoMovimientoCcProveedor.Ajuste, null, 25m, -15m, null),
            (SembradoDeDocumentos.Dia(5, 6), TipoMovimientoCcProveedor.Compra, cC, 50m, 35m, "d"),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var esApertura = f.Tipo == TipoMovimientoCcProveedor.Apertura;
            var movimiento = new MovimientoCuentaCorrienteProveedor
            {
                IdTenant = e.IdTenant, IdProveedor = proveedor.Id, Fecha = f.Fecha, Tipo = f.Tipo,
                IdPuntoVenta = esApertura ? null : e.PvA, IdEmpleado = esApertura ? null : e.IdEmpleado,
                IdComprobanteCompra = f.Compra, Importe = f.Importe, SaldoResultante = f.Saldo, Detalle = f.Detalle
            };
            db.MovimientosCuentaCorrienteProveedor.Add(movimiento);
            await db.SaveChangesAsync();
            ids[i] = movimiento.Id;
        }

        return (e, proveedor.Id, ids);
    }

    [Theory]
    [InlineData("fecha", "ascendente", "2,4,1,5,3,6")]
    [InlineData("fecha", "descendente", "6,3,5,1,4,2")]
    [InlineData("tipo", "ascendente", "5,2,1,3,6,4")]
    [InlineData("tipo", "descendente", "4,1,3,6,2,5")]
    [InlineData("referencia", "ascendente", "3,1,4,6,2,5")]
    [InlineData("referencia", "descendente", "4,6,1,3,2,5")]
    [InlineData("detalle", "ascendente", "3,1,4,6,2,5")]
    [InlineData("detalle", "descendente", "6,4,1,3,2,5")]
    [InlineData("importe", "ascendente", "4,3,2,5,6,1")]
    [InlineData("importe", "descendente", "1,6,5,2,3,4")]
    [InlineData("saldoResultante", "ascendente", "4,5,2,6,3,1")]
    [InlineData("saldoResultante", "descendente", "1,3,6,2,5,4")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (e, idProveedor, ids) = await SembrarAsync();

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(
            e.Admin, $"/api/proveedores/{idProveedor}/cuenta-corriente", $"historico=true&orden={orden}&direccion={direccion}", propiedadDelId: "idMovimiento");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElLibroSigueSiendoPorFechaDescendenteYElOrdenConservaElSaldoGuardadoDeCadaFila()
    {
        var (e, idProveedor, ids) = await SembrarAsync();
        var ruta = $"/api/proveedores/{idProveedor}/cuenta-corriente";

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "6,3,5,1,4,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, ruta, "historico=true", propiedadDelId: "idMovimiento"));

        var respuesta = await e.Admin.GetAsync($"{ruta}?historico=true&orden=importe&direccion=ascendente&pagina=1&tamanio=10");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(
            [-40m, 60m, 0m, -15m, 35m, 100m],
            json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("saldoResultante").GetDecimal()).ToList());
    }

    [Theory]
    [InlineData("orden=estado", "orden_invalido")]
    [InlineData("orden=fecha&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosDan400(string query, string codigo)
    {
        var (e, idProveedor, _) = await SembrarAsync();

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, $"/api/proveedores/{idProveedor}/cuenta-corriente", query, codigo);
    }
}

/// <summary><c>GET /api/personal/{id}/cuenta?orden=&amp;direccion=</c> contra Postgres. Posiciones (1 a 6, en orden de
/// alta):
/// <code>
/// pos fecha tipo   horas valor importe saldo detalle
///  1  05-03 Horas  8     100   800     800   b
///  2  05-01 Ajuste -     -     -50     750   ajuste
///  3  05-05 Pago   -     -     -300    450   -
///  4  05-02 Horas  4     150   600     1050  a
///  5  05-04 Horas  8     80    640     1690  -
///  6  05-06 Pago   -     -     -100    100   z
/// </code>
/// El importe de una carga de horas es <c>round(horas * valor, 2)</c> (CHECK de la tabla); un pago es negativo y un
/// ajuste no es cero. El saldo es el guardado de cada fila: ordenar nunca lo recalcula.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaDePersonalOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private async Task<(EscenarioDeDocumentos E, int IdPersonal, int[] Ids)> SembrarAsync()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "cdp");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var personal = new Personal
        {
            IdTenant = e.IdTenant, Nombre = "Ana", ValorHora = 100m, CreatedAt = SembradoDeDocumentos.Ahora,
            UpdatedAt = SembradoDeDocumentos.Ahora
        };
        db.Personal.Add(personal);
        await db.SaveChangesAsync();

        (DateOnly Fecha, TipoMovimientoPersonal Tipo, decimal? Horas, decimal? ValorHora, decimal Importe, decimal Saldo, string? Detalle)[] filas =
        [
            (new DateOnly(2026, 5, 3), TipoMovimientoPersonal.Horas, 8m, 100m, 800m, 800m, "b"),
            (new DateOnly(2026, 5, 1), TipoMovimientoPersonal.Ajuste, null, null, -50m, 750m, "ajuste"),
            (new DateOnly(2026, 5, 5), TipoMovimientoPersonal.Pago, null, null, -300m, 450m, null),
            (new DateOnly(2026, 5, 2), TipoMovimientoPersonal.Horas, 4m, 150m, 600m, 1050m, "a"),
            (new DateOnly(2026, 5, 4), TipoMovimientoPersonal.Horas, 8m, 80m, 640m, 1690m, null),
            (new DateOnly(2026, 5, 6), TipoMovimientoPersonal.Pago, null, null, -100m, 100m, "z"),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var movimiento = new MovimientoPersonal
            {
                IdTenant = e.IdTenant, IdPersonal = personal.Id, IdEmpleado = e.IdEmpleado, Fecha = f.Fecha, Tipo = f.Tipo,
                Horas = f.Horas, ValorHora = f.ValorHora, Importe = f.Importe, SaldoResultante = f.Saldo, Detalle = f.Detalle,
                CreatedAt = SembradoDeDocumentos.Ahora
            };
            db.MovimientosPersonal.Add(movimiento);
            await db.SaveChangesAsync();
            ids[i] = movimiento.Id;
        }

        return (e, personal.Id, ids);
    }

    [Theory]
    [InlineData("fecha", "ascendente", "2,4,1,5,3,6")]
    [InlineData("fecha", "descendente", "6,3,5,1,4,2")]
    [InlineData("tipo", "ascendente", "2,1,4,5,3,6")]
    [InlineData("tipo", "descendente", "3,6,1,4,5,2")]
    [InlineData("horas", "ascendente", "4,1,5,2,3,6")]
    [InlineData("horas", "descendente", "1,5,4,2,3,6")]
    [InlineData("valorHora", "ascendente", "5,1,4,2,3,6")]
    [InlineData("valorHora", "descendente", "4,1,5,2,3,6")]
    [InlineData("importe", "ascendente", "3,6,2,4,5,1")]
    [InlineData("importe", "descendente", "1,5,4,2,6,3")]
    [InlineData("saldoResultante", "ascendente", "6,3,2,1,4,5")]
    [InlineData("saldoResultante", "descendente", "5,4,1,2,3,6")]
    [InlineData("detalle", "ascendente", "4,2,1,6,3,5")]
    [InlineData("detalle", "descendente", "6,1,2,4,3,5")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (e, idPersonal, ids) = await SembrarAsync();

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(
            e.Admin, $"/api/personal/{idPersonal}/cuenta", $"historico=true&orden={orden}&direccion={direccion}", propiedadDelId: "idMovimiento");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElLibroSigueSiendoPorFechaDescendente()
    {
        var (e, idPersonal, ids) = await SembrarAsync();

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "6,3,5,1,4,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, $"/api/personal/{idPersonal}/cuenta", "historico=true", propiedadDelId: "idMovimiento"));
    }

    [Theory]
    [InlineData("orden=estado", "orden_invalido")]
    [InlineData("orden=fecha&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosDan400(string query, string codigo)
    {
        var (e, idPersonal, _) = await SembrarAsync();

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, $"/api/personal/{idPersonal}/cuenta", query, codigo);
    }
}
