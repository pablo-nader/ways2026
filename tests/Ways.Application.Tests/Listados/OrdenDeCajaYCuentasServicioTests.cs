using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Listados;
using Ways.Application.Plantel;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Plantel;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Application.Tests.Listados;

/// <summary>Orden en el servidor del histórico de cajas, los cierres del POS y los libros de proveedor y de personal,
/// sobre InMemory: cada caso recorre las páginas de a dos y compara la secuencia completa (el orden se aplica antes
/// de paginar), con vacíos al final en las dos direcciones. Las posiciones (1 a 5 o 6) son el orden de alta, es decir
/// de id. Lo que InMemory NO prueba —traducción a SQL y colación— lo cubren los tests de integración.</summary>
public class OrdenDeHistoricoDeCajasServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeHistoricoDeCajas Servicio, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var zeta = Pv("Zeta");
            var alfa = Pv("Alfa");
            var baja = Pv("ZZ-baja");
            baja.DeletedAt = Ahora;
            siembra.AddRange(zeta, alfa, baja);
            await siembra.SaveChangesAsync();

            // Turno 4 sin arqueos: sus tres totales son 0, como la celda. El 1 y el 2 tienen dos arqueos cada uno.
            (int Pv, DateTimeOffset Apertura, DateTimeOffset Cierre, (decimal Esperado, decimal Declarado, decimal Diferencia)[] Arqueos)[] filas =
            [
                (zeta.Id, Dia(5, 3), Dia(5, 4), [(60m, 50m, -10m), (40m, 40m, 0m)]),
                (alfa.Id, Dia(5, 1), Dia(5, 9), [(300m, 310m, 10m)]),
                (baja.Id, Dia(5, 2), Dia(5, 5), [(50m, 50m, 0m)]),
                (IdColgante, Dia(5, 5), Dia(5, 6), []),
                (zeta.Id, Dia(5, 4), Dia(5, 3), [(200m, 400m, 200m)]),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var turno = new TurnoCaja
                {
                    IdTenant = IdTenant, IdPuntoVenta = f.Pv, IdEmpleadoApertura = 1, IdEmpleadoCierre = 1,
                    FechaApertura = f.Apertura, FechaCierre = f.Cierre, Estado = EstadoTurno.Cerrado,
                    CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.TurnosCaja.Add(turno);
                await siembra.SaveChangesAsync();
                ids[i] = turno.Id;

                var medio = 1;
                foreach (var a in f.Arqueos)
                {
                    siembra.ArqueosTurno.Add(new ArqueoTurno
                    {
                        IdTenant = IdTenant, IdTurnoCaja = turno.Id, IdMedioPago = medio++,
                        ImporteEsperado = a.Esperado, ImporteDeclarado = a.Declarado, Diferencia = a.Diferencia
                    });
                }

                await siembra.SaveChangesAsync();
            }
        }

        return (new ServicioDeHistoricoDeCajas(ContextoDelTenant(baseDeDatos)), ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDeHistoricoDeCajas servicio, string? orden, string? direccion)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 5; pagina++)
        {
            var p = await servicio.ListarCierresAsync(pagina: pagina, tamanio: 2, orden: orden, direccion: direccion);
            ids.AddRange(p.Items.Select(i => i.IdTurnoCaja));
            if (pagina * 2 >= p.Total) break;
        }

        return ids;
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
        var (servicio, ids) = await PrepararAsync($"historico-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorCierreDescendente()
    {
        var (servicio, ids) = await PrepararAsync("historico-defecto");

        Assert.Equal(PorPosicion(ids, "2,4,3,1,5"), await RecorrerAsync(servicio, null, null));
    }

    [Fact]
    public async Task OrdenarNoCambiaLosTotalesDeCadaFila()
    {
        var (servicio, ids) = await PrepararAsync("historico-totales");

        var pagina = await servicio.ListarCierresAsync(tamanio: 10, orden: "esperado", direccion: "descendente");

        Assert.Equal(
            [(ids[1], 300m, 310m, 10m), (ids[4], 200m, 400m, 200m), (ids[0], 100m, 90m, -10m), (ids[2], 50m, 50m, 0m), (ids[3], 0m, 0m, 0m)],
            pagina.Items.Select(i => (i.IdTurnoCaja, i.Esperado, i.Declarado, i.Diferencia)).ToList());
    }

    [Theory]
    [InlineData("estado", null, "orden_invalido")]
    [InlineData("turno", "sube", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"historico-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarCierresAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["turno", "puntoVenta", "apertura", "cierre", "esperado", "declarado", "diferencia"],
            ServicioDeHistoricoDeCajas.ClavesDeOrden.Keys.ToArray());
    }
}

public class OrdenDeTurnosServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeTurnos Servicio, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[5];
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            // Dos turnos abiertos (sin cierre) en puntos de venta distintos.
            (int Pv, DateTimeOffset Apertura, DateTimeOffset? Cierre)[] filas =
            [
                (1, Dia(5, 3), Dia(5, 4)),
                (2, Dia(5, 1), null),
                (1, Dia(5, 5), Dia(5, 6)),
                (3, Dia(5, 2), null),
                (1, Dia(5, 4), Dia(5, 2)),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var turno = new TurnoCaja
                {
                    IdTenant = IdTenant, IdPuntoVenta = f.Pv, IdEmpleadoApertura = 1, FechaApertura = f.Apertura,
                    FechaCierre = f.Cierre, IdEmpleadoCierre = f.Cierre is null ? null : 1,
                    Estado = f.Cierre is null ? EstadoTurno.Abierto : EstadoTurno.Cerrado, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.TurnosCaja.Add(turno);
                await siembra.SaveChangesAsync();
                ids[i] = turno.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        return (new ServicioDeTurnos(db, Reloj, Contexto, null!, null!, null!, null!), ids);
    }

    private static async Task<List<int>> RecorrerAsync(ServicioDeTurnos servicio, string? orden, string? direccion)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 5; pagina++)
        {
            var p = await servicio.ListarAsync(t => true, pagina: pagina, tamanio: 2, orden: orden, direccion: direccion);
            ids.AddRange(p.Items.Select(i => i.Id));
            if (pagina * 2 >= p.Total) break;
        }

        return ids;
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
        var (servicio, ids) = await PrepararAsync($"turnos-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorAperturaDescendente()
    {
        var (servicio, ids) = await PrepararAsync("turnos-defecto");

        Assert.Equal(PorPosicion(ids, "3,5,1,4,2"), await RecorrerAsync(servicio, null, null));
    }

    [Theory]
    [InlineData("estado", null, "orden_invalido")]
    [InlineData("turno", "sube", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"turnos-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(t => true, orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(["turno", "apertura", "cierre"], ServicioDeTurnos.ClavesDeOrden.Keys.ToArray());
    }
}

public class OrdenDeCuentaDeProveedorServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeCuentaCorrienteDeProveedor Servicio, int IdProveedor, int[] Ids)> PrepararAsync(
        string baseDeDatos)
    {
        var ids = new int[6];
        int idProveedor;
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var proveedor = Prov("P");
            siembra.Proveedores.Add(proveedor);
            await siembra.SaveChangesAsync();
            idProveedor = proveedor.Id;

            // El saldo resultante es el guardado de cada fila, a propósito sin relación con los importes: ordenar jamás
            // puede recalcularlo. Los movimientos 4 y 5 son ajustes: el 4 lleva compra (contramovimiento de una anulación),
            // el 5 es manual.
            (DateTimeOffset Fecha, TipoMovimientoCcProveedor Tipo, int? Compra, int? Pago, decimal Importe, decimal Saldo, string? Detalle)[] filas =
            [
                (Dia(5, 3), TipoMovimientoCcProveedor.Compra, 10, null, 100m, 100m, "b"),
                (Dia(5, 1), TipoMovimientoCcProveedor.Apertura, null, null, 0m, 0m, null),
                (Dia(5, 5), TipoMovimientoCcProveedor.Pago, null, 7, -40m, 60m, "a"),
                (Dia(5, 2), TipoMovimientoCcProveedor.Ajuste, 3, null, -100m, -40m, "c"),
                (Dia(5, 4), TipoMovimientoCcProveedor.Ajuste, null, null, 25m, -15m, null),
                (Dia(5, 6), TipoMovimientoCcProveedor.Compra, 9, null, 50m, 35m, "d"),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var movimiento = new MovimientoCuentaCorrienteProveedor
                {
                    IdTenant = IdTenant, IdProveedor = idProveedor, Fecha = f.Fecha, Tipo = f.Tipo,
                    IdComprobanteCompra = f.Compra, IdPago = f.Pago, Importe = f.Importe, SaldoResultante = f.Saldo,
                    Detalle = f.Detalle
                };
                siembra.MovimientosCuentaCorrienteProveedor.Add(movimiento);
                await siembra.SaveChangesAsync();
                ids[i] = movimiento.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        return (new ServicioDeCuentaCorrienteDeProveedor(db, Reloj, Contexto), idProveedor, ids);
    }

    private static async Task<List<int>> RecorrerAsync(
        ServicioDeCuentaCorrienteDeProveedor servicio, int idProveedor, string? orden, string? direccion)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 6; pagina++)
        {
            var p = await servicio.ObtenerEstadoDeCuentaAsync(
                idProveedor, null, null, historico: true, pagina, tamanio: 2, orden, direccion);
            ids.AddRange(p.Items.Select(i => i.IdMovimiento));
            if (pagina * 2 >= p.Total) break;
        }

        return ids;
    }

    [Theory]
    [InlineData("fecha", "ascendente", "2,4,1,5,3,6")]
    [InlineData("fecha", "descendente", "6,3,5,1,4,2")]
    [InlineData("tipo", "ascendente", "5,2,1,6,4,3")]
    [InlineData("tipo", "descendente", "3,4,1,6,2,5")]
    [InlineData("referencia", "ascendente", "4,6,1,3,2,5")]
    [InlineData("referencia", "descendente", "3,1,6,4,2,5")]
    [InlineData("detalle", "ascendente", "3,1,4,6,2,5")]
    [InlineData("detalle", "descendente", "6,4,1,3,2,5")]
    [InlineData("importe", "ascendente", "4,3,2,5,6,1")]
    [InlineData("importe", "descendente", "1,6,5,2,3,4")]
    [InlineData("saldoResultante", "ascendente", "4,5,2,6,3,1")]
    [InlineData("saldoResultante", "descendente", "1,3,6,2,5,4")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, idProveedor, ids) = await PrepararAsync($"ccp-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, idProveedor, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElLibroSigueSiendoPorFechaDescendente()
    {
        var (servicio, idProveedor, ids) = await PrepararAsync("ccp-defecto");

        Assert.Equal(PorPosicion(ids, "6,3,5,1,4,2"), await RecorrerAsync(servicio, idProveedor, null, null));
    }

    [Fact]
    public async Task OrdenarPorImporteConservaElSaldoResultanteGuardadoDeCadaFila()
    {
        var (servicio, idProveedor, ids) = await PrepararAsync("ccp-saldo-guardado");

        var pagina = await servicio.ObtenerEstadoDeCuentaAsync(
            idProveedor, null, null, historico: true, 1, 10, "importe", "ascendente");

        Assert.Equal(
            [(ids[3], -40m), (ids[2], 60m), (ids[1], 0m), (ids[4], -15m), (ids[5], 35m), (ids[0], 100m)],
            pagina.Items.Select(i => (i.IdMovimiento, i.SaldoResultante)).ToList());
    }

    [Theory]
    [InlineData("estado", null, "orden_invalido")]
    [InlineData("fecha", "sube", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, idProveedor, _) = await PrepararAsync($"ccp-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicio.ObtenerEstadoDeCuentaAsync(idProveedor, null, null, true, 1, 10, orden, direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["fecha", "tipo", "referencia", "detalle", "importe", "saldoResultante"],
            ServicioDeCuentaCorrienteDeProveedor.ClavesDeOrden.Keys.ToArray());
    }
}

public class OrdenDeCuentaDePersonalServicioTests : OrdenDeDocumentosBase
{
    private static async Task<(ServicioDeCuentaDePersonal Servicio, int IdPersonal, int[] Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new int[6];
        int idPersonal;
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            var personal = new Personal { IdTenant = IdTenant, Nombre = "Ana", CreatedAt = Ahora, UpdatedAt = Ahora };
            siembra.Personal.Add(personal);
            await siembra.SaveChangesAsync();
            idPersonal = personal.Id;

            // El saldo resultante es el guardado de cada fila, a propósito sin relación con los importes. Las horas 1 y 5
            // empatan en 8: gana el id.
            (DateOnly Fecha, TipoMovimientoPersonal Tipo, decimal? Horas, decimal? ValorHora, decimal Importe, decimal Saldo, string? Detalle)[] filas =
            [
                (new DateOnly(2026, 5, 3), TipoMovimientoPersonal.Horas, 8m, 100m, 800m, 800m, "b"),
                (new DateOnly(2026, 5, 1), TipoMovimientoPersonal.Ajuste, null, null, -50m, 750m, "ajuste"),
                (new DateOnly(2026, 5, 5), TipoMovimientoPersonal.Pago, null, null, -300m, 450m, null),
                (new DateOnly(2026, 5, 2), TipoMovimientoPersonal.Horas, 4m, 150m, 600m, 1050m, "a"),
                (new DateOnly(2026, 5, 4), TipoMovimientoPersonal.Horas, 8m, 80m, 640m, 1690m, null),
                (new DateOnly(2026, 5, 6), TipoMovimientoPersonal.Pago, null, null, -100m, 100m, "z"),
            ];
            for (var i = 0; i < filas.Length; i++)
            {
                var f = filas[i];
                var movimiento = new MovimientoPersonal
                {
                    IdTenant = IdTenant, IdPersonal = idPersonal, IdEmpleado = 1, Fecha = f.Fecha, Tipo = f.Tipo,
                    Horas = f.Horas, ValorHora = f.ValorHora, Importe = f.Importe, SaldoResultante = f.Saldo,
                    Detalle = f.Detalle, CreatedAt = Ahora
                };
                siembra.MovimientosPersonal.Add(movimiento);
                await siembra.SaveChangesAsync();
                ids[i] = movimiento.Id;
            }
        }

        var db = ContextoDelTenant(baseDeDatos);
        return (new ServicioDeCuentaDePersonal(db, Reloj, Contexto), idPersonal, ids);
    }

    private static async Task<List<int>> RecorrerAsync(
        ServicioDeCuentaDePersonal servicio, int idPersonal, string? orden, string? direccion)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 6; pagina++)
        {
            var p = await servicio.ObtenerCuentaAsync(idPersonal, null, null, historico: true, pagina, tamanio: 2, orden, direccion);
            ids.AddRange(p.Items.Select(i => i.IdMovimiento));
            if (pagina * 2 >= p.Total) break;
        }

        return ids;
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
        var (servicio, idPersonal, ids) = await PrepararAsync($"cp-{orden}-{direccion}");

        Assert.Equal(PorPosicion(ids, esperado), await RecorrerAsync(servicio, idPersonal, orden, direccion));
    }

    [Fact]
    public async Task SinOrdenElLibroSigueSiendoPorFechaDescendente()
    {
        var (servicio, idPersonal, ids) = await PrepararAsync("cp-defecto");

        Assert.Equal(PorPosicion(ids, "6,3,5,1,4,2"), await RecorrerAsync(servicio, idPersonal, null, null));
    }

    [Fact]
    public async Task OrdenarPorImporteConservaElSaldoResultanteGuardadoDeCadaFila()
    {
        var (servicio, idPersonal, ids) = await PrepararAsync("cp-saldo-guardado");

        var pagina = await servicio.ObtenerCuentaAsync(idPersonal, null, null, true, 1, 10, "importe", "ascendente");

        Assert.Equal(
            [(ids[2], 450m), (ids[5], 100m), (ids[1], 750m), (ids[3], 1050m), (ids[4], 1690m), (ids[0], 800m)],
            pagina.Items.Select(i => (i.IdMovimiento, i.SaldoResultante)).ToList());
    }

    [Theory]
    [InlineData("estado", null, "orden_invalido")]
    [InlineData("fecha", "sube", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, idPersonal, _) = await PrepararAsync($"cp-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() =>
            servicio.ObtenerCuentaAsync(idPersonal, null, null, true, 1, 10, orden, direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla()
    {
        Assert.Equal(
            ["fecha", "tipo", "horas", "valorHora", "importe", "saldoResultante", "detalle"],
            ServicioDeCuentaDePersonal.ClavesDeOrden.Keys.ToArray());
    }
}
