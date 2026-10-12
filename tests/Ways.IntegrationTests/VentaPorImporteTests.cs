using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Venta de un artículo por importe (<see cref="UnidadVenta.Importe"/>) en <c>POST /api/ventas</c>, en la vista previa
/// de precios, en el escaneo y en la instantánea del POS: cantidad 1 al importe que tipea el cajero, sin lista ni ofertas,
/// mezclable con líneas de lista, online y offline. Mismo trámite de siembra que <c>VentaAjusteManualTests</c> — no se
/// comparte helper entre archivos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentaPorImporteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, int IdTurno, HttpClient Cliente, int IdMedioEfectivo);

    // ---- siembra ---------------------------------------------------------------------------

    private async Task<(int IdTenant, int IdPuntoVenta, int IdUsuarioAdmin, HttpClient Admin)> AprovisionarAsync(
        string nombre, ModoPuntoVenta modo)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants", new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, modo));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin);
    }

    private async Task<(int IdTurno, int IdMedioEfectivo)> AbrirTurnoYBuscarEfectivoAsync(
        int idTenant, int idPuntoVenta, int idEmpleadoApertura)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleadoApertura,
            FechaApertura = ahora,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        return (turno.Id, idMedioEfectivo);
    }

    /// <summary>Tenant en modo Web: el admin vende directo, sin dispositivo ni número pre-asignado.</summary>
    private async Task<Contexto> PrepararOnlineAsync(string nombre)
    {
        var (idTenant, idPuntoVenta, idUsuarioAdmin, admin) = await AprovisionarAsync(nombre, ModoPuntoVenta.Web);
        var (idTurno, idMedioEfectivo) = await AbrirTurnoYBuscarEfectivoAsync(idTenant, idPuntoVenta, idUsuarioAdmin);
        return new Contexto(idTenant, idPuntoVenta, idTurno, admin, idMedioEfectivo);
    }

    /// <summary>Tenant en modo Escritorio: vende un cajero de dispositivo, con un bloque de
    /// numeración ya reservado (el primer número pre-asignado es el 1).</summary>
    private async Task<Contexto> PrepararOfflineAsync(string nombre)
    {
        var (idTenant, idPuntoVenta, idUsuarioAdmin, admin) = await AprovisionarAsync(nombre, ModoPuntoVenta.Escritorio);
        var (idTurno, idMedioEfectivo) = await AbrirTurnoYBuscarEfectivoAsync(idTenant, idPuntoVenta, idUsuarioAdmin);

        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, $"Caja {nombre}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valorCookie = setCookie[prefijo.Length..];
        var cookieDispositivo = valorCookie[..valorCookie.IndexOf(';')];
        admin.Dispose();

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = "cajero",
                Mail = $"cajero-{idTenant}@ways.test",
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
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        Assert.Equal(HttpStatusCode.OK, (await cajero.SendAsync(solicitud)).StatusCode);

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        return new Contexto(idTenant, idPuntoVenta, idTurno, cajero, idMedioEfectivo);
    }


    /// <summary>Artículo (sin stock) de la unidad pedida; <paramref name="precio"/> <c>null</c> lo deja sin fila en
    /// <c>precios</c>. Un artículo por importe nace sin acumulación.</summary>
    private async Task<(int IdArticulo, string Codigo)> SembrarAsync(int idTenant, UnidadVenta unidad, decimal? precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = idTenant, Nombre = $"Ventas-{Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"a{Guid.NewGuid():N}"[..6],
            Nombre = unidad == UnidadVenta.Importe ? "Fiambrería" : "Servicio de prueba",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = unidad,
            AcumulaEnVenta = unidad != UnidadVenta.Importe,
            Activo = true,
            EsProducto = false,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (precio is { } monto)
        {
            db.Precios.Add(new Precio
            {
                IdTenant = idTenant,
                IdArticulo = articulo.Id,
                IdListaPrecio = idListaGeneral,
                Monto = monto,
                VigenteDesde = ahora.AddDays(-1),
                VigenteHasta = null,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        return (articulo.Id, articulo.CodigoInterno);
    }

    private async Task SembrarOfertaDePorcentajeAsync(int idTenant, int idArticulo, decimal porcentaje)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        db.Ofertas.Add(new Oferta
        {
            IdTenant = idTenant,
            Nombre = "Mitad de precio",
            IdArticulo = idArticulo,
            Porcentaje = porcentaje,
            Prioridad = 1,
            Acumulable = false,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeVenta VentaOnline(Contexto ctx, decimal pagado, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, lineas,
            [new PagoDeVenta(ctx.IdMedioEfectivo, pagado, null, 0m)], null, null);

    private static SolicitudDeVenta VentaOffline(Contexto ctx, decimal pagado, long numero, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, lineas,
            [new PagoDeVenta(ctx.IdMedioEfectivo, pagado, null, 0m)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: numero);

    private static SolicitudDeVenta Devolucion(Contexto ctx, int? idAsociado, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "NCX", idAsociado, lineas, [], null, null);

    private static async Task<ComprobanteEmitido> EmitirOkAsync(HttpClient cliente, SolicitudDeVenta solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeRechazoAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString()!;
    }

    private static async Task<string> CodigoDeRechazoAsync(HttpClient cliente, SolicitudDeVenta solicitud) =>
        await CodigoDeRechazoAsync(await cliente.PostAsJsonAsync("/api/ventas", solicitud));

    private async Task<int> ContarComprobantesAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ComprobantesVenta.CountAsync();
    }

    private async Task<List<ItemComprobanteVenta>> LeerItemsAsync(int idTenant, int idComprobante)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ItemsComprobanteVenta.AsNoTracking()
            .Where(i => i.IdComprobanteVenta == idComprobante).OrderBy(i => i.Orden).ToListAsync();
    }

    private async Task<int> ContarDiscrepanciasAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.Auditoria.CountAsync(a => a.IdTenant == idTenant && a.Accion == "venta.discrepancia");
    }

    private async Task<int> ListaPorDefectoAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
    }

    // ---- venta online --------------------------------------------------------------------------

    /// <summary>El ejemplo del dueño: 150 de fiambrería junto a 2 unidades de lista. La línea por importe sale con
    /// cantidad 1, precio unitario 150, total 150, sin oferta; la otra, de siempre. Lo mismo en la base.</summary>
    [Fact]
    public async Task UnaVentaOnlineMezclaUnaLineaPorImporteConUnaDeLista()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnaVentaOnlineMezclaUnaLineaPorImporteConUnaDeLista));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(ctx, 350m, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m), new LineaDeVenta(comun, 2m, null)));

        Assert.Equal(350m, emitido.Total);
        var porImporte = emitido.Items[0];
        Assert.Equal(
            (fiambreria, 1m, 150m, 0m, 150m, (int?)null),
            (porImporte.IdArticulo!.Value, porImporte.Cantidad, porImporte.PrecioUnitario, porImporte.Descuento,
                porImporte.Total, porImporte.IdOferta));
        Assert.Equal(
            (comun, 2m, 100m, 200m),
            (emitido.Items[1].IdArticulo!.Value, emitido.Items[1].Cantidad, emitido.Items[1].PrecioUnitario, emitido.Items[1].Total));

        var guardados = await LeerItemsAsync(ctx.IdTenant, emitido.Id);
        Assert.Equal(
            [(1m, 150m, 150m), (2m, 100m, 200m)], guardados.Select(i => (i.Cantidad, i.PrecioUnitario, i.Total)).ToList());
    }

    /// <summary>Tipearlo dos veces da dos líneas: el artículo por importe no acumula.</summary>
    [Fact]
    public async Task ElMismoArticuloPorImporteDosVecesDaDosLineasConSuPropioImporte()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElMismoArticuloPorImporteDosVecesDaDosLineasConSuPropioImporte));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(
                ctx, 230m,
                new LineaDeVenta(fiambreria, 1m, null, Importe: 150m), new LineaDeVenta(fiambreria, 1m, null, Importe: 80m)));

        Assert.Equal([150m, 80m], emitido.Items.Select(i => i.Total).ToList());
        Assert.Equal(230m, emitido.Total);
    }

    /// <summary>El importe tipeado manda sobre un precio de lista y sobre una oferta del artículo.</summary>
    [Fact]
    public async Task ElImporteTipeadoIgnoraElPrecioDeListaYLasOfertas()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElImporteTipeadoIgnoraElPrecioDeListaYLasOfertas));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: 999m);
        await SembrarOfertaDePorcentajeAsync(ctx.IdTenant, fiambreria, 50m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 150m, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        var item = Assert.Single(emitido.Items);
        Assert.Equal((150m, 0m, 150m, (int?)null), (item.PrecioUnitario, item.Descuento, item.Total, item.IdOferta));
    }

    /// <summary>El ajuste manual se aplica sobre el importe como sobre cualquier línea (-10 % de 150 = 135).</summary>
    [Fact]
    public async Task ElAjusteManualSeAplicaSobreElImporte()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElAjusteManualSeAplicaSobreElImporte));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(ctx, 135m, new LineaDeVenta(fiambreria, 1m, null, AjusteManualPorcentaje: -10m, Importe: 150m)));

        var item = Assert.Single(emitido.Items);
        Assert.Equal(
            (150m, -10m, -15m, 135m), (item.PrecioUnitario, item.AjusteManualPorcentaje, item.AjusteManual, item.Total));
        Assert.Equal((150m, 15m, 135m), (emitido.Subtotal, emitido.DescuentoManualTotal, emitido.Total));
    }

    /// <summary>Los totales de venta son de peso entero: un importe con centavos conserva el precio unitario tipeado y
    /// el total de la línea sale redondeado al peso (150,50 cobra 151), como cualquier otra línea calculada.</summary>
    [Fact]
    public async Task UnImporteConCentavosConservaElPrecioUnitarioYElTotalDeLineaVaAlPesoEntero()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnImporteConCentavosConservaElPrecioUnitarioYElTotalDeLineaVaAlPesoEntero));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var emitido = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 151m, new LineaDeVenta(fiambreria, 1m, null, Importe: 150.50m)));

        var item = Assert.Single(emitido.Items);
        Assert.Equal((150.50m, 0m, 151m), (item.PrecioUnitario, item.Descuento, item.Total));
        Assert.Equal(151m, emitido.Total);
    }

    /// <summary>La falta de precio de lista solo frena a un artículo que se vende por lista.</summary>
    [Fact]
    public async Task UnArticuloPorImporteNoNecesitaPrecioEnNingunaLista()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnArticuloPorImporteNoNecesitaPrecioEnNingunaLista));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (sinPrecio, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, precio: null);

        await EmitirOkAsync(ctx.Cliente, VentaOnline(ctx, 10m, new LineaDeVenta(fiambreria, 1m, null, Importe: 10m)));

        Assert.Equal(
            "articulo_sin_precio_vigente",
            await CodigoDeRechazoAsync(ctx.Cliente, VentaOnline(ctx, 10m, new LineaDeVenta(sinPrecio, 1m, null))));
    }

    [Fact]
    public async Task UnaLineaPorImporteMalArmadaSeRechazaYNoDejaNada()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnaLineaPorImporteMalArmadaSeRechazaYNoDejaNada));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);

        // Un importe sobre un artículo que no se vende por importe no se ignora en silencio.
        Assert.Equal(
            "importe_no_admitido",
            await CodigoDeRechazoAsync(ctx.Cliente, VentaOnline(ctx, 100m, new LineaDeVenta(comun, 1m, null, Importe: 100m))));
        // El artículo por importe sin su importe: nadie le inventa un precio.
        Assert.Equal(
            "importe_requerido",
            await CodigoDeRechazoAsync(ctx.Cliente, VentaOnline(ctx, 100m, new LineaDeVenta(fiambreria, 1m, null))));
        foreach (var invalido in new[] { 0m, -5m, 150.005m, 1_000_000_000_000m })
        {
            Assert.Equal(
                "importe_invalido",
                await CodigoDeRechazoAsync(
                    ctx.Cliente, VentaOnline(ctx, 100m, new LineaDeVenta(fiambreria, 1m, null, Importe: invalido))));
        }

        // La cantidad de una línea por importe es siempre 1.
        Assert.Equal(
            "cantidad_importe_invalida",
            await CodigoDeRechazoAsync(
                ctx.Cliente, VentaOnline(ctx, 300m, new LineaDeVenta(fiambreria, 2m, null, Importe: 150m))));
        // El precio de línea es del dispositivo offline: junto a un importe no tiene destino.
        Assert.Equal(
            "importe_con_precio_no_admitido",
            await CodigoDeRechazoAsync(
                ctx.Cliente,
                VentaOnline(ctx, 150m, new LineaDeVenta(fiambreria, 1m, null, PrecioUnitario: 150m, Importe: 150m))));

        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    // ---- vista previa --------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/ofertas/resolver", false)]
    [InlineData("/api/ofertas/resolver", true)]
    [InlineData("/api/ofertas/resolver-con-combos", false)]
    public async Task LaVistaPreviaDevuelveElImporteSinOfertasNiPrecioDeLista(string ruta, bool porLinea)
    {
        var ctx = await PrepararOnlineAsync(nameof(LaVistaPreviaDevuelveElImporteSinOfertasNiPrecioDeLista) + ruta.Length + porLinea);
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: 999m);
        await SembrarOfertaDePorcentajeAsync(ctx.IdTenant, fiambreria, 50m);
        var idLista = await ListaPorDefectoAsync(ctx.IdTenant);

        var respuesta = await ctx.Cliente.PostAsJsonAsync(ruta, new SolicitudDeResolucion(
            [new LineaDeResolucion(fiambreria, null, idLista, 1m, Importe: 150m)],
            Agregacion: porLinea ? AgregacionDeResolucion.PorLinea : AgregacionDeResolucion.Documento));

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        using var documento = JsonDocument.Parse(cuerpo);
        var linea = (ruta.EndsWith("con-combos") ? documento.RootElement.GetProperty("lineas") : documento.RootElement)[0];
        Assert.Equal(150m, linea.GetProperty("precioOriginal").GetDecimal());
        Assert.Equal(150m, linea.GetProperty("precioFinal").GetDecimal());
        Assert.Equal(0m, linea.GetProperty("descuentoUnitario").GetDecimal());
        Assert.Empty(linea.GetProperty("aplicadas").EnumerateArray());
    }

    [Fact]
    public async Task LaVistaPreviaRechazaUnImporteQueNoCorrespondeAlArticuloOALaCantidad()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaVistaPreviaRechazaUnImporteQueNoCorrespondeAlArticuloOALaCantidad));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);
        var idLista = await ListaPorDefectoAsync(ctx.IdTenant);

        async Task<string> CodigoAsync(LineaDeResolucion linea) => await CodigoDeRechazoAsync(
            await ctx.Cliente.PostAsJsonAsync("/api/ofertas/resolver", new SolicitudDeResolucion([linea])));

        Assert.Equal("importe_no_admitido", await CodigoAsync(new LineaDeResolucion(comun, null, idLista, 1m, Importe: 100m)));
        Assert.Equal("importe_invalido", await CodigoAsync(new LineaDeResolucion(fiambreria, null, idLista, 1m, Importe: 0m)));
        Assert.Equal(
            "cantidad_importe_invalida", await CodigoAsync(new LineaDeResolucion(fiambreria, null, idLista, 3m, Importe: 10m)));
    }

    // ---- venta offline -------------------------------------------------------------------------

    [Fact]
    public async Task UnaVentaOfflineSoloDeLineasPorImporteSeAceptaSinPrecioDelDispositivo()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaVentaOfflineSoloDeLineasPorImporteSeAceptaSinPrecioDelDispositivo));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (verduleria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 200m, 1,
                new LineaDeVenta(fiambreria, 1m, null, Importe: 150m), new LineaDeVenta(verduleria, 1m, null, Importe: 50m)));

        Assert.Equal((1L, 200m), (emitido.Numero, emitido.Total));
        Assert.Equal([150m, 50m], emitido.Items.Select(i => i.Total).ToList());
        Assert.Equal(0, await ContarDiscrepanciasAsync(ctx.IdTenant));
    }

    /// <summary>Mezclada con líneas de lista, la línea por importe no entra en el "todas o ninguna" del precio offline:
    /// las de lista traen su precio, la de importe solo su importe, y el servidor no la marca como discrepancia.</summary>
    [Fact]
    public async Task UnaVentaOfflineMixtaTraePrecioEnLasLineasDeListaYSoloImporteEnLaOtra()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaVentaOfflineMixtaTraePrecioEnLasLineasDeListaYSoloImporteEnLaOtra));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(
                ctx, 350m, 1,
                new LineaDeVenta(comun, 2m, null, PrecioUnitario: 100m, DescuentoUnitario: 0m),
                new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        Assert.Equal([200m, 150m], emitido.Items.Select(i => i.Total).ToList());
        Assert.Equal(350m, emitido.Total);
        Assert.Equal(0, await ContarDiscrepanciasAsync(ctx.IdTenant));
    }

    [Fact]
    public async Task EnUnaVentaOfflineLasLineasDeListaSiguenSiendoTodasONinguna()
    {
        var ctx = await PrepararOfflineAsync(nameof(EnUnaVentaOfflineLasLineasDeListaSiguenSiendoTodasONinguna));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);

        Assert.Equal(
            "precio_offline_incompleto",
            await CodigoDeRechazoAsync(
                ctx.Cliente,
                VentaOffline(
                    ctx, 350m, 1,
                    new LineaDeVenta(comun, 1m, null, PrecioUnitario: 100m),
                    new LineaDeVenta(comun, 1m, null),
                    new LineaDeVenta(fiambreria, 1m, null, Importe: 150m))));
        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>Una venta que el dispositivo ya cobró con un precio de lista no se rechaza porque el artículo pasó a
    /// venderse por importe después: el precio que cobró es el del ticket que el cliente tiene en la mano.</summary>
    [Fact]
    public async Task UnaLineaOfflineCobradaConPrecioDeListaDeUnArticuloQueAhoraEsPorImporteSeHonra()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaLineaOfflineCobradaConPrecioDeListaDeUnArticuloQueAhoraEsPorImporteSeHonra));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaOffline(ctx, 120m, 1, new LineaDeVenta(fiambreria, 1m, null, PrecioUnitario: 120m, DescuentoUnitario: 0m)));

        var item = Assert.Single(emitido.Items);
        Assert.Equal((120m, 120m), (item.PrecioUnitario, item.Total));
    }

    /// <summary>Un precio de lista sin número pre-asignado sigue rechazado aunque la venta traiga también una línea
    /// por importe.</summary>
    [Fact]
    public async Task UnPrecioDeListaSinNumeroPreasignadoSigueRechazadoAunqueHayaUnaLineaPorImporte()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnPrecioDeListaSinNumeroPreasignadoSigueRechazadoAunqueHayaUnaLineaPorImporte));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);

        Assert.Equal(
            "precio_offline_no_admitido",
            await CodigoDeRechazoAsync(
                ctx.Cliente,
                VentaOnline(
                    ctx, 250m,
                    new LineaDeVenta(comun, 1m, null, PrecioUnitario: 100m),
                    new LineaDeVenta(fiambreria, 1m, null, Importe: 150m))));
    }

    /// <summary>Una línea por importe no tiene combo: los datos de combo del dispositivo no tienen destino.</summary>
    [Fact]
    public async Task UnaLineaPorImporteOfflineConDatosDeComboSeRechaza()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaLineaPorImporteOfflineConDatosDeComboSeRechaza));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        Assert.Equal(
            "combo_offline_no_admitido",
            await CodigoDeRechazoAsync(
                ctx.Cliente,
                VentaOffline(
                    ctx, 150m, 1,
                    new LineaDeVenta(
                        fiambreria, 1m, null, UnidadesConsumidasPorCombo: 1, AhorroDeCombo: 0m, IdCombo: 1, Importe: 150m))));
    }

    // ---- devolución ----------------------------------------------------------------------------

    /// <summary>La nota de crédito asociada devuelve la línea por el importe con que se vendió.</summary>
    [Fact]
    public async Task LaDevolucionDeUnaLineaPorImporteVuelveAlImporteOriginal()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionDeUnaLineaPorImporteVuelveAlImporteOriginal));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: 999m);
        var venta = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(
                ctx, 230m,
                new LineaDeVenta(fiambreria, 1m, null, Importe: 150m), new LineaDeVenta(fiambreria, 1m, null, Importe: 80m)));

        var nota = await EmitirOkAsync(
            ctx.Cliente, Devolucion(ctx, venta.Id, new LineaDeVenta(fiambreria, 1m, null, Importe: 80m)));

        var item = Assert.Single(nota.Items);
        Assert.Equal((-1m, 80m, -80m), (item.Cantidad, item.PrecioUnitario, item.Total));
        Assert.Equal(-80m, nota.Total);
    }

    [Fact]
    public async Task LaDevolucionAsociadaPorUnImporteQueNoSeVendioSeRechaza()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionAsociadaPorUnImporteQueNoSeVendioSeRechaza));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var venta = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 150m, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente, Devolucion(ctx, venta.Id, new LineaDeVenta(fiambreria, 1m, null, Importe: 100m))));
    }

    /// <summary>El importe tiene que ser el de una línea del MISMO artículo: otro artículo vendido por el mismo monto no
    /// alcanza, y una línea que ya es una devolución (cantidad negativa) tampoco es una venta que devolver.</summary>
    [Fact]
    public async Task LaDevolucionNoSeApoyaEnOtroArticuloNiEnUnaLineaDeNotaDeCredito()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionNoSeApoyaEnOtroArticuloNiEnUnaLineaDeNotaDeCredito));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (verduleria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var venta = await EmitirOkAsync(
            ctx.Cliente, VentaOnline(ctx, 150m, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente, Devolucion(ctx, venta.Id, new LineaDeVenta(verduleria, 1m, null, Importe: 150m))));

        // El monto de una línea devuelta vendida no cubre a otra línea de la misma nota que no se vendió.
        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente,
                Devolucion(
                    ctx, venta.Id,
                    new LineaDeVenta(fiambreria, 1m, null, Importe: 150m), new LineaDeVenta(verduleria, 1m, null, Importe: 150m))));

        var nota = await EmitirOkAsync(
            ctx.Cliente, Devolucion(ctx, venta.Id, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));
        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente, Devolucion(ctx, nota.Id, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m))));
    }

    /// <summary>La comparación con la venta asociada es solo de las líneas por importe: una línea de lista de la misma nota
    /// se devuelve como siempre.</summary>
    [Fact]
    public async Task LaDevolucionAsociadaConUnaLineaDeListaYUnaPorImporteSoloComparaLaSegunda()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionAsociadaConUnaLineaDeListaYUnaPorImporteSoloComparaLaSegunda));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);
        var venta = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(ctx, 250m, new LineaDeVenta(comun, 1m, null), new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        var nota = await EmitirOkAsync(
            ctx.Cliente,
            Devolucion(ctx, venta.Id, new LineaDeVenta(comun, 1m, null), new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        Assert.Equal([-100m, -150m], nota.Items.Select(i => i.Total).ToList());
    }

    /// <summary>Una devolución offline ya se entregó en la caja: no se rechaza por no coincidir con la venta asociada.</summary>
    [Fact]
    public async Task UnaDevolucionOfflineNoSeRechazaPorElImporteDeLaVentaAsociada()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaDevolucionOfflineNoSeRechazaPorElImporteDeLaVentaAsociada));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var venta = await EmitirOkAsync(
            ctx.Cliente, VentaOffline(ctx, 150m, 1, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));
        var reserva = await ctx.Cliente.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(ctx.IdPuntoVenta, "NCX", 5));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        var nota = await EmitirOkAsync(
            ctx.Cliente,
            new SolicitudDeVenta(
                ctx.IdPuntoVenta, null, "NCX", venta.Id, [new LineaDeVenta(fiambreria, 1m, null, Importe: 100m)], [], null, null,
                IdPresupuestoOrigen: null, NumeroPreasignado: 1));

        Assert.Equal(-100m, nota.Total);
    }

    [Fact]
    public async Task LaDevolucionSinComprobanteAsociadoTomaElImporteTipeado()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionSinComprobanteAsociadoTomaElImporteTipeado));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var nota = await EmitirOkAsync(
            ctx.Cliente, Devolucion(ctx, null, new LineaDeVenta(fiambreria, 1m, null, Importe: 95m)));

        Assert.Equal(-95m, nota.Total);
        Assert.Equal(
            "importe_requerido",
            await CodigoDeRechazoAsync(ctx.Cliente, Devolucion(ctx, null, new LineaDeVenta(fiambreria, 1m, null))));
    }

    // ---- escaneo -------------------------------------------------------------------------------

    private static async Task<JsonElement> EscanearAsync(HttpClient cliente, string entrada)
    {
        var respuesta = await cliente.GetAsync($"/api/articulos/escaneo?entrada={Uri.EscapeDataString(entrada)}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.Clone();
    }

    [Fact]
    public async Task ElEscaneoDeUnArticuloPorImporteSeparaElImporteDeLaCantidad()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElEscaneoDeUnArticuloPorImporteSeparaElImporteDeLaCantidad));
        var (fiambreria, codigo) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var conImporte = await EscanearAsync(ctx.Cliente, $"150*{codigo}");
        Assert.Equal(
            (fiambreria, 1m, 150m),
            (conImporte.GetProperty("idArticulo").GetInt32(), conImporte.GetProperty("cantidad").GetDecimal(),
                conImporte.GetProperty("importe").GetDecimal()));

        var conCentavos = await EscanearAsync(ctx.Cliente, $"150.5*{codigo}");
        Assert.Equal(150.5m, conCentavos.GetProperty("importe").GetDecimal());

        // El código pelado trae el artículo sin importe: el cliente lo pide (D4).
        var pelado = await EscanearAsync(ctx.Cliente, codigo);
        Assert.Equal(1m, pelado.GetProperty("cantidad").GetDecimal());
        Assert.False(pelado.TryGetProperty("importe", out _));
    }

    /// <summary>Un prefijo ilegible, cero, negativo, con coma o con más de dos decimales no se degrada a "cantidad 1 sin
    /// importe" para un artículo por importe: se rechaza, para que ningún cliente cobre un monto distinto del tipeado.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("150,50")]
    [InlineData("150.505")]
    public async Task UnPrefijoQueNoEsUnImporteValidoSeRechazaEnUnArticuloPorImporte(string prefijo)
    {
        var ctx = await PrepararOnlineAsync(
            nameof(UnPrefijoQueNoEsUnImporteValidoSeRechazaEnUnArticuloPorImporte) + prefijo.Length + prefijo.Count(char.IsDigit));
        var (_, codigo) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        var respuesta = await ctx.Cliente.GetAsync(
            $"/api/articulos/escaneo?entrada={Uri.EscapeDataString($"{prefijo}*{codigo}")}");

        Assert.Equal("importe_invalido", await CodigoDeRechazoAsync(respuesta));
    }

    /// <summary>Para un artículo común el escaneo es el de siempre: el prefijo es la cantidad, uno ilegible cae a 1 y no
    /// aparece ningún campo nuevo en la respuesta.</summary>
    [Fact]
    public async Task ElEscaneoDeUnArticuloComunNoCambia()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElEscaneoDeUnArticuloComunNoCambia));
        var (_, codigo) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);

        var tres = await EscanearAsync(ctx.Cliente, $"3*{codigo}");
        var ilegible = await EscanearAsync(ctx.Cliente, $"abc*{codigo}");

        Assert.Equal(3m, tres.GetProperty("cantidad").GetDecimal());
        Assert.Equal(1m, ilegible.GetProperty("cantidad").GetDecimal());
        Assert.False(tres.TryGetProperty("importe", out _));
        Assert.False(ilegible.TryGetProperty("importe", out _));
    }

    // ---- instantánea del POS -------------------------------------------------------------------

    [Fact]
    public async Task LaInstantaneaEmiteElArticuloPorImporteSinPreciosYConservaLosDeLosDemas()
    {
        var ctx = await PrepararOfflineAsync(nameof(LaInstantaneaEmiteElArticuloPorImporteSinPreciosYConservaLosDeLosDemas));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var (conPrecioViejo, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: 999m);
        var (comun, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, 100m);
        var (sinPrecio, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, precio: null);

        var respuesta = await ctx.Cliente.GetAsync("/api/pos/instantanea?version=2");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.IsSuccessStatusCode, cuerpo);
        var articulos = JsonDocument.Parse(cuerpo).RootElement.GetProperty("articulos").EnumerateArray()
            .ToDictionary(a => a.GetProperty("idArticulo").GetInt32());

        Assert.Empty(articulos[fiambreria].GetProperty("preciosPorLista").EnumerateArray());
        Assert.Equal("Importe", articulos[fiambreria].GetProperty("unidadVenta").GetString());
        // Un precio de lista que el artículo por importe todavía conserve no viaja: el dispositivo no lo usa.
        Assert.Empty(articulos[conPrecioViejo].GetProperty("preciosPorLista").EnumerateArray());
        Assert.Single(articulos[comun].GetProperty("preciosPorLista").EnumerateArray());
        Assert.DoesNotContain(sinPrecio, articulos.Keys);
    }

    // ---- presupuestos y remitos ----------------------------------------------------------------

    [Fact]
    public async Task UnPresupuestoYUnRemitoRechazanUnArticuloPorImporte()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnPresupuestoYUnRemitoRechazanUnArticuloPorImporte));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: 999m);

        var presupuesto = await ctx.Cliente.PostAsJsonAsync(
            "/api/presupuestos",
            new SolicitudDePresupuesto(ctx.IdPuntoVenta, null, null, [new LineaDePresupuesto(fiambreria, 1m)]));
        var remito = await ctx.Cliente.PostAsJsonAsync(
            "/api/remitos",
            new SolicitudDeRemito(ctx.IdPuntoVenta, null, null, null, [new LineaDeRemito(fiambreria, 1m, null)]));

        Assert.Equal("articulo_por_importe_no_admitido", await CodigoDeRechazoAsync(presupuesto));
        Assert.Equal("articulo_por_importe_no_admitido", await CodigoDeRechazoAsync(remito));
    }

    // ---- identidad del reenvío y devolución con ajuste -----------------------------------------

    /// <summary>El importe tipeado es la identidad de una línea por importe: el reenvío bajo el mismo número pre-asignado con
    /// otro importe es otra venta (409); con el mismo, el mismo comprobante. El pago se mantiene igual (200, con otro vuelto)
    /// para que el importe sea lo único que distingue los dos pedidos.</summary>
    [Fact]
    public async Task ElReenvioBajoElMismoNumeroConOtroImporteEsOtraVentaYConElMismoEsElMismoComprobante()
    {
        var ctx = await PrepararOfflineAsync(nameof(ElReenvioBajoElMismoNumeroConOtroImporteEsOtraVentaYConElMismoEsElMismoComprobante));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);

        SolicitudDeVenta Reenvio(decimal importe) => new(
            ctx.IdPuntoVenta, null, "TX", null, [new LineaDeVenta(fiambreria, 1m, null, Importe: importe)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 200m, null, 200m - importe)], null, null,
            IdPresupuestoOrigen: null, NumeroPreasignado: 1);

        var primera = await EmitirOkAsync(ctx.Cliente, Reenvio(150m));

        var otroImporte = await ctx.Cliente.PostAsJsonAsync("/api/ventas", Reenvio(100m));
        Assert.Equal(HttpStatusCode.Conflict, otroImporte.StatusCode);
        var problema = await otroImporte.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_preasignado_con_otro_contenido", problema.GetProperty("codigo").GetString());

        var mismoImporte = await EmitirOkAsync(ctx.Cliente, Reenvio(150m));
        Assert.Equal(primera.Id, mismoImporte.Id);
        Assert.Equal(1, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>Una venta de 150 con −50 % cobró 75: devolver 150 sin el ajuste reembolsaría más de lo cobrado, así que la
    /// línea devuelta tiene que coincidir con la vendida en artículo, importe Y porcentaje de ajuste manual.</summary>
    [Fact]
    public async Task LaDevolucionPorImporteTieneQueCoincidirTambienEnElAjusteManualDeLaVenta()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionPorImporteTieneQueCoincidirTambienEnElAjusteManualDeLaVenta));
        var (fiambreria, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Importe, precio: null);
        var venta = await EmitirOkAsync(
            ctx.Cliente,
            VentaOnline(ctx, 75m, new LineaDeVenta(fiambreria, 1m, null, AjusteManualPorcentaje: -50m, Importe: 150m)));
        Assert.Equal(75m, venta.Total);

        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente, Devolucion(ctx, venta.Id, new LineaDeVenta(fiambreria, 1m, null, Importe: 150m))));
        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente,
                Devolucion(ctx, venta.Id, new LineaDeVenta(fiambreria, 1m, null, AjusteManualPorcentaje: -25m, Importe: 150m))));

        var nota = await EmitirOkAsync(
            ctx.Cliente,
            Devolucion(ctx, venta.Id, new LineaDeVenta(fiambreria, 1m, null, AjusteManualPorcentaje: -50m, Importe: 150m)));
        Assert.Equal(-75m, nota.Total);
    }

    /// <summary>Lo que cuenta es cómo se vendió la línea, no el monto: una línea de lista de un artículo que después pasó a
    /// venderse por importe no es una línea por importe que devolver, aunque su precio coincida.</summary>
    [Fact]
    public async Task LaDevolucionPorImporteNoSeApoyaEnUnaLineaVendidaDeLista()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDevolucionPorImporteNoSeApoyaEnUnaLineaVendidaDeLista));
        var (articulo, _) = await SembrarAsync(ctx.IdTenant, UnidadVenta.Unidad, precio: 150m);
        var venta = await EmitirOkAsync(ctx.Cliente, VentaOnline(ctx, 150m, new LineaDeVenta(articulo, 1m, null)));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var entidad = await db.Articulos.SingleAsync(a => a.Id == articulo);
            entidad.UnidadVenta = UnidadVenta.Importe;
            entidad.AcumulaEnVenta = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            "importe_devolucion_inexistente",
            await CodigoDeRechazoAsync(
                ctx.Cliente, Devolucion(ctx, venta.Id, new LineaDeVenta(articulo, 1m, null, Importe: 150m))));
    }
}
