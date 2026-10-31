using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>Datos y utilidades compartidas por los tests de orden en el servidor de los listados de documentos
/// (compras, presupuestos, remitos, órdenes de compra) contra Postgres real. Respeta los CHECK de cada tabla: en
/// Postgres una FK no puede quedar colgante, así que "referencia que no resuelve" es una fila de baja lógica, que
/// el filtro global de EF deja invisible igual que a una FK colgante.</summary>
internal sealed record EscenarioDeDocumentos(
    int IdTenant, int PvA, int PvB, int IdEmpleado, int IdCondicionFiscal, int IdListaPrecio, int[] TiposCompra,
    HttpClient Admin);

internal static class SembradoDeDocumentos
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    public static readonly DateTimeOffset Ahora = DateTimeOffset.UtcNow;

    public static DateTimeOffset Dia(int mes, int dia) => new(2026, mes, dia, 12, 0, 0, TimeSpan.Zero);

    public static async Task<EscenarioDeDocumentos> AprovisionarAsync(WaysApiFixture fixture, string prefijo)
    {
        var nombre = $"{prefijo}{Guid.NewGuid():N}"[..14];
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
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var pvA = await db.PuntosVenta.Where(p => p.IdTenant == resultado.IdTenant).OrderBy(p => p.Id).FirstAsync();
        var pvB = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = pvA.IdEmpresa, Nombre = "Alfa", Modo = pvA.Modo,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        db.PuntosVenta.Add(pvB);
        await db.SaveChangesAsync();

        var tipos = await db.TiposComprobante
            .Where(t => t.Clase == ClaseComprobante.Compra)
            .OrderBy(t => t.Codigo)
            .Select(t => t.Id)
            .Take(3)
            .ToArrayAsync();
        Assert.Equal(3, tipos.Length);

        return new EscenarioDeDocumentos(
            resultado.IdTenant,
            pvA.Id,
            pvB.Id,
            await db.Usuarios.Where(u => u.IdTenant == resultado.IdTenant).Select(u => u.Id).FirstAsync(),
            await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            await db.ListasPrecio.Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault).Select(l => l.Id).SingleAsync(),
            tipos,
            admin);
    }

    public static Proveedor Proveedor(EscenarioDeDocumentos e, string razon, bool baja = false) => new()
    {
        IdTenant = e.IdTenant, RazonSocial = razon, IdCondicionFiscal = e.IdCondicionFiscal,
        DeletedAt = baja ? Ahora : null, CreatedAt = Ahora, UpdatedAt = Ahora
    };

    public static Cliente Cliente(EscenarioDeDocumentos e, int numero, bool baja = false) => new()
    {
        IdTenant = e.IdTenant, Numero = numero, Nombre = $"Cli{numero}", IdCondicionFiscal = e.IdCondicionFiscal,
        IdListaPrecio = e.IdListaPrecio, DeletedAt = baja ? Ahora : null, CreatedAt = Ahora, UpdatedAt = Ahora
    };

    /// <summary>Recorre todas las páginas de a <paramref name="tamanio"/> y devuelve la secuencia completa de ids.
    /// <paramref name="query"/> NO lleva <c>pagina</c> ni <c>tamanio</c>: un entero duplicado se bindea como 400.
    /// <paramref name="propiedadDelId"/> es la propiedad del id de cada fila en el contrato del endpoint.</summary>
    public static async Task<List<int>> RecorrerAsync(
        HttpClient cliente, string ruta, string query, int tamanio = 2, string propiedadDelId = "id")
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 10; pagina++)
        {
            var url = $"{ruta}?{(query.Length == 0 ? "" : query + "&")}pagina={pagina}&tamanio={tamanio}";
            var respuesta = await cliente.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
            using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
            ids.AddRange(json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(propiedadDelId).GetInt32()));
            if (pagina * tamanio >= json.RootElement.GetProperty("total").GetInt32()) break;
        }

        return ids;
    }

    public static List<int> PorPosicion(int[] ids, string posiciones) =>
        [.. posiciones.Split(',').Select(p => ids[int.Parse(p) - 1])];

    public static async Task AssertCuatrocientosAsync(HttpClient cliente, string ruta, string query, string codigo)
    {
        var respuesta = await cliente.GetAsync($"{ruta}?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains(codigo, await respuesta.Content.ReadAsStringAsync());
    }
}

/// <summary><c>GET /api/compras?orden=&amp;direccion=</c> contra Postgres. Posiciones (1 a 5, en orden de alta):
/// <code>
/// pos numero proveedor tipo estado     recepcion total
///  1  B-2    PB        T1   Confirmada 03-05     50
///  2  -      PA        T3   Borrador   -         10
///  3  A-1    (baja)    T2   Confirmada 01-10     30
///  4  C-3    PC        T2   Anulada    02-01     20
///  5  A-0    (baja)    T1   Borrador   04-01     40
/// </code>
/// T1 &lt; T2 &lt; T3 por código. Una compra confirmada o anulada exige número y fechas (CHECK de la tabla).</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/compras";

    private async Task<int[]> SembrarAsync(EscenarioDeDocumentos e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var pa = SembradoDeDocumentos.Proveedor(e, "PA");
        var pb = SembradoDeDocumentos.Proveedor(e, "PB");
        var pc = SembradoDeDocumentos.Proveedor(e, "PC");
        var baja = SembradoDeDocumentos.Proveedor(e, "ZZ-baja", baja: true);
        var baja2 = SembradoDeDocumentos.Proveedor(e, "ZZ-baja2", baja: true);
        db.Proveedores.AddRange(pa, pb, pc, baja, baja2);
        await db.SaveChangesAsync();

        (string? Numero, int Prov, int Tipo, EstadoCompra Estado, DateTimeOffset? Fecha, decimal Total)[] filas =
        [
            ("B-2", pb.Id, e.TiposCompra[0], EstadoCompra.Confirmada, SembradoDeDocumentos.Dia(3, 5), 50m),
            (null, pa.Id, e.TiposCompra[2], EstadoCompra.Borrador, null, 10m),
            ("A-1", baja.Id, e.TiposCompra[1], EstadoCompra.Confirmada, SembradoDeDocumentos.Dia(1, 10), 30m),
            ("C-3", pc.Id, e.TiposCompra[1], EstadoCompra.Anulada, SembradoDeDocumentos.Dia(2, 1), 20m),
            ("A-0", baja2.Id, e.TiposCompra[0], EstadoCompra.Borrador, SembradoDeDocumentos.Dia(4, 1), 40m),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var compra = new ComprobanteCompra
            {
                IdTenant = e.IdTenant, IdPuntoVenta = e.PvA, IdProveedor = f.Prov, IdTipoComprobante = f.Tipo,
                IdEmpleado = e.IdEmpleado, NumeroExterno = f.Numero, Estado = f.Estado, FechaRecepcion = f.Fecha,
                FechaComprobante = f.Estado == EstadoCompra.Borrador ? null : DateOnly.FromDateTime(f.Fecha!.Value.UtcDateTime),
                Total = f.Total, Subtotal = f.Total, CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.ComprobantesCompra.Add(compra);
            await db.SaveChangesAsync();
            ids[i] = compra.Id;
        }

        return ids;
    }

    [Theory]
    [InlineData("numero", "ascendente", "5,3,1,4,2")]
    [InlineData("numero", "descendente", "4,1,3,5,2")]
    [InlineData("proveedor", "ascendente", "2,1,4,3,5")]
    [InlineData("proveedor", "descendente", "4,1,2,3,5")]
    [InlineData("tipo", "ascendente", "1,5,3,4,2")]
    [InlineData("tipo", "descendente", "2,3,4,1,5")]
    [InlineData("estado", "ascendente", "4,2,5,1,3")]
    [InlineData("estado", "descendente", "1,3,2,5,4")]
    [InlineData("fechaRecepcion", "ascendente", "3,4,1,5,2")]
    [InlineData("fechaRecepcion", "descendente", "5,1,4,3,2")]
    [InlineData("total", "ascendente", "2,4,3,5,1")]
    [InlineData("total", "descendente", "1,5,3,4,2")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "cmp");
        var ids = await SembrarAsync(e);

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorIdDescendenteYUnFiltroConviveConElOrden()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "cmp");
        var ids = await SembrarAsync(e);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "5,4,3,2,1"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, ""));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "5,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "estado=Borrador&orden=total&direccion=descendente"));
    }

    [Theory]
    [InlineData("orden=estadoPago", "orden_invalido")]
    [InlineData("orden=numero&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=ascendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string query, string codigo)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "cmp");

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, Ruta, query, codigo);
    }

    [Fact]
    public async Task ElOrdenSoloVeLasComprasDelTenantDeLaSesion()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "cmp");
        var ids = await SembrarAsync(e);
        var otro = await SembradoDeDocumentos.AprovisionarAsync(fixture, "cmp");
        var idsOtro = await SembrarAsync(otro);

        var propios = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "orden=total&direccion=ascendente");
        var ajenos = await SembradoDeDocumentos.RecorrerAsync(otro.Admin, Ruta, "orden=total&direccion=ascendente");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, "2,4,3,5,1"), propios);
        Assert.Equal(SembradoDeDocumentos.PorPosicion(idsOtro, "2,4,3,5,1"), ajenos);
    }
}

/// <summary><c>GET /api/presupuestos?orden=&amp;direccion=</c> contra Postgres. PV A = "Local 1" (id menor), PV B =
/// "Alfa". Posiciones (1 a 5):
/// <code>
/// pos numero  cliente   pv  estado     vencimiento emision total
///  1  (A,5)   #210      A   Enviado    06-10       05-03   100
///  2  -       #202      B   Borrador   -           05-01   50
///  3  (A,7)   (baja)    A   Enviado    06-01       05-05   300
///  4  (B,3)   (baja)    B   Convertido 07-01       05-02   200
///  5  (A,1)   #202      A   Anulado    06-15       05-04   400
/// </code>
/// El vencimiento solo cuenta con el presupuesto Enviado. Un presupuesto numerado exige fecha de envío y
/// vencimiento (CHECK de la tabla).</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PresupuestosOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/presupuestos";

    private async Task<int[]> SembrarAsync(EscenarioDeDocumentos e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var c1 = SembradoDeDocumentos.Cliente(e, 210);
        var c2 = SembradoDeDocumentos.Cliente(e, 202);
        var baja = SembradoDeDocumentos.Cliente(e, 250, baja: true);
        var baja2 = SembradoDeDocumentos.Cliente(e, 251, baja: true);
        db.Clientes.AddRange(c1, c2, baja, baja2);
        await db.SaveChangesAsync();

        (long? Numero, int Cliente, int Pv, EstadoPresupuesto Estado, DateOnly? Vence, DateTimeOffset Emision, decimal Total)[] filas =
        [
            (5, c1.Id, e.PvA, EstadoPresupuesto.Enviado, new DateOnly(2026, 6, 10), SembradoDeDocumentos.Dia(5, 3), 100m),
            (null, c2.Id, e.PvB, EstadoPresupuesto.Borrador, null, SembradoDeDocumentos.Dia(5, 1), 50m),
            (7, baja.Id, e.PvA, EstadoPresupuesto.Enviado, new DateOnly(2026, 6, 1), SembradoDeDocumentos.Dia(5, 5), 300m),
            (3, baja2.Id, e.PvB, EstadoPresupuesto.Convertido, new DateOnly(2026, 7, 1), SembradoDeDocumentos.Dia(5, 2), 200m),
            (1, c2.Id, e.PvA, EstadoPresupuesto.Anulado, new DateOnly(2026, 6, 15), SembradoDeDocumentos.Dia(5, 4), 400m),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var presupuesto = new Presupuesto
            {
                IdTenant = e.IdTenant, IdPuntoVenta = f.Pv, IdCliente = f.Cliente, IdEmpleado = e.IdEmpleado,
                Numero = f.Numero, FechaEnvio = f.Numero is null ? null : f.Emision, Vencimiento = f.Vence,
                Estado = f.Estado, FechaEmision = f.Emision, Total = f.Total, Subtotal = f.Total,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.Presupuestos.Add(presupuesto);
            await db.SaveChangesAsync();
            ids[i] = presupuesto.Id;
        }

        return ids;
    }

    [Theory]
    [InlineData("numero", "ascendente", "5,1,3,4,2")]
    [InlineData("numero", "descendente", "4,3,1,5,2")]
    [InlineData("cliente", "ascendente", "2,5,1,3,4")]
    [InlineData("cliente", "descendente", "1,2,5,3,4")]
    [InlineData("puntoVenta", "ascendente", "2,4,1,3,5")]
    [InlineData("puntoVenta", "descendente", "1,3,5,2,4")]
    [InlineData("estado", "ascendente", "5,2,4,1,3")]
    [InlineData("estado", "descendente", "1,3,4,2,5")]
    [InlineData("vencimiento", "ascendente", "3,1,2,4,5")]
    [InlineData("vencimiento", "descendente", "1,3,2,4,5")]
    [InlineData("fechaEmision", "ascendente", "2,4,1,5,3")]
    [InlineData("fechaEmision", "descendente", "3,5,1,4,2")]
    [InlineData("total", "ascendente", "2,1,4,3,5")]
    [InlineData("total", "descendente", "5,3,4,1,2")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "pre");
        var ids = await SembrarAsync(e);

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorFechaDeEmisionDescendenteYUnFiltroConviveConElOrden()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "pre");
        var ids = await SembrarAsync(e);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "3,5,1,4,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, ""));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "3,1"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "estado=Enviado&orden=total&direccion=descendente"));
    }

    [Theory]
    [InlineData("orden=iva", "orden_invalido")]
    [InlineData("orden=total&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string query, string codigo)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "pre");

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, Ruta, query, codigo);
    }

    [Fact]
    public async Task ElOrdenSoloVeLosPresupuestosDelTenantDeLaSesion()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "pre");
        var ids = await SembrarAsync(e);
        var otro = await SembradoDeDocumentos.AprovisionarAsync(fixture, "pre");
        var idsOtro = await SembrarAsync(otro);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "2,1,4,3,5"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "orden=total&direccion=ascendente"));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(idsOtro, "2,1,4,3,5"),
            await SembradoDeDocumentos.RecorrerAsync(otro.Admin, Ruta, "orden=total&direccion=ascendente"));
    }
}

/// <summary><c>GET /api/remitos?orden=&amp;direccion=</c> contra Postgres. Mismo esquema de PV y clientes que
/// presupuestos. Posiciones (1 a 5):
/// <code>
/// pos numero  cliente   pv  estado   emision total
///  1  (A,5)   #210      A   Emitido  05-03   100
///  2  -       #202      B   Borrador 05-01   50
///  3  (A,7)   (baja)    A   Anulado  05-05   300
///  4  (B,3)   (baja)    B   Anulado  05-02   200
///  5  (A,1)   #202      A   Emitido  05-04   400
/// </code>
/// Un remito numerado exige fecha de salida (CHECK de la tabla); "facturado" exige un comprobante de venta, por eso
/// los estados sembrados son los que no lo necesitan.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RemitosOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/remitos";

    private async Task<int[]> SembrarAsync(EscenarioDeDocumentos e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var c1 = SembradoDeDocumentos.Cliente(e, 210);
        var c2 = SembradoDeDocumentos.Cliente(e, 202);
        var baja = SembradoDeDocumentos.Cliente(e, 250, baja: true);
        var baja2 = SembradoDeDocumentos.Cliente(e, 251, baja: true);
        db.Clientes.AddRange(c1, c2, baja, baja2);
        await db.SaveChangesAsync();

        (long? Numero, int Cliente, int Pv, EstadoRemito Estado, DateTimeOffset Emision, decimal Total)[] filas =
        [
            (5, c1.Id, e.PvA, EstadoRemito.Emitido, SembradoDeDocumentos.Dia(5, 3), 100m),
            (null, c2.Id, e.PvB, EstadoRemito.Borrador, SembradoDeDocumentos.Dia(5, 1), 50m),
            (7, baja.Id, e.PvA, EstadoRemito.Anulado, SembradoDeDocumentos.Dia(5, 5), 300m),
            (3, baja2.Id, e.PvB, EstadoRemito.Anulado, SembradoDeDocumentos.Dia(5, 2), 200m),
            (1, c2.Id, e.PvA, EstadoRemito.Emitido, SembradoDeDocumentos.Dia(5, 4), 400m),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var remito = new Remito
            {
                IdTenant = e.IdTenant, IdPuntoVenta = f.Pv, IdCliente = f.Cliente, IdEmpleado = e.IdEmpleado,
                Numero = f.Numero, FechaSalida = f.Numero is null ? null : f.Emision, Estado = f.Estado,
                FechaEmision = f.Emision, Total = f.Total, Subtotal = f.Total,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.Remitos.Add(remito);
            await db.SaveChangesAsync();
            ids[i] = remito.Id;
        }

        return ids;
    }

    [Theory]
    [InlineData("numero", "ascendente", "5,1,3,4,2")]
    [InlineData("numero", "descendente", "4,3,1,5,2")]
    [InlineData("cliente", "ascendente", "2,5,1,3,4")]
    [InlineData("cliente", "descendente", "1,2,5,3,4")]
    [InlineData("puntoVenta", "ascendente", "2,4,1,3,5")]
    [InlineData("puntoVenta", "descendente", "1,3,5,2,4")]
    [InlineData("estado", "ascendente", "3,4,2,1,5")]
    [InlineData("estado", "descendente", "1,5,2,3,4")]
    [InlineData("fechaEmision", "ascendente", "2,4,1,5,3")]
    [InlineData("fechaEmision", "descendente", "3,5,1,4,2")]
    [InlineData("total", "ascendente", "2,1,4,3,5")]
    [InlineData("total", "descendente", "5,3,4,1,2")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rem");
        var ids = await SembrarAsync(e);

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorFechaDeEmisionDescendenteYUnFiltroConviveConElOrden()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rem");
        var ids = await SembrarAsync(e);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "3,5,1,4,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, ""));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "5,1"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "estado=Emitido&orden=total&direccion=descendente"));
    }

    [Theory]
    [InlineData("orden=vencimiento", "orden_invalido")]
    [InlineData("orden=total&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string query, string codigo)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rem");

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, Ruta, query, codigo);
    }

    [Fact]
    public async Task ElOrdenSoloVeLosRemitosDelTenantDeLaSesion()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rem");
        var ids = await SembrarAsync(e);
        var otro = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rem");
        var idsOtro = await SembrarAsync(otro);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "2,1,4,3,5"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "orden=total&direccion=ascendente"));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(idsOtro, "2,1,4,3,5"),
            await SembradoDeDocumentos.RecorrerAsync(otro.Admin, Ruta, "orden=total&direccion=ascendente"));
    }
}

/// <summary><c>GET /api/ordenes-compra?orden=&amp;direccion=</c> contra Postgres. Posiciones (1 a 5):
/// <code>
/// pos numero proveedor pv  estado          emision esperada
///  1  30     PB        A   Enviada         05-03   06-10
///  2  -      PA        B   Borrador        05-01   -
///  3  10     (baja)    A   RecibidaParcial 05-05   06-01
///  4  20     PC        B   Cerrada         05-02   07-01
///  5  5      (baja)    A   Anulada         05-04   -
/// </code>
/// Una orden numerada exige fecha de envío y una cerrada, fecha de cierre (CHECK de la tabla).</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OrdenesDeCompraOrdenEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/ordenes-compra";

    private async Task<int[]> SembrarAsync(EscenarioDeDocumentos e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var pa = SembradoDeDocumentos.Proveedor(e, "PA");
        var pb = SembradoDeDocumentos.Proveedor(e, "PB");
        var pc = SembradoDeDocumentos.Proveedor(e, "PC");
        var baja = SembradoDeDocumentos.Proveedor(e, "ZZ-baja", baja: true);
        var baja2 = SembradoDeDocumentos.Proveedor(e, "ZZ-baja2", baja: true);
        db.Proveedores.AddRange(pa, pb, pc, baja, baja2);
        await db.SaveChangesAsync();

        (long? Numero, int Prov, int Pv, EstadoOrdenCompra Estado, DateTimeOffset Emision, DateOnly? Espera)[] filas =
        [
            (30, pb.Id, e.PvA, EstadoOrdenCompra.Enviada, SembradoDeDocumentos.Dia(5, 3), new DateOnly(2026, 6, 10)),
            (null, pa.Id, e.PvB, EstadoOrdenCompra.Borrador, SembradoDeDocumentos.Dia(5, 1), null),
            (10, baja.Id, e.PvA, EstadoOrdenCompra.RecibidaParcial, SembradoDeDocumentos.Dia(5, 5), new DateOnly(2026, 6, 1)),
            (20, pc.Id, e.PvB, EstadoOrdenCompra.Cerrada, SembradoDeDocumentos.Dia(5, 2), new DateOnly(2026, 7, 1)),
            (5, baja2.Id, e.PvA, EstadoOrdenCompra.Anulada, SembradoDeDocumentos.Dia(5, 4), null),
        ];
        var ids = new int[filas.Length];
        for (var i = 0; i < filas.Length; i++)
        {
            var f = filas[i];
            var orden = new OrdenCompra
            {
                IdTenant = e.IdTenant, IdPuntoVenta = f.Pv, IdProveedor = f.Prov, IdEmpleado = e.IdEmpleado,
                Numero = f.Numero, FechaEnvio = f.Numero is null ? null : f.Emision, Estado = f.Estado,
                FechaEmision = f.Emision, FechaEsperada = f.Espera,
                FechaCierre = f.Estado == EstadoOrdenCompra.Cerrada ? f.Emision : null,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.OrdenesCompra.Add(orden);
            await db.SaveChangesAsync();
            ids[i] = orden.Id;
        }

        return ids;
    }

    [Theory]
    [InlineData("numero", "ascendente", "5,3,4,1,2")]
    [InlineData("numero", "descendente", "1,4,3,5,2")]
    [InlineData("proveedor", "ascendente", "2,1,4,3,5")]
    [InlineData("proveedor", "descendente", "4,1,2,3,5")]
    [InlineData("puntoVenta", "ascendente", "2,4,1,3,5")]
    [InlineData("puntoVenta", "descendente", "1,3,5,2,4")]
    [InlineData("estado", "ascendente", "5,2,4,1,3")]
    [InlineData("estado", "descendente", "3,1,4,2,5")]
    [InlineData("fechaEmision", "ascendente", "2,4,1,5,3")]
    [InlineData("fechaEmision", "descendente", "3,5,1,4,2")]
    [InlineData("fechaEsperada", "ascendente", "3,1,4,2,5")]
    [InlineData("fechaEsperada", "descendente", "4,1,3,2,5")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "oc");
        var ids = await SembrarAsync(e);

        var obtenido = await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"orden={orden}&direccion={direccion}");

        Assert.Equal(SembradoDeDocumentos.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenElListadoSigueSiendoPorFechaDeEmisionDescendenteYUnFiltroConviveConElOrden()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "oc");
        var ids = await SembrarAsync(e);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "3,5,1,4,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, ""));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "4"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, $"idProveedor={await IdProveedorAsync(e, "PC")}&orden=numero&direccion=ascendente"));
    }

    private async Task<int> IdProveedorAsync(EscenarioDeDocumentos e, string razon)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Proveedores.Where(p => p.IdTenant == e.IdTenant && p.RazonSocial == razon).Select(p => p.Id).SingleAsync();
    }

    [Theory]
    [InlineData("orden=total", "orden_invalido")]
    [InlineData("orden=numero&direccion=sube", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionInvalidosSeRechazanConCuatrocientos(string query, string codigo)
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "oc");

        await SembradoDeDocumentos.AssertCuatrocientosAsync(e.Admin, Ruta, query, codigo);
    }

    [Fact]
    public async Task ElOrdenSoloVeLasOrdenesDelTenantDeLaSesion()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "oc");
        var ids = await SembrarAsync(e);
        var otro = await SembradoDeDocumentos.AprovisionarAsync(fixture, "oc");
        var idsOtro = await SembrarAsync(otro);

        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(ids, "5,3,4,1,2"),
            await SembradoDeDocumentos.RecorrerAsync(e.Admin, Ruta, "orden=numero&direccion=ascendente"));
        Assert.Equal(
            SembradoDeDocumentos.PorPosicion(idsOtro, "5,3,4,1,2"),
            await SembradoDeDocumentos.RecorrerAsync(otro.Admin, Ruta, "orden=numero&direccion=ascendente"));
    }
}
