using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Combos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra común de las pruebas de combos que cruzan el POS (vista previa del carrito, instantánea y venta offline):
/// un tenant aprovisionado con su administrador, un turno abierto, y artículos, familias, combos y ofertas sembrados
/// directo por EF. Los precios van en la lista por defecto del tenant, la que usa el Consumidor Final: tanto la venta
/// sin cliente como la instantánea la cotizan.
/// </summary>
internal sealed class CombosApoyo(WaysApiFixture fixture)
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    public static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public sealed record Escenario(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo)
    {
        public HttpClient? Cajero { get; set; }
    }

    public WaysDbContext Db(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    /// <summary>Tenant nuevo con un punto de venta en el modo pedido, el administrador logueado y un turno abierto. En
    /// modo escritorio además deja logueado un cajero de dispositivo (<see cref="Escenario.Cajero"/>).</summary>
    public async Task<Escenario> PrepararAsync(string nombre, ModoPuntoVenta modo)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{unico}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants", new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, modo));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = Db(resultado.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = "Combos-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idLista = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var escenario = new Escenario(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva, idLista,
            idMedioEfectivo);

        if (modo == ModoPuntoVenta.Escritorio)
        {
            escenario.Cajero = await LoguearCajeroDeDispositivoAsync(escenario);
        }

        return escenario;
    }

    private async Task<HttpClient> LoguearCajeroDeDispositivoAsync(Escenario ctx)
    {
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(ctx.IdPuntoVenta, "Caja combos"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        var cookieDispositivo = valor[..valor.IndexOf(';')];

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = ctx.IdTenant, NombreUsuario = "cajero-combos",
                Mail = $"cajero-combos-{ctx.IdTenant}@ways.test", RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero), PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-combos", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cajero;
    }

    /// <summary>Reserva un bloque de numeración del dispositivo: las ventas offline usan 1..<paramref name="cantidad"/>.</summary>
    public static async Task ReservarBloqueAsync(Escenario ctx, int cantidad = 10)
    {
        var respuesta = await ctx.Cajero!.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(ctx.IdPuntoVenta, "TX", cantidad));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    public async Task<int> ArticuloAsync(
        Escenario ctx, string nombre, decimal precio, UnidadVenta unidad = UnidadVenta.Unidad, int? idFamilia = null,
        bool activo = true, bool conPrecio = true, bool disponibleVenta = true)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = unidad, IdFamilia = idFamilia,
            EsProducto = true, Activo = activo, DisponibleVenta = disponibleVenta, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (conPrecio)
        {
            db.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return articulo.Id;
    }

    public async Task<int> FamiliaAsync(Escenario ctx, string nombre)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var familia = new Familia { IdTenant = ctx.IdTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }

    public async Task<int> OtraEmpresaAsync(Escenario ctx)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa.Id;
    }

    public static (int? IdArticulo, int? IdFamilia, decimal Cantidad) Art(int id, decimal cantidad = 1m) => (id, null, cantidad);

    public static (int? IdArticulo, int? IdFamilia, decimal Cantidad) Fam(int id, decimal cantidad = 1m) => (null, id, cantidad);

    public async Task<int> ComboAsync(
        Escenario ctx, decimal precio, IReadOnlyList<(int? IdArticulo, int? IdFamilia, decimal Cantidad)> componentes,
        int? idEmpresa = null, bool activo = true, bool dadoDeBaja = false, string nombre = "Combo")
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var codigo = (await db.Combos.IgnoreQueryFilters().Where(c => c.IdTenant == ctx.IdTenant).MaxAsync(c => (int?)c.Codigo) ?? 0) + 1;

        var combo = new Combo
        {
            IdTenant = ctx.IdTenant, Codigo = codigo, Nombre = nombre, Precio = precio, IdEmpresa = idEmpresa,
            Activo = activo, CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = dadoDeBaja ? ahora : null
        };
        db.Combos.Add(combo);
        await db.SaveChangesAsync();

        db.CombosComponentes.AddRange(componentes.Select(c => new ComboComponente
        {
            IdTenant = ctx.IdTenant, IdCombo = combo.Id, IdArticulo = c.IdArticulo, IdFamilia = c.IdFamilia,
            Cantidad = c.Cantidad
        }));
        await db.SaveChangesAsync();

        return combo.Id;
    }

    public async Task<int> OfertaDePrecioAsync(Escenario ctx, int idArticulo, decimal precioUnitario)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var oferta = new Oferta
        {
            IdTenant = ctx.IdTenant, Nombre = "Oferta", IdArticulo = idArticulo, PrecioUnitario = precioUnitario,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Ofertas.Add(oferta);
        await db.SaveChangesAsync();

        return oferta.Id;
    }

    public async Task<List<ItemComprobanteVenta>> ItemsAsync(Escenario ctx, int idComprobante)
    {
        await using var db = Db(ctx.IdTenant);

        return await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == idComprobante)
            .OrderBy(i => i.Orden)
            .ToListAsync();
    }
}
