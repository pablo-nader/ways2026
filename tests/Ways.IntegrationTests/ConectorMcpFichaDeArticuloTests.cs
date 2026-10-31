using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeConsultasMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// La herramienta <c>articulo_info</c> de punta a punta: token OAuth real, <c>POST /mcp</c>, Postgres como
/// <c>ways_app</c> (RLS activo) y reloj fijo 2026-10-18T12:00Z (las 09:00 del 18 en Buenos Aires, de modo que la
/// ventana de 30 días es 2026-09-19 a 2026-10-18). Los importes están calculados a mano.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public partial class ConectorMcpFichaDeArticuloTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly DateTimeOffset RelojDelMediodia = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HoyAlMediodia = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly ApoyoDeConsultasMcp _apoyo = new(fixture, new ApoyoDeConectorMcp(fixture));

    private static string[] Lineas(string texto) => texto.Split(Environment.NewLine);

    [GeneratedRegex(@"^\d+ \| B\d\d \| Bravo \d\d$")]
    private static partial Regex FilaDeCandidatoBravo();

    private sealed record Escenario(
        TenantDeVentas Tenant, ApoyoDeConectorMcp.UsuarioDeTenant Admin, ApoyoDeConectorMcp.UsuarioDeTenant Supervisor,
        ApoyoDeConectorMcp.UsuarioDeTenant Vendedor, int IdCola, int IdPepsi, int IdSucursalCerrada);

    private sealed class ContextoFijo(int idUsuario, int idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId { get; } = idUsuario;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static TenantActualFijo DeTenant(TenantDeVentas t) => new(ModoDeAcceso.Tenant, t.IdTenant);

    private async Task FijarCostoNominalAsync(TenantDeVentas t, int idArticulo, decimal costo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(t));
        Assert.Equal(1, await db.Articulos.Where(a => a.Id == idArticulo).ExecuteUpdateAsync(a => a.SetProperty(x => x.CostoNominal, (decimal?)costo)));
    }

    private async Task SembrarCodigoDeBarrasAsync(TenantDeVentas t, int idArticulo, string codigo, bool activo = true)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(t));
        var ahora = DateTimeOffset.UtcNow;
        db.CodigosBarra.Add(new CodigoBarra
        {
            IdTenant = t.IdTenant, IdArticulo = idArticulo, Codigo = codigo, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Baja lógica directa de una fila que el artículo sigue referenciando.</summary>
    private async Task DarDeBajaAsync(TenantDeVentas t, Func<IWaysDbContext, Task<int>> baja)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(t));
        Assert.Equal(1, await baja(db));
    }

    /// <summary>
    /// Catálogo: área Bebidas, categoría Gaseosas, marca ColaCo, proveedores Distribuidora Norte (el habitual de Cola 2L)
    /// y Mayorista Sur, sin grupo. Listas: General (por defecto), Mayorista, Gremio (derivada de General, +10%) y Vieja
    /// (inactiva). Cola 2L: General 100 hasta el 2026-09-01 y 150 desde ahí; Mayorista 120 hasta el 2026-12-01 y 999
    /// programado desde ahí; Vieja 1. Stock: Local 1 con 12 y mínimo 5, Depósito con 3,5 y un punto de venta dado de
    /// baja con 7. Ventas de Cola 2L: hoy (2, 600), en el primer instante de la ventana, 2026-09-19 00:00 local (1, 300),
    /// un minuto antes (50, 9999, afuera), una anulada (10, 9999, afuera) y una NC de hoy (−1, −100): netas 2 unidades y
    /// 800. Costo nominal 123,45. Compras confirmadas de Cola 2L, de la más reciente a la más antigua por recepción:
    /// 0001-0000300 (Norte, 5 a 110), 0001-0000200 (Sur, 20 a 90 con descuento de 100 y IVA 21% no incluido),
    /// 0001-0000100 (Norte, 10 a 100,5, con una fecha de comprobante POSTERIOR a las otras para que el orden por
    /// recepción se distinga del de la fecha mostrada) y 0001-0000050 (Sur, 1 a 80); además un borrador (7777) y una
    /// anulada (8888) que no cuentan. Pepsi, hermano del mismo tenant, tiene su propio stock, ventas, compra y precio.
    /// </summary>
    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        var t = await _apoyo.SembrarTenantAsync(nombre);
        var admin = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Admin);
        var supervisor = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Supervisor);
        var vendedor = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Vendedor);

        var area = await _apoyo.SembrarAreaAsync(t, "Bebidas");
        var gaseosas = await _apoyo.SembrarCategoriaAsync(t, "Gaseosas");
        var colaCo = await _apoyo.SembrarMarcaAsync(t, "ColaCo");
        var norte = await _apoyo.SembrarProveedorAsync(t, "Distribuidora Norte");
        var sur = await _apoyo.SembrarProveedorAsync(t, "Mayorista Sur");
        var cola = await _apoyo.SembrarArticuloAsync(t, "Cola 2L", "COLA-2L", area, gaseosas, colaCo, norte);
        var pepsi = await _apoyo.SembrarArticuloAsync(t, "Pepsi 2L", "PEPSI-2L", area, gaseosas, colaCo, norte);
        await FijarCostoNominalAsync(t, cola, 123.45m);

        var general = await _apoyo.IdDeLaListaGeneralAsync(t);
        var mayorista = await _apoyo.SembrarListaDePreciosAsync(t, "Mayorista");
        await _apoyo.SembrarListaDePreciosAsync(t, "Gremio", ModoLista.Derivada, general, 10m);
        var vieja = await _apoyo.SembrarListaDePreciosAsync(t, "Vieja", activa: false);
        await _apoyo.SembrarPrecioAsync(t, cola, general, 100m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, cola, general, 150m, new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, cola, mayorista, 120m, new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, cola, mayorista, 999m, new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, cola, vieja, 1m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, pepsi, general, 777m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var deposito = await _apoyo.SembrarPuntoVentaAsync(t, "Depósito");
        var cerrado = await _apoyo.SembrarPuntoVentaAsync(t, "Cerrado");
        await _apoyo.SembrarStockAsync(t, cola, t.IdPuntoVenta, 12m, 5m);
        await _apoyo.SembrarStockAsync(t, cola, deposito, 3.5m);
        await _apoyo.SembrarStockAsync(t, cola, cerrado, 7m);
        await _apoyo.SembrarStockAsync(t, pepsi, t.IdPuntoVenta, 999m);
        var sucursalCerrada = await _apoyo.DarDeBajaPuntoVentaAsync(t, cerrado);

        var empleado = admin.IdUsuario;
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, empleado, [new(cola, "Cola 2L", area, 2m, 600m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 9, 19, 3, 0, 0, TimeSpan.Zero), empleado, [new(cola, "Cola 2L", area, 1m, 300m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 9, 19, 2, 59, 0, TimeSpan.Zero), empleado, [new(cola, "Cola 2L", area, 50m, 9999m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, empleado, [new(cola, "Cola 2L", area, 10m, 9999m)], estado: EstadoComprobante.Anulado);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, empleado, [new(cola, "Cola 2L", area, -1m, -100m)], esNcx: true);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, empleado, [new(pepsi, "Pepsi 2L", area, 44m, 4444m)]);

        await _apoyo.SembrarCompraAsync(t, norte, empleado, EstadoCompra.Confirmada, new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 13), "0001-0000300", [new(cola, 5m, 110m)]);
        await _apoyo.SembrarCompraAsync(t, sur, empleado, EstadoCompra.Confirmada, new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 11), "0001-0000200", [new(cola, 20m, 90m, 100m, 21m)], discriminaIva: true);
        await _apoyo.SembrarCompraAsync(t, norte, empleado, EstadoCompra.Confirmada, new(2026, 10, 10, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 17), "0001-0000100", [new(cola, 10m, 100.5m)]);
        await _apoyo.SembrarCompraAsync(t, sur, empleado, EstadoCompra.Confirmada, new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero),
            new(2026, 9, 1), "0001-0000050", [new(cola, 1m, 80m)]);
        await _apoyo.SembrarCompraAsync(t, norte, empleado, EstadoCompra.Borrador, default, default, "x", [new(cola, 1m, 7777m)]);
        await _apoyo.SembrarCompraAsync(t, norte, empleado, EstadoCompra.Anulada, new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 15), "0001-0000999", [new(cola, 1m, 8888m)]);
        await _apoyo.SembrarCompraAsync(t, sur, empleado, EstadoCompra.Confirmada, new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 16), "0001-0000777", [new(pepsi, 3m, 55.55m)]);

        return new Escenario(t, admin, supervisor, vendedor, cola, pepsi, sucursalCerrada);
    }

    private async Task<ResultadoDeHerramienta> LlamarComoAsync(
        ApoyoDeConectorMcp.UsuarioDeTenant usuario, object argumentos, DateTimeOffset? reloj = null)
    {
        await using var host = _apoyo.HostConReloj(reloj ?? RelojDelMediodia, usuario.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        return await LlamarHerramientaAsync(cliente, await AccessTokenAsync(cliente, usuario.Mail), "articulo_info", argumentos);
    }

    private static string[] FichaDeColaEsperada(int idCola, int idCerrado, int comprasMostradas = 4, bool conMinimo = true) =>
    [
        $"Ficha de artículo: Cola 2L (id {idCola}, código COLA-2L)",
        "Estado: activo | producto | unidad de venta: unidad",
        "Clasificación actual: área Bebidas | grupo (sin grupo) | categoría Gaseosas | marca ColaCo | proveedor habitual Distribuidora Norte",
        "Alcance: todas las empresas del tenant",
        "",
        "Precios vigentes por lista:",
        "lista | precio",
        "General | 150,00",
        "Gremio (derivada) | 165,00",
        "Mayorista | 120,00",
        "",
        "Stock por punto de venta (solo los que tienen saldo registrado):",
        conMinimo ? "punto de venta | cantidad | mínimo" : "punto de venta | cantidad",
        conMinimo ? "Local 1 | 12 | 5" : "Local 1 | 12",
        conMinimo ? "Depósito | 3,5 | -" : "Depósito | 3,5",
        conMinimo ? $"(sucursal id {idCerrado}) | 7 | -" : $"(sucursal id {idCerrado}) | 7",
        "",
        "Ventas de los últimos 30 días (2026-09-19 a 2026-10-18, zona horaria: America/Argentina/Buenos_Aires): 2 unidades, importe 800,00",
        "",
        "Costo nominal (por unidad, con IVA y neto del descuento del proveedor): 123,45",
        "Última compra: 2026-10-13 a Distribuidora Norte, comprobante 0001-0000300, 5 unidades a costo unitario 110,0000 (costo efectivo 110,00)",
        "",
        $"Últimas {comprasMostradas} de 4 compras confirmadas:",
        "fecha | proveedor | comprobante | cantidad | costo unitario | descuento | costo efectivo",
        .. new[]
        {
            "2026-10-13 | Distribuidora Norte | 0001-0000300 | 5 | 110,0000 | 0,00 | 110,00",
            "2026-10-11 | Mayorista Sur | 0001-0000200 | 20 | 90,0000 | 100,00 | 102,85",
            "2026-10-17 | Distribuidora Norte | 0001-0000100 | 10 | 100,5000 | 0,00 | 100,50",
            "2026-09-01 | Mayorista Sur | 0001-0000050 | 1 | 80,0000 | 0,00 | 80,00"
        }.Take(comprasMostradas)
    ];

    /// <summary>Cláusulas bajo prueba: el orden de las compras por recepción descendente (la fecha mostrada de la
    /// tercera es posterior a la de la segunda), el filtro de compras confirmadas (el borrador y la anulada quedan
    /// afuera), el costo efectivo con IVA de la segunda, la ventana de 30 días en la zona de la empresa (la venta de
    /// 02:59Z queda afuera y la de 03:00Z, el primer instante del 19 local, entra), las listas activas y el precio
    /// vigente (el cerrado y el programado no aparecen). Supervisor ve exactamente lo mismo que Admin.</summary>
    [Fact]
    public async Task AdminYSupervisorVenLaFichaCompletaConPreciosVigentesStockVentasYComprasEnOrden()
    {
        var e = await SembrarEscenarioAsync(nameof(AdminYSupervisorVenLaFichaCompletaConPreciosVigentesStockVentasYComprasEnOrden));

        var admin = await LlamarComoAsync(e.Admin, new { articulo = "COLA-2L" });
        var supervisor = await LlamarComoAsync(e.Supervisor, new { articulo = "COLA-2L" });

        Assert.False(admin.EsError, admin.Texto);
        Assert.Equal(FichaDeColaEsperada(e.IdCola, e.IdSucursalCerrada), Lineas(admin.Texto));
        Assert.Equal(admin.Texto, supervisor.Texto);
    }

    [Fact]
    public async Task ElTopeDeComprasLimitaLasFilasYCuentaElTotalDeLasConfirmadas()
    {
        var e = await SembrarEscenarioAsync(nameof(ElTopeDeComprasLimitaLasFilasYCuentaElTotalDeLasConfirmadas));

        var dos = await LlamarComoAsync(e.Admin, new { articulo = "COLA-2L", compras = 2 });
        var cero = await LlamarComoAsync(e.Admin, new { articulo = "COLA-2L", compras = 0 });
        var veintiuna = await LlamarComoAsync(e.Admin, new { articulo = "COLA-2L", compras = 21 });

        Assert.False(dos.EsError, dos.Texto);
        Assert.Equal(FichaDeColaEsperada(e.IdCola, e.IdSucursalCerrada, comprasMostradas: 2), Lineas(dos.Texto));
        Assert.True(cero.EsError);
        Assert.Contains("entre 1 y 20", cero.Texto);
        Assert.True(veintiuna.EsError);
        Assert.Contains("entre 1 y 20", veintiuna.Texto);
    }

    /// <summary>Cláusula bajo prueba: la política <c>LecturaDeCostos</c> en <c>ArticuloInfoAsync</c> y la de
    /// <c>LecturaDeReportes</c> para las ventas. El Vendedor ve precios y stock, y ninguna cifra de costo, de compra ni de
    /// venta ni el proveedor de la compra (el habitual de la clasificación sí, como en la grilla de artículos), y el
    /// stock sin el mínimo, que en la web solo leen los reportes.</summary>
    [Fact]
    public async Task ElVendedorVePreciosYStockPeroNingunCostoCompraNiVenta()
    {
        var e = await SembrarEscenarioAsync(nameof(ElVendedorVePreciosYStockPeroNingunCostoCompraNiVenta));

        var resultado = await LlamarComoAsync(e.Vendedor, new { articulo = "COLA-2L" });

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Equal(
            [
                .. FichaDeColaEsperada(e.IdCola, e.IdSucursalCerrada, conMinimo: false).Take(16),
                "",
                "Ventas: requiere Supervisor o Admin.",
                "Costos y compras: no disponibles para tu rol."
            ],
            Lineas(resultado.Texto));
        foreach (var prohibido in new[] { "123,45", "110,00", "102,85", "100,50", "Mayorista Sur", "0001-0000", "800,00", "7777", "8888" })
        {
            Assert.DoesNotContain(prohibido, resultado.Texto);
        }
    }

    [Fact]
    public async Task ElOtroTenantConElMismoCodigoNuncaAparece()
    {
        var a = await SembrarEscenarioAsync(nameof(ElOtroTenantConElMismoCodigoNuncaAparece) + "-a");
        var b = await _apoyo.SembrarTenantAsync(nameof(ElOtroTenantConElMismoCodigoNuncaAparece) + "-b");
        var adminB = await _apoyo.SembrarUsuarioAsync(b, RolConocido.Admin);
        var areaB = await _apoyo.SembrarAreaAsync(b, "Bebidas");
        var proveedorB = await _apoyo.SembrarProveedorAsync(b, "Proveedor Ajeno");
        var colaB = await _apoyo.SembrarArticuloAsync(b, "Cola 2L", "COLA-2L", areaB);
        await _apoyo.SembrarPrecioAsync(b, colaB, await _apoyo.IdDeLaListaGeneralAsync(b), 424242m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarStockAsync(b, colaB, b.IdPuntoVenta, 31337m);
        await _apoyo.SembrarComprobanteConLineasAsync(b, HoyAlMediodia, adminB.IdUsuario, [new(colaB, "Cola 2L", areaB, 1000m, 123456m)]);
        await _apoyo.SembrarCompraAsync(b, proveedorB, adminB.IdUsuario, EstadoCompra.Confirmada, new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 17), "9999-0000001", [new(colaB, 7m, 31415.9265m)]);

        var resultado = await LlamarComoAsync(a.Admin, new { articulo = "COLA-2L" });

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Equal(FichaDeColaEsperada(a.IdCola, a.IdSucursalCerrada), Lineas(resultado.Texto));
        foreach (var ajeno in new[] { "424", "31.337", "123.456", "Proveedor Ajeno", "9999-0000001", "31.415" })
        {
            Assert.DoesNotContain(ajeno, resultado.Texto);
        }
    }

    /// <summary>Cláusula bajo prueba: el filtro global de EF de <c>db.Articulos</c> en la búsqueda del artículo. Como
    /// <c>ways_app</c> el RLS ya oculta las filas de otro tenant y tapa ese filtro, así que este caso baja por debajo
    /// del confound: el servicio corre sobre un contexto de owner (superusuario, sin RLS) en modo tenant A, con un
    /// artículo del mismo código en el tenant B. Con el filtro quitado la búsqueda por código encuentra dos artículos
    /// y se rechaza por ambigua. El stock y las compras no llevan cláusula de tenant propia: salen del id del artículo
    /// ya resuelto y la FK compuesta <c>(id_articulo, id_tenant)</c> de <c>stock</c> impide una fila de otro tenant.</summary>
    [Fact]
    public async Task SinRlsLaBusquedaDelArticuloAlcanzaParaNoMezclarOtroTenant()
    {
        var a = await SembrarEscenarioAsync(nameof(SinRlsLaBusquedaDelArticuloAlcanzaParaNoMezclarOtroTenant) + "-a");
        var b = await _apoyo.SembrarTenantAsync(nameof(SinRlsLaBusquedaDelArticuloAlcanzaParaNoMezclarOtroTenant) + "-b");
        var adminB = await _apoyo.SembrarUsuarioAsync(b, RolConocido.Admin);
        var areaB = await _apoyo.SembrarAreaAsync(b, "Bebidas");
        var proveedorB = await _apoyo.SembrarProveedorAsync(b, "Proveedor Ajeno");
        var colaB = await _apoyo.SembrarArticuloAsync(b, "Cola 2L", "COLA-2L", areaB);
        await _apoyo.SembrarStockAsync(b, colaB, b.IdPuntoVenta, 31337m);
        await _apoyo.SembrarCompraAsync(b, proveedorB, adminB.IdUsuario, EstadoCompra.Confirmada, new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 17), "9999-0000001", [new(colaB, 7m, 31415.9265m)]);

        await using var db = fixture.CrearContextoDeOwner(new TenantActualFijo(ModoDeAcceso.Tenant, a.Tenant.IdTenant));
        var reloj = new RelojFijo(RelojDelMediodia);
        var contexto = new ContextoFijo(a.Admin.IdUsuario, a.Tenant.IdTenant);
        var parametros = new ServicioDeParametros(db, reloj, new ServicioDeLotes(db, reloj, contexto));
        var consultas = new ServicioDeConsultasDeVentas(
            db, reloj, parametros, new ServicioDeReportesDeVentas(db, new LectorDeSerieTemporal(db), parametros),
            new ServicioDeReportesDeArticulos(db, parametros));
        var servicio = new ServicioDeFichaDeArticulo(db, reloj, new ServicioDePrecios(db, reloj, contexto), consultas);

        var ficha = await servicio.ObtenerAsync(new SolicitudDeFichaDeArticulo("COLA-2L", null, 5, true, true));

        Assert.Equal(a.IdCola, ficha.Id);
        Assert.Equal(["Local 1", "Depósito", $"(sucursal id {a.IdSucursalCerrada})"], ficha.Stock.Select(s => s.PuntoVenta).ToArray());
        Assert.Equal(4, ficha.Costos!.TotalDeCompras);
        Assert.DoesNotContain(ficha.Costos.Compras, c => c.Proveedor == "Proveedor Ajeno");
    }

    /// <summary>Cláusula bajo prueba: la resolución exacta primero de <c>BuscarArticuloAsync</c>. "A1" es un código
    /// exacto aunque A10 y A11 también lo contengan, y "a1" también: el paso exacto ignora mayúsculas (si no, el
    /// "contiene" lo haría ambiguo); el código de barras, el nombre exacto y el id también ganan antes
    /// de buscar por contenido; un código de barras inactivo no resuelve; con más de un candidato se listan hasta 10.</summary>
    [Fact]
    public async Task LaCoincidenciaExactaGanaAntesDeBuscarPorContenidoYLaAmbiguaListaHastaDiezCandidatos()
    {
        var t = await _apoyo.SembrarTenantAsync(nameof(LaCoincidenciaExactaGanaAntesDeBuscarPorContenidoYLaAmbiguaListaHastaDiezCandidatos));
        var admin = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Admin);
        var area = await _apoyo.SembrarAreaAsync(t, "Varios");
        var a1 = await _apoyo.SembrarArticuloAsync(t, "Alfa uno", "A1", area);
        var a10 = await _apoyo.SembrarArticuloAsync(t, "Alfa diez", "A10", area);
        var a11 = await _apoyo.SembrarArticuloAsync(t, "Alfa once", "A11", area);
        var gaseosa = await _apoyo.SembrarArticuloAsync(t, "Gaseosa", "G-1", area);
        await _apoyo.SembrarArticuloAsync(t, "Gaseosa cola", "G-2", area);
        for (var i = 1; i <= 12; i++)
        {
            await _apoyo.SembrarArticuloAsync(t, $"Bravo {i:00}", $"B{i:00}", area);
        }

        await SembrarCodigoDeBarrasAsync(t, a10, "7791234567890");
        await SembrarCodigoDeBarrasAsync(t, a11, "7790000000001", activo: false);

        async Task<ResultadoDeHerramienta> Buscar(string texto) => await LlamarComoAsync(admin, new { articulo = texto });

        Assert.StartsWith($"Ficha de artículo: Alfa uno (id {a1}, código A1)", (await Buscar("A1")).Texto);
        Assert.StartsWith($"Ficha de artículo: Alfa uno (id {a1}, código A1)", (await Buscar("a1")).Texto);
        Assert.StartsWith($"Ficha de artículo: Alfa diez (id {a10}, código A10)", (await Buscar("a10")).Texto);
        Assert.StartsWith($"Ficha de artículo: Alfa once (id {a11}, código A11)", (await Buscar(a11.ToString())).Texto);
        Assert.StartsWith($"Ficha de artículo: Alfa diez (id {a10}, código A10)", (await Buscar("7791234567890")).Texto);
        Assert.StartsWith($"Ficha de artículo: Gaseosa (id {gaseosa}, código G-1)", (await Buscar("GASEOSA")).Texto);

        var inactivo = await Buscar("7790000000001");
        Assert.True(inactivo.EsError);
        Assert.Contains("No encontré el artículo '7790000000001'", inactivo.Texto);

        var ambiguo = await Buscar("alfa");
        Assert.True(ambiguo.EsError);
        Assert.Equal(
            [
                "'alfa' es ambiguo para el artículo; coincide con (id | código | nombre):",
                $"{a1} | A1 | Alfa uno",
                $"{a10} | A10 | Alfa diez",
                $"{a11} | A11 | Alfa once",
                "Indicá el id o el código exacto."
            ],
            Lineas(ambiguo.Texto));

        var muchos = await Buscar("bravo");
        Assert.True(muchos.EsError);
        var lineas = Lineas(muchos.Texto);
        Assert.Equal(10, lineas.Count(l => FilaDeCandidatoBravo().IsMatch(l)));
        Assert.Contains("… y más", lineas);

        var inexistente = await Buscar("zzz");
        Assert.True(inexistente.EsError);
        Assert.Equal("No encontré el artículo 'zzz'.", inexistente.Texto);
    }

    /// <summary>Cláusulas bajo prueba: el LEFT JOIN de la cabecera y el de proveedores. Categoría, marca y proveedor habitual
    /// dados de baja (el artículo conserva el id) se muestran como "(sin …)", el proveedor de la compra dado de baja
    /// como "(proveedor id N)", el artículo inactivo como inactivo y un servicio no muestra stock.</summary>
    [Fact]
    public async Task UnaClasificacionOUnProveedorDadosDeBajaSeMuestranComoSinAsignarYElServicioNoTieneStock()
    {
        var t = await _apoyo.SembrarTenantAsync(nameof(UnaClasificacionOUnProveedorDadosDeBajaSeMuestranComoSinAsignarYElServicioNoTieneStock));
        var admin = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Admin);
        var area = await _apoyo.SembrarAreaAsync(t, "Servicios");
        var categoria = await _apoyo.SembrarCategoriaAsync(t, "Descontinuada");
        var marca = await _apoyo.SembrarMarcaAsync(t, "Vieja marca");
        var proveedor = await _apoyo.SembrarProveedorAsync(t, "Proveedor caído");
        var idArticulo = await _apoyo.SembrarArticuloAsync(t, "Flete", "FLETE", area, categoria, marca, proveedor);
        await _apoyo.SembrarCompraAsync(t, proveedor, admin.IdUsuario, EstadoCompra.Confirmada, new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 13), "0001-0000001", [new(idArticulo, 2m, 50m)]);
        var ahora = DateTimeOffset.UtcNow;
        await DarDeBajaAsync(t, db => db.Categorias.Where(c => c.Id == categoria).ExecuteUpdateAsync(c => c.SetProperty(x => x.DeletedAt, ahora)));
        await DarDeBajaAsync(t, db => db.Marcas.Where(m => m.Id == marca).ExecuteUpdateAsync(m => m.SetProperty(x => x.DeletedAt, ahora)));
        await DarDeBajaAsync(t, db => db.Proveedores.Where(p => p.Id == proveedor).ExecuteUpdateAsync(p => p.SetProperty(x => x.DeletedAt, ahora)));
        await DarDeBajaAsync(t, db => db.Articulos.Where(a => a.Id == idArticulo)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.Activo, false).SetProperty(x => x.EsProducto, false)));

        var resultado = await LlamarComoAsync(admin, new { articulo = "FLETE" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.Equal(
            [
                $"Ficha de artículo: Flete (id {idArticulo}, código FLETE)",
                "Estado: inactivo | servicio | unidad de venta: unidad",
                "Clasificación actual: área Servicios | grupo (sin grupo) | categoría (sin categoría) | marca (sin marca) | proveedor habitual (sin proveedor)"
            ],
            lineas.Take(3));
        Assert.Contains("Stock: es un servicio, no maneja stock.", lineas);
        Assert.Contains($"2026-10-13 | (proveedor id {proveedor}) | 0001-0000001 | 2 | 50,0000 | 0,00 | 50,00", lineas);
    }

    /// <summary>Cláusula bajo prueba: el "hoy" local de la empresa como extremo de la ventana de 30 días. Con el reloj en
    /// 2026-10-18T02:00Z son las 23:00 del 17 en Buenos Aires: la ventana va del 18 de septiembre al 17 de octubre. Hay una
    /// venta en el primer instante (03:00Z del 18/09) y otra un minuto antes, y otra del 17 a la noche (01:00Z) y otra
    /// pasada la medianoche local (03:30Z del 18/10); con el "hoy" tomado de UTC las dos del medio cambian de lado.</summary>
    [Fact]
    public async Task LaVentanaDeTreintaDiasTerminaEnElHoyLocalDeLaEmpresaYNoEnElDeUtc()
    {
        var t = await _apoyo.SembrarTenantAsync(nameof(LaVentanaDeTreintaDiasTerminaEnElHoyLocalDeLaEmpresaYNoEnElDeUtc));
        var admin = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Admin);
        var area = await _apoyo.SembrarAreaAsync(t, "Bebidas");
        var articulo = await _apoyo.SembrarArticuloAsync(t, "Agua 1L", "AGUA-1L", area);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 9, 18, 3, 0, 0, TimeSpan.Zero), admin.IdUsuario, [new(articulo, "Agua", area, 2m, 200m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 9, 18, 2, 59, 0, TimeSpan.Zero), admin.IdUsuario, [new(articulo, "Agua", area, 7m, 7000m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 10, 18, 1, 0, 0, TimeSpan.Zero), admin.IdUsuario, [new(articulo, "Agua", area, 1m, 100m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(t, new(2026, 10, 18, 3, 30, 0, TimeSpan.Zero), admin.IdUsuario, [new(articulo, "Agua", area, 5m, 500m)]);

        var resultado = await LlamarComoAsync(admin, new { articulo = "AGUA-1L" }, reloj: new DateTimeOffset(2026, 10, 18, 2, 0, 0, TimeSpan.Zero));

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Contains(
            "Ventas de los últimos 30 días (2026-09-18 a 2026-10-17, zona horaria: America/Argentina/Buenos_Aires): 3 unidades, importe 300,00",
            Lineas(resultado.Texto));
    }

    /// <summary>Cláusula bajo prueba: el alcance por empresa de listas, stock, ventas y compras. Sin empresa se suman las
    /// dos y cada punto de venta lleva su empresa; con empresa solo cuenta la elegida y una lista propia de la otra
    /// no aparece.</summary>
    [Fact]
    public async Task ConEmpresaLaFichaMuestraSoloSusListasStockVentasYCompras()
    {
        var t = await _apoyo.SembrarTenantAsync(nameof(ConEmpresaLaFichaMuestraSoloSusListasStockVentasYCompras));
        var admin = await _apoyo.SembrarUsuarioAsync(t, RolConocido.Admin);
        var area = await _apoyo.SembrarAreaAsync(t, "Bebidas");
        var proveedor = await _apoyo.SembrarProveedorAsync(t, "Proveedor X");
        var articulo = await _apoyo.SembrarArticuloAsync(t, "Agua 1L", "AGUA-1L", area);
        var empresaDos = await _apoyo.SembrarEmpresaAsync(t, "Segunda SA");
        var cajaNorte = await _apoyo.SembrarPuntoVentaAsync(t, "Caja Norte", empresaDos);
        var listaPropia = await _apoyo.SembrarListaDePreciosAsync(t, "Propia de Segunda", idEmpresa: empresaDos);
        var listaDeLaPrimera = await _apoyo.SembrarListaDePreciosAsync(t, "Propia de Primera", idEmpresa: t.IdEmpresa);
        var general = await _apoyo.IdDeLaListaGeneralAsync(t);
        await _apoyo.SembrarPrecioAsync(t, articulo, general, 10m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, articulo, listaDeLaPrimera, 30m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarPrecioAsync(t, articulo, listaPropia, 20m, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await _apoyo.SembrarStockAsync(t, articulo, t.IdPuntoVenta, 4m);
        await _apoyo.SembrarStockAsync(t, articulo, cajaNorte, 9m);
        await _apoyo.SembrarComprobanteConLineasAsync(t, HoyAlMediodia, admin.IdUsuario, [new(articulo, "Agua 1L", area, 1m, 100m)]);
        await _apoyo.SembrarComprobanteConLineasAsync(
            t, HoyAlMediodia, admin.IdUsuario, [new(articulo, "Agua 1L", area, 3m, 450m)], idPuntoVenta: cajaNorte);
        await _apoyo.SembrarCompraAsync(t, proveedor, admin.IdUsuario, EstadoCompra.Confirmada, new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 13), "0001-0000001", [new(articulo, 1m, 10m)]);
        await _apoyo.SembrarCompraAsync(t, proveedor, admin.IdUsuario, EstadoCompra.Confirmada, new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero),
            new(2026, 10, 14), "0001-0000002", [new(articulo, 1m, 20m)], idPuntoVenta: cajaNorte);
        string empresaUno;
        await using (var db = fixture.CrearContextoDeAplicacion(DeTenant(t)))
        {
            empresaUno = await db.Empresas.Where(e => e.Id == t.IdEmpresa).Select(e => e.NombreFantasia ?? e.RazonSocial).SingleAsync();
        }

        var todas = await LlamarComoAsync(admin, new { articulo = "AGUA-1L" });
        var deLaSegunda = await LlamarComoAsync(admin, new { articulo = "AGUA-1L", empresa = "Segunda SA" });
        var inexistente = await LlamarComoAsync(admin, new { articulo = "AGUA-1L", empresa = "Tercera" });

        Assert.False(todas.EsError, todas.Texto);
        var lineasTodas = Lineas(todas.Texto);
        Assert.Contains("General | 10,00", lineasTodas);
        Assert.Contains("Propia de Segunda (empresa Segunda SA) | 20,00", lineasTodas);
        Assert.Contains($"Propia de Primera (empresa {empresaUno}) | 30,00", lineasTodas);
        Assert.Contains($"Local 1 ({empresaUno}) | 4 | -", lineasTodas);
        Assert.Contains("Caja Norte (Segunda SA) | 9 | -", lineasTodas);
        Assert.Contains("(2026-09-19 a 2026-10-18, zona horaria: America/Argentina/Buenos_Aires): 4 unidades, importe 550,00", todas.Texto);
        Assert.Contains("Últimas 2 de 2 compras confirmadas:", lineasTodas);

        Assert.False(deLaSegunda.EsError, deLaSegunda.Texto);
        var lineasSegunda = Lineas(deLaSegunda.Texto);
        Assert.Contains("Alcance: empresa Segunda SA", lineasSegunda);
        Assert.Contains("General | 10,00", lineasSegunda);
        Assert.Contains("Propia de Segunda | 20,00", lineasSegunda);
        Assert.DoesNotContain("Propia de Primera", deLaSegunda.Texto);
        Assert.Contains("Caja Norte | 9 | -", lineasSegunda);
        Assert.DoesNotContain(lineasSegunda, l => l.StartsWith("Local 1"));
        Assert.Contains("3 unidades, importe 450,00", deLaSegunda.Texto);
        Assert.Contains("Últimas 1 de 1 compras confirmadas:", lineasSegunda);
        Assert.Contains("2026-10-14 | Proveedor X | 0001-0000002 | 1 | 20,0000 | 0,00 | 20,00", lineasSegunda);
        Assert.DoesNotContain("0001-0000001", deLaSegunda.Texto);

        Assert.True(inexistente.EsError);
        Assert.Contains("No encontré la empresa 'Tercera'", inexistente.Texto);
    }

    /// <summary>El título y las anotaciones los fija <see cref="ConectorMcpConjuntoDeHerramientasTests"/>.</summary>
    [Fact]
    public async Task ToolsListAnunciaArticuloInfoConSusArgumentos()
    {
        var e = await SembrarEscenarioAsync(nameof(ToolsListAnunciaArticuloInfoConSusArgumentos));
        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Vendedor.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);

        var (_, mensaje) = await ApoyoDeConectorMcp.LlamarMcpAsync(cliente, await AccessTokenAsync(cliente, e.Vendedor.Mail), "tools/list");

        var herramienta = mensaje!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(h => h.GetProperty("name").GetString() == "articulo_info");
        var esquema = herramienta.GetProperty("inputSchema");
        Assert.Equal(["articulo", "compras", "empresa"], esquema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["articulo"], esquema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }
}
