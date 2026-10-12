using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Combos;
using Ways.Application.Fiscal;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Combos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Combos aplicados a una venta ONLINE (<c>POST /api/ventas</c>) de punta a punta contra Postgres real: la venta
/// conserva las líneas de los artículos reales y el combo es un descuento repartido entre ellas, así que stock, IVA
/// y anulación siguen funcionando por artículo. Los combos se siembran directo por EF (el ABM tiene sus propias
/// pruebas en <see cref="CombosEndpointsTests"/>); eso permite también armar estados que el ABM no deja crear, como
/// un componente que después pasó a ser pesable.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdCliente);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{unico}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

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

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Combos", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });

        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente Combos",
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            IdListaPrecio = lista.Id, LimiteCredito = 0, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, cliente.Id);
    }

    private WaysDbContext Db(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private async Task<int> ArticuloAsync(
        Contexto ctx, string nombre, decimal precio, UnidadVenta unidad = UnidadVenta.Unidad, int? idFamilia = null,
        int? idAlicuotaIva = null, bool controlaLote = false)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = idAlicuotaIva ?? ctx.IdAlicuotaIva, UnidadVenta = unidad,
            IdFamilia = idFamilia, EsProducto = true, ControlaLote = controlaLote, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> FamiliaAsync(Contexto ctx, string nombre)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var familia = new Familia { IdTenant = ctx.IdTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }

    private async Task<int> OtraEmpresaAsync(Contexto ctx)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa.Id;
    }

    private static (int? IdArticulo, int? IdFamilia, decimal Cantidad) Art(int id, decimal cantidad = 1m) =>
        (id, null, cantidad);

    private static (int? IdArticulo, int? IdFamilia, decimal Cantidad) Fam(int id, decimal cantidad = 1m) =>
        (null, id, cantidad);

    private async Task<int> ComboAsync(
        Contexto ctx, decimal precio, IReadOnlyList<(int? IdArticulo, int? IdFamilia, decimal Cantidad)> componentes,
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

    private async Task<int> OfertaDePrecioAsync(Contexto ctx, int idArticulo, decimal precioUnitario)
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

    private static SolicitudDeVenta Venta(
        Contexto ctx, decimal total, params (int IdArticulo, decimal Cantidad)[] lineas) =>
        new(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [.. lineas.Select(l => new LineaDeVenta(l.IdArticulo, l.Cantidad, null))],
            [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)],
            null, null);

    private static SolicitudDeVenta VentaConLineas(Contexto ctx, decimal total, params LineaDeVenta[] lineas) =>
        new(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [.. lineas],
            [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)],
            null, null);

    private async Task HabilitarLotesAsync(Contexto ctx)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;
        db.Parametros.Add(new Parametro
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ComprobanteEmitido> EmitirAsync(Contexto ctx, SolicitudDeVenta solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static Task<ComprobanteEmitido> EmitirAsync(
        Contexto ctx, decimal total, params (int IdArticulo, decimal Cantidad)[] lineas) =>
        EmitirAsync(ctx, Venta(ctx, total, lineas));

    private async Task<List<ItemComprobanteVenta>> ItemsAsync(Contexto ctx, int idComprobante)
    {
        await using var db = Db(ctx.IdTenant);

        return await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == idComprobante)
            .OrderBy(i => i.Orden)
            .ToListAsync();
    }

    private async Task<decimal> StockAsync(Contexto ctx, int idArticulo)
    {
        await using var db = Db(ctx.IdTenant);

        return await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync();
    }

    // =================================================================================================
    // El ejemplo del dueño
    // =================================================================================================

    [Fact]
    public async Task UnPanchoYUnaCocaConElComboCuestanElPrecioDelComboYLasLineasSiguenSiendoLasDeLosArticulos()
    {
        var ctx = await PrepararAsync(nameof(UnPanchoYUnaCocaConElComboCuestanElPrecioDelComboYLasLineasSiguenSiendoLasDeLosArticulos));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal(5500m, emitido.Subtotal);
        Assert.Equal(500m, emitido.DescuentoTotal);
        Assert.Equal([pancho, coca], emitido.Items.Select(i => i.IdArticulo));
        Assert.Equal([2727m, 2273m], emitido.Items.Select(i => i.Total));
        Assert.Equal([273m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));

        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.Equal([2727m, 2273m], persistidos.Select(i => i.Total));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));

        var releido = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitido.Id}", OpcionesJson);
        Assert.All(releido!.Items, i => Assert.Equal(combo, i.IdCombo));
    }

    [Fact]
    public async Task TresArticulosDeMilConUnComboDeDosMilCobranExactamenteDosMilConLineasEnteras()
    {
        var ctx = await PrepararAsync(nameof(TresArticulosDeMilConUnComboDeDosMilCobranExactamenteDosMilConLineasEnteras));
        var a = await ArticuloAsync(ctx, "a", 1000m);
        var b = await ArticuloAsync(ctx, "b", 1000m);
        var c = await ArticuloAsync(ctx, "c", 1000m);
        var combo = await ComboAsync(ctx, 2000m, [Art(a), Art(b), Art(c)]);

        var emitido = await EmitirAsync(ctx, 2000m, (a, 1m), (b, 1m), (c, 1m));

        Assert.Equal(2000m, emitido.Total);
        Assert.Equal([667m, 667m, 666m], emitido.Items.Select(i => i.Total));
        Assert.Equal([333m, 333m, 334m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
    }

    [Fact]
    public async Task ElComboDescuentaUnaUnidadDeCadaArticuloEnElPuntoDeVentaDeLaVentaConUnMovimientoPorArticulo()
    {
        var ctx = await PrepararAsync(nameof(ElComboDescuentaUnaUnidadDeCadaArticuloEnElPuntoDeVentaDeLaVentaConUnMovimientoPorArticulo));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        Assert.Equal(-1m, await StockAsync(ctx, pancho));
        Assert.Equal(-1m, await StockAsync(ctx, coca));

        await using var db = Db(ctx.IdTenant);
        var movimientos = await db.MovimientosStock
            .Where(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Venta)
            .OrderBy(m => m.IdArticulo)
            .ToListAsync();
        Assert.Equal([pancho, coca], movimientos.Select(m => m.IdArticulo));
        Assert.All(movimientos, m =>
        {
            Assert.Equal(-1m, m.Cantidad);
            Assert.Equal(ctx.IdPuntoVenta, m.IdPuntoVenta);
        });
    }

    [Fact]
    public async Task ElPagoSeValidaContraElTotalConElComboNoContraElDeLista()
    {
        var ctx = await PrepararAsync(nameof(ElPagoSeValidaContraElTotalConElComboNoContraElDeLista));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        // 4500 no cubre ni el total con combo (5000) ni el de lista (5500).
        var corto = await ctx.Admin.PostAsJsonAsync("/api/ventas", Venta(ctx, 4500m, (pancho, 1m), (coca, 1m)));
        Assert.Equal(HttpStatusCode.BadRequest, corto.StatusCode);

        // 5000 cubre justo el total con combo: si el pago se validara contra el de lista, sería insuficiente.
        var conElTotalDelCombo = await ctx.Admin.PostAsJsonAsync("/api/ventas", Venta(ctx, 5000m, (pancho, 1m), (coca, 1m)));
        Assert.Equal(HttpStatusCode.Created, conElTotalDelCombo.StatusCode);
    }

    [Fact]
    public async Task ElIvaDeCadaLineaSaleDeLaAlicuotaDeSuArticuloSobreElTotalYaDescontado()
    {
        var ctx = await PrepararAsync(nameof(ElIvaDeCadaLineaSaleDeLaAlicuotaDeSuArticuloSobreElTotalYaDescontado));
        List<AlicuotaIva> alicuotas;
        await using (var db = Db(ctx.IdTenant))
        {
            alicuotas = await db.AlicuotasIva
                .Where(a => a.CodigoAfip != null && a.Porcentaje > 0)
                .OrderBy(a => a.Porcentaje)
                .ToListAsync();
        }

        var baja = alicuotas.First();
        var alta = alicuotas.Last(a => a.Porcentaje != baja.Porcentaje);
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m, idAlicuotaIva: baja.Id);
        var coca = await ArticuloAsync(ctx, "coca", 2500m, idAlicuotaIva: alta.Id);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        Assert.Equal([baja.Porcentaje, alta.Porcentaje], emitido.Items.Select(i => i.PorcentajeIva));
        Assert.Equal([baja.Id, alta.Id], emitido.Items.Select(i => i.IdAlicuotaIva));

        var lineasFiscales = emitido.Items
            .Select(i => new LineaFiscal(
                i.IdAlicuotaIva, i.IdAlicuotaIva == baja.Id ? baja.Nombre : alta.Nombre,
                i.IdAlicuotaIva == baja.Id ? baja.CodigoAfip : alta.CodigoAfip, i.PorcentajeIva, i.Total))
            .ToList();
        var fiscales = ComposicionDeTotalesFiscales.Componer(lineasFiscales);

        var netoBajo = Math.Round(2727m / (1 + (baja.Porcentaje / 100m)), 2, MidpointRounding.AwayFromZero);
        var netoAlto = Math.Round(2273m / (1 + (alta.Porcentaje / 100m)), 2, MidpointRounding.AwayFromZero);
        Assert.Equal(netoBajo + netoAlto, fiscales.ImpNeto);
        Assert.Equal(5000m - netoBajo - netoAlto, fiscales.ImpIVA);
        Assert.Equal(5000m, fiscales.ImpTotal);
    }

    // =================================================================================================
    // Componentes, cantidades y ofertas
    // =================================================================================================

    [Fact]
    public async Task UnComponenteFamiliaSeSatisfaceConDosMiembrosDistintosYCadaUnoLlevaElCombo()
    {
        var ctx = await PrepararAsync(nameof(UnComponenteFamiliaSeSatisfaceConDosMiembrosDistintosYCadaUnoLlevaElCombo));
        var gaseosas = await FamiliaAsync(ctx, "Gaseosas");
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m, idFamilia: gaseosas);
        var sprite = await ArticuloAsync(ctx, "sprite", 2000m, idFamilia: gaseosas);
        var combo = await ComboAsync(ctx, 7000m, [Art(pancho), Fam(gaseosas, 2m)]);

        var emitido = await EmitirAsync(ctx, 7000m, (pancho, 1m), (coca, 1m), (sprite, 1m));

        Assert.Equal(7000m, emitido.Total);
        Assert.Equal(500m, emitido.DescuentoTotal);
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
        Assert.Equal(-1m, await StockAsync(ctx, coca));
        Assert.Equal(-1m, await StockAsync(ctx, sprite));
    }

    [Fact]
    public async Task DosPanchosYUnaCocaAplicanUnComboYDejanUnPanchoAPrecioDeLista()
    {
        var ctx = await PrepararAsync(nameof(DosPanchosYUnaCocaAplicanUnComboYDejanUnPanchoAPrecioDeLista));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 8000m, (pancho, 2m), (coca, 1m));

        Assert.Equal(8000m, emitido.Total);
        Assert.Equal(500m, emitido.DescuentoTotal);
        Assert.Equal([5727m, 2273m], emitido.Items.Select(i => i.Total));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
        Assert.Equal(-2m, await StockAsync(ctx, pancho));
    }

    [Fact]
    public async Task LaUnidadQueElComboNoConsumioConservaSuOfertaYLaLineaSigueLlevandoSuIdOferta()
    {
        var ctx = await PrepararAsync(nameof(LaUnidadQueElComboNoConsumioConservaSuOfertaYLaLineaSigueLlevandoSuIdOferta));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var oferta = await OfertaDePrecioAsync(ctx, pancho, 2700m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 7700m, (pancho, 2m), (coca, 1m));

        // El pancho consumido va a lista menos su parte (273); el no consumido conserva los 300 de la oferta.
        Assert.Equal(573m, emitido.Items[0].Descuento);
        Assert.Equal(5427m, emitido.Items[0].Total);
        Assert.Equal(oferta, emitido.Items[0].IdOferta);
        Assert.Equal(combo, emitido.Items[0].IdCombo);
        Assert.Equal(2273m, emitido.Items[1].Total);
        Assert.Equal(7700m, emitido.Total);
    }

    [Fact]
    public async Task LaLineaConTodasSusUnidadesConsumidasNoLlevaIdOfertaAunqueElArticuloTuvieraOferta()
    {
        var ctx = await PrepararAsync(nameof(LaLineaConTodasSusUnidadesConsumidasNoLlevaIdOfertaAunqueElArticuloTuvieraOferta));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await OfertaDePrecioAsync(ctx, pancho, 2700m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        Assert.Equal(5000m, emitido.Total);
        Assert.Equal(273m, emitido.Items[0].Descuento);
        Assert.Null(emitido.Items[0].IdOferta);
        Assert.Equal(combo, emitido.Items[0].IdCombo);

        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.Null(persistidos[0].IdOferta);
    }

    [Fact]
    public async Task UnComboQueNoEsMasBaratoQueLasOfertasNoSeAplicaYLasOfertasSiguenIntactas()
    {
        var ctx = await PrepararAsync(nameof(UnComboQueNoEsMasBaratoQueLasOfertasNoSeAplicaYLasOfertasSiguenIntactas));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var oferta = await OfertaDePrecioAsync(ctx, pancho, 2000m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 4500m, (pancho, 1m), (coca, 1m));

        Assert.Equal(4500m, emitido.Total);
        Assert.Equal(1000m, emitido.Items[0].Descuento);
        Assert.Equal(oferta, emitido.Items[0].IdOferta);
        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
    }

    [Fact]
    public async Task UnArticuloPesableConCantidadEnteraNoSeConsumeAunqueElComboLoNombre()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloPesableConCantidadEnteraNoSeConsumeAunqueElComboLoNombre));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var queso = await ArticuloAsync(ctx, "queso", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(queso)]);

        // El ABM valida la unidad de venta al guardar el combo; el artículo pudo cambiar a pesable después.
        await using (var db = Db(ctx.IdTenant))
        {
            await db.Articulos.Where(a => a.Id == queso).ExecuteUpdateAsync(s => s.SetProperty(a => a.UnidadVenta, UnidadVenta.Peso));
        }

        var emitido = await EmitirAsync(ctx, 5500m, (pancho, 1m), (queso, 1m));

        Assert.Equal(5500m, emitido.Total);
        Assert.Equal(0m, emitido.DescuentoTotal);
        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
    }

    // =================================================================================================
    // Qué combos son candidatos
    // =================================================================================================

    [Fact]
    public async Task UnComboDeOtraEmpresaNoSeAplicaPeroUnoDeLaEmpresaDeLaVentaSi()
    {
        var ctx = await PrepararAsync(nameof(UnComboDeOtraEmpresaNoSeAplicaPeroUnoDeLaEmpresaDeLaVentaSi));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var otraEmpresa = await OtraEmpresaAsync(ctx);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)], idEmpresa: otraEmpresa);

        var sinCombo = await EmitirAsync(ctx, 5500m, (pancho, 1m), (coca, 1m));
        Assert.Equal(5500m, sinCombo.Total);
        Assert.All(sinCombo.Items, i => Assert.Null(i.IdCombo));

        var propio = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)], idEmpresa: ctx.IdEmpresa);

        var conCombo = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));
        Assert.Equal(5000m, conCombo.Total);
        Assert.All(conCombo.Items, i => Assert.Equal(propio, i.IdCombo));
    }

    [Fact]
    public async Task UnComboInactivoNoSeAplica()
    {
        var ctx = await PrepararAsync(nameof(UnComboInactivoNoSeAplica));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)], activo: false);

        var emitido = await EmitirAsync(ctx, 5500m, (pancho, 1m), (coca, 1m));

        Assert.Equal(5500m, emitido.Total);
        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
    }

    [Fact]
    public async Task UnComboDadoDeBajaNoSeAplica()
    {
        var ctx = await PrepararAsync(nameof(UnComboDadoDeBajaNoSeAplica));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)], dadoDeBaja: true);

        var emitido = await EmitirAsync(ctx, 5500m, (pancho, 1m), (coca, 1m));

        Assert.Equal(5500m, emitido.Total);
        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
    }

    [Fact]
    public async Task UnComboDadoDeBajaDespuesDeLaVentaNoCambiaLoQueDevuelveLaVentaEmitida()
    {
        var ctx = await PrepararAsync(nameof(UnComboDadoDeBajaDespuesDeLaVentaNoCambiaLoQueDevuelveLaVentaEmitida));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var baja = await ctx.Admin.DeleteAsync($"/api/combos/{combo}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        var releido = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitido.Id}", OpcionesJson);
        Assert.Equal(5000m, releido!.Total);
        Assert.All(releido.Items, i => Assert.Equal(combo, i.IdCombo));
    }

    // =================================================================================================
    // Carga de candidatos: solo los combos que el carrito puede tocar
    // =================================================================================================

    [Fact]
    public async Task ElCargadorNoTraeUnComboSinNingunComponenteEnElCarritoYTraeCompletoElQueTieneUno()
    {
        var ctx = await PrepararAsync(nameof(ElCargadorNoTraeUnComboSinNingunComponenteEnElCarritoYTraeCompletoElQueTieneUno));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var papas = await ArticuloAsync(ctx, "papas", 1000m);
        var gaseosa = await ArticuloAsync(ctx, "gaseosa", 800m);
        var delCarrito = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var ajeno = await ComboAsync(ctx, 1500m, [Art(papas), Art(gaseosa)]);

        await using var db = Db(ctx.IdTenant);
        var candidatos = await ServicioDeCombos.CargarCandidatosParaVentaAsync(db, ctx.IdEmpresa, [pancho], []);

        // El ajeno no figura; el del carrito figura con sus dos componentes aunque el carrito solo tenga el pancho.
        var unico = Assert.Single(candidatos);
        Assert.Equal(delCarrito, unico.Id);
        Assert.DoesNotContain(candidatos, c => c.Id == ajeno);
        Assert.Equal(
            [(pancho, (int?)null, 1m), (coca, (int?)null, 1m)],
            unico.Componentes.Select(c => (c.IdArticulo!.Value, c.IdFamilia, c.Cantidad)));

        Assert.Empty(await ServicioDeCombos.CargarCandidatosParaVentaAsync(db, ctx.IdEmpresa, [], []));
    }

    [Fact]
    public async Task ElCargadorTraeConTodosSusComponentesUnComboQueCoincideSoloPorUnaFamilia()
    {
        var ctx = await PrepararAsync(nameof(ElCargadorTraeConTodosSusComponentesUnComboQueCoincideSoloPorUnaFamilia));
        var gaseosas = await FamiliaAsync(ctx, "Gaseosas");
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m, idFamilia: gaseosas);
        var combo = await ComboAsync(ctx, 4000m, [Art(pancho), Fam(gaseosas, 2m)]);

        await using var db = Db(ctx.IdTenant);
        var candidatos = await ServicioDeCombos.CargarCandidatosParaVentaAsync(db, ctx.IdEmpresa, [coca], [gaseosas]);

        // El carrito no tiene el pancho: el combo entra por la familia y trae igual el componente del pancho.
        var unico = Assert.Single(candidatos);
        Assert.Equal(combo, unico.Id);
        Assert.Equal(2, unico.Componentes.Count);
        Assert.Contains(unico.Componentes, c => c.IdArticulo == pancho && c.IdFamilia is null && c.Cantidad == 1m);
        Assert.Contains(unico.Componentes, c => c.IdArticulo is null && c.IdFamilia == gaseosas && c.Cantidad == 2m);
    }

    // =================================================================================================
    // Anulación, devolución, ventas sin combos
    // =================================================================================================

    [Fact]
    public async Task AnularLaVentaConComboRestauraElStockDeCadaArticuloComponente()
    {
        var ctx = await PrepararAsync(nameof(AnularLaVentaConComboRestauraElStockDeCadaArticuloComponente));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        Assert.Equal(0m, await StockAsync(ctx, pancho));
        Assert.Equal(0m, await StockAsync(ctx, coca));

        await using var db = Db(ctx.IdTenant);
        var inversos = await db.MovimientosStock
            .Where(m => m.IdComprobanteVenta == emitido.Id && m.Motivo == MotivoStock.Anulacion)
            .OrderBy(m => m.IdArticulo)
            .ToListAsync();
        Assert.Equal([pancho, coca], inversos.Select(m => m.IdArticulo));
        Assert.All(inversos, m => Assert.Equal(1m, m.Cantidad));
    }

    [Fact]
    public async Task UnaNotaDeCreditoSinVentaAsociadaNoDetectaCombos()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoSinVentaAsociadaNoDetectaCombos));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "NCX", null,
            [new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(coca, 1m, null)],
            [], null, null);

        var ncx = await EmitirAsync(ctx, solicitud);

        Assert.Equal(-5500m, ncx.Total);
        Assert.All(ncx.Items, i =>
        {
            Assert.Equal(0m, i.Descuento);
            Assert.Null(i.IdCombo);
        });
    }

    [Fact]
    public async Task UnaVentaSinCombosQuedaIgualQueAntes()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaSinCombosQuedaIgualQueAntes));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var otro = await ArticuloAsync(ctx, "otro", 100m);
        await ComboAsync(ctx, 250m, [Art(otro, 3m)]);
        await OfertaDePrecioAsync(ctx, pancho, 2700m);

        var emitido = await EmitirAsync(ctx, 5200m, (pancho, 1m), (coca, 1m));

        Assert.Equal(5200m, emitido.Total);
        Assert.Equal([300m, 0m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.Null(i.IdCombo));
        Assert.NotNull(emitido.Items[0].IdOferta);

        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.All(persistidos, i => Assert.Null(i.IdCombo));
    }

    // =================================================================================================
    // Ajuste manual, lotes y el mismo artículo en dos líneas
    // =================================================================================================

    [Fact]
    public async Task ElAjusteManualSeAplicaSobreElNetoDeLaLineaQueYaIncluyeLaParteDelCombo()
    {
        var ctx = await PrepararAsync(nameof(ElAjusteManualSeAplicaSobreElNetoDeLaLineaQueYaIncluyeLaParteDelCombo));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        // Combo: pancho 3000 − 273 = 2727 y coca 2500 − 227 = 2273. El −10 % de la coca es
        // 2273 × 0.10 = 227.3 → 227 (sobre el neto con el combo, no sobre los 2500 de lista):
        // la línea queda en 2273 − 227 = 2046 y la venta en 2727 + 2046 = 4773.
        var emitido = await EmitirAsync(
            ctx,
            VentaConLineas(
                ctx, 4773m,
                new LineaDeVenta(pancho, 1m, null),
                new LineaDeVenta(coca, 1m, null, AjusteManualPorcentaje: -10m)));

        Assert.Equal(4773m, emitido.Total);
        Assert.Equal(5500m, emitido.Subtotal);
        Assert.Equal(500m, emitido.DescuentoTotal);
        Assert.Equal(227m, emitido.DescuentoManualTotal);
        Assert.Equal([2727m, 2046m], emitido.Items.Select(i => i.Total));
        Assert.Equal([273m, 227m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([0m, -227m], emitido.Items.Select(i => i.AjusteManual));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));

        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.Equal([2727m, 2046m], persistidos.Select(i => i.Total));
        Assert.Equal([0m, -227m], persistidos.Select(i => i.AjusteManual));
    }

    [Fact]
    public async Task UnArticuloConLoteConsumidoPorElComboConservaDescuentoIdComboYLoteYDescuentaElStockUnaSolaVez()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloConLoteConsumidoPorElComboConservaDescuentoIdComboYLoteYDescuentaElStockUnaSolaVez));
        await HabilitarLotesAsync(ctx);
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m, controlaLote: true);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        int idLote;
        await using (var db = Db(ctx.IdTenant))
        {
            var ahora = DateTimeOffset.UtcNow;
            var lote = new Lote
            {
                IdArticulo = pancho, Codigo = "L-COMBO", FechaVencimiento = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(5)),
                EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Lotes.Add(lote);
            await db.SaveChangesAsync();
            idLote = lote.Id;

            db.StockLotes.Add(new StockLote
            {
                IdArticulo = pancho, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = idLote, IdTenant = ctx.IdTenant, Cantidad = 10m
            });
            db.Stock.Add(new Stock
            {
                IdArticulo = pancho, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = 10m
            });
            await db.SaveChangesAsync();
        }

        var emitido = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var item = emitido.Items[0];
        Assert.Equal(273m, item.Descuento);
        Assert.Equal(2727m, item.Total);
        Assert.Equal(combo, item.IdCombo);
        Assert.Equal(idLote, item.IdLote);

        var persistido = (await ItemsAsync(ctx, emitido.Id))[0];
        Assert.Equal(idLote, persistido.IdLote);
        Assert.Equal(combo, persistido.IdCombo);

        await using var lectura = Db(ctx.IdTenant);
        Assert.Equal(9m, await lectura.StockLotes
            .Where(sl => sl.IdArticulo == pancho && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).SingleAsync());
        Assert.Equal(9m, await StockAsync(ctx, pancho));
        Assert.Equal(-1m, await StockAsync(ctx, coca));
    }

    [Fact]
    public async Task ElMismoArticuloEnDosLineasConUnSoloComboConsumeUnaUnidadYDescuentaElStockDeAmbasLineas()
    {
        var ctx = await PrepararAsync(nameof(ElMismoArticuloEnDosLineasConUnSoloComboConsumeUnaUnidadYDescuentaElStockDeAmbasLineas));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 8000m, (pancho, 1m), (coca, 1m), (pancho, 1m));

        // Un set completo: el combo toma el primer pancho y la coca; el segundo pancho queda a lista.
        Assert.Equal(8000m, emitido.Total);
        Assert.Equal(500m, emitido.DescuentoTotal);
        Assert.Equal([2727m, 2273m, 3000m], emitido.Items.Select(i => i.Total));
        Assert.Equal([combo, combo, (int?)null], emitido.Items.Select(i => i.IdCombo));
        Assert.Equal(-2m, await StockAsync(ctx, pancho));
        Assert.Equal(-1m, await StockAsync(ctx, coca));
    }

    [Fact]
    public async Task ElMismoArticuloEnDosLineasConDosCombosAplicaElConjuntoDosVecesYElTotalEsElDeLosDosCombos()
    {
        var ctx = await PrepararAsync(nameof(ElMismoArticuloEnDosLineasConDosCombosAplicaElConjuntoDosVecesYElTotalEsElDeLosDosCombos));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var emitido = await EmitirAsync(ctx, 10000m, (pancho, 1m), (coca, 2m), (pancho, 1m));

        // Dos sets: cada pancho se lleva 273 y la coca 2 × 227 = 454; los dos sets suman 10000.
        Assert.Equal(10000m, emitido.Total);
        Assert.Equal(1000m, emitido.DescuentoTotal);
        Assert.Equal([273m, 454m, 273m], emitido.Items.Select(i => i.Descuento));
        Assert.Equal([2727m, 4546m, 2727m], emitido.Items.Select(i => i.Total));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));
        Assert.Equal(-2m, await StockAsync(ctx, pancho));
        Assert.Equal(-2m, await StockAsync(ctx, coca));
    }

    // =================================================================================================
    // Nota de crédito asociada a una venta con combo: devuelve lo que se cobró
    // =================================================================================================

    private static SolicitudDeVenta Devolucion(Contexto ctx, int idAsociado, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, ctx.IdCliente, "NCX", idAsociado, [.. lineas], [], null, null);

    private async Task DesactivarOfertasAsync(Contexto ctx)
    {
        await using var db = Db(ctx.IdTenant);
        foreach (var oferta in await db.Ofertas.ToListAsync())
        {
            oferta.Activo = false;
        }

        await db.SaveChangesAsync();
    }

    private async Task<decimal> SaldoDelClienteAsync(Contexto ctx)
    {
        await using var db = Db(ctx.IdTenant);

        return await db.Clientes.Where(c => c.Id == ctx.IdCliente).Select(c => c.Saldo).SingleAsync();
    }

    [Fact]
    public async Task LaNotaDeCreditoDeTodoElComboDevuelveLoCobradoYNoLaListaYReponeElStock()
    {
        var ctx = await PrepararAsync(nameof(LaNotaDeCreditoDeTodoElComboDevuelveLoCobradoYNoLaListaYReponeElStock));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(coca, 1m, null)));

        Assert.Equal(-5000m, ncx.Total);
        Assert.Equal(-5500m, ncx.Subtotal);
        Assert.Equal(-500m, ncx.DescuentoTotal);
        Assert.Equal([pancho, coca], ncx.Items.Select(i => i.IdArticulo));
        Assert.Equal([-2727m, -2273m], ncx.Items.Select(i => i.Total));
        Assert.Equal([-273m, -227m], ncx.Items.Select(i => i.Descuento));
        Assert.Equal([3000m, 2500m], ncx.Items.Select(i => i.PrecioUnitario));
        Assert.Equal([-1m, -1m], ncx.Items.Select(i => i.Cantidad));
        Assert.All(ncx.Items, i => Assert.Equal(combo, i.IdCombo));

        var persistidos = await ItemsAsync(ctx, ncx.Id);
        Assert.Equal([-2727m, -2273m], persistidos.Select(i => i.Total));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));

        Assert.Equal(0m, await StockAsync(ctx, pancho));
        Assert.Equal(0m, await StockAsync(ctx, coca));
        Assert.Equal(0m, await SaldoDelClienteAsync(ctx));
    }

    [Fact]
    public async Task LaDevolucionParcialDeUnaVentaConDosJuegosDevuelveLaMitadDeLoCobradoPorEseItem()
    {
        var ctx = await PrepararAsync(nameof(LaDevolucionParcialDeUnaVentaConDosJuegosDevuelveLaMitadDeLoCobradoPorEseItem));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 10000m, (pancho, 2m), (coca, 2m));
        Assert.Equal([5454m, 4546m], venta.Items.Select(i => i.Total));

        var uno = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));
        var item = Assert.Single(uno.Items);
        Assert.Equal(-2727m, item.Total);
        Assert.Equal(-2727m, uno.Total);
        Assert.Equal(3000m, item.PrecioUnitario);
        Assert.Equal(-273m, item.Descuento);
        Assert.Equal(-1m, item.Cantidad);

        var todos = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null)));
        Assert.Equal(-5454m, Assert.Single(todos.Items).Total);
        Assert.Equal(-546m, Assert.Single(todos.Items).Descuento);
    }

    [Fact]
    public async Task UnaLineaDevueltaQueNoEstabaEnElComboSePreciaComoSiempreYLaDelComboAlPrecioCobrado()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaDevueltaQueNoEstabaEnElComboSePreciaComoSiempreYLaDelComboAlPrecioCobrado));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var otro = await ArticuloAsync(ctx, "otro", 100m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5100m, (pancho, 1m), (coca, 1m), (otro, 1m));
        await OfertaDePrecioAsync(ctx, otro, 80m);

        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(otro, 1m, null), new LineaDeVenta(pancho, 1m, null)));

        // El artículo que nunca estuvo en un combo se acredita al precio de HOY (con su oferta de hoy); el del combo, a lo cobrado.
        Assert.Equal([-80m, -2727m], ncx.Items.Select(i => i.Total));
        Assert.Equal([-20m, -273m], ncx.Items.Select(i => i.Descuento));
        Assert.Null(ncx.Items[0].IdCombo);
        Assert.NotNull(ncx.Items[1].IdCombo);
    }

    [Fact]
    public async Task LaDevolucionDeUnaVentaSinCombosSeSigueUsandoAlPrecioDeHoyYNoAlCobrado()
    {
        var ctx = await PrepararAsync(nameof(LaDevolucionDeUnaVentaSinCombosSeSigueUsandoAlPrecioDeHoyYNoAlCobrado));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await OfertaDePrecioAsync(ctx, pancho, 2700m);
        var venta = await EmitirAsync(ctx, 5200m, (pancho, 1m), (coca, 1m));
        Assert.Equal(5200m, venta.Total);

        await DesactivarOfertasAsync(ctx);
        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(coca, 1m, null)));

        // Se cobraron 5200 con la oferta; sin combos la nota de crédito no mira lo cobrado: precia con la lista de hoy.
        Assert.Equal(-5500m, ncx.Total);
        Assert.Equal([-3000m, -2500m], ncx.Items.Select(i => i.Total));
        Assert.Equal([0m, 0m], ncx.Items.Select(i => i.Descuento));
        Assert.All(ncx.Items, i => Assert.Null(i.IdCombo));
        Assert.All(ncx.Items, i => Assert.Null(i.IdOferta));
    }

    [Fact]
    public async Task LaDevolucionDeUnaLineaDelComboConAjusteManualDevuelveElTotalQueIncluiaEseAjuste()
    {
        var ctx = await PrepararAsync(nameof(LaDevolucionDeUnaLineaDelComboConAjusteManualDevuelveElTotalQueIncluiaEseAjuste));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(
            ctx,
            VentaConLineas(
                ctx, 4773m,
                new LineaDeVenta(pancho, 1m, null),
                new LineaDeVenta(coca, 1m, null, AjusteManualPorcentaje: -10m)));
        Assert.Equal([2727m, 2046m], venta.Items.Select(i => i.Total));

        var ncx = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(coca, 1m, null)));

        var item = Assert.Single(ncx.Items);
        Assert.Equal(-2046m, item.Total);
        Assert.Equal(-454m, item.Descuento);
        Assert.Equal(0m, item.AjusteManual);
        Assert.Null(item.AjusteManualPorcentaje);
    }

    [Fact]
    public async Task ElAjusteManualDeLaLineaDeLaNotaDeCreditoSeAplicaSobreLoDevueltoComoEnCualquierNotaDeCredito()
    {
        var ctx = await PrepararAsync(nameof(ElAjusteManualDeLaLineaDeLaNotaDeCreditoSeAplicaSobreLoDevueltoComoEnCualquierNotaDeCredito));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(coca, 1m, null, AjusteManualPorcentaje: -10m)));

        // Neto devuelto −2273; −10 % sobre ese neto: −2273 × −0.10 = +227.3, al peso +227 (se devuelve menos).
        var item = Assert.Single(ncx.Items);
        Assert.Equal(-10m, item.AjusteManualPorcentaje);
        Assert.Equal(227m, item.AjusteManual);
        Assert.Equal(-2046m, item.Total);
        Assert.Equal(-2046m, ncx.Total);
    }

    [Fact]
    public async Task LaNotaDeCreditoConservaLaAlicuotaDeIvaDelItemVendidoAunqueElArticuloLaHayaCambiado()
    {
        var ctx = await PrepararAsync(nameof(LaNotaDeCreditoConservaLaAlicuotaDeIvaDelItemVendidoAunqueElArticuloLaHayaCambiado));
        List<AlicuotaIva> alicuotas;
        await using (var db = Db(ctx.IdTenant))
        {
            alicuotas = await db.AlicuotasIva
                .Where(a => a.CodigoAfip != null && a.Porcentaje > 0)
                .OrderBy(a => a.Porcentaje)
                .ToListAsync();
        }

        var vieja = alicuotas.First();
        var nueva = alicuotas.Last(a => a.Porcentaje != vieja.Porcentaje);
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m, idAlicuotaIva: vieja.Id);
        var coca = await ArticuloAsync(ctx, "coca", 2500m, idAlicuotaIva: vieja.Id);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        await using (var db = Db(ctx.IdTenant))
        {
            foreach (var articulo in await db.Articulos.Where(a => a.Id == pancho || a.Id == coca).ToListAsync())
            {
                articulo.IdAlicuotaIva = nueva.Id;
            }

            await db.SaveChangesAsync();
        }

        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(coca, 1m, null)));

        Assert.All(ncx.Items, i =>
        {
            Assert.Equal(vieja.Id, i.IdAlicuotaIva);
            Assert.Equal(vieja.Porcentaje, i.PorcentajeIva);
        });
        var persistidos = await ItemsAsync(ctx, ncx.Id);
        Assert.All(persistidos, i => Assert.Equal(vieja.Id, i.IdAlicuotaIva));
    }

    [Fact]
    public async Task ConDosItemsDeComboDelMismoArticuloLasUnidadesSeTomanDelDeMenorOrden()
    {
        var ctx = await PrepararAsync(nameof(ConDosItemsDeComboDelMismoArticuloLasUnidadesSeTomanDelDeMenorOrden));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        // Dos juegos en cuatro líneas; el segundo pancho lleva −10 %: queda en 2727 − 273 = 2454.
        var venta = await EmitirAsync(
            ctx,
            VentaConLineas(
                ctx, 9727m,
                new LineaDeVenta(pancho, 1m, null),
                new LineaDeVenta(coca, 1m, null),
                new LineaDeVenta(pancho, 1m, null, AjusteManualPorcentaje: -10m),
                new LineaDeVenta(coca, 1m, null)));
        Assert.Equal([2727m, 2273m, 2454m, 2273m], venta.Items.Select(i => i.Total));

        var unaUnidad = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));
        Assert.Equal(-2727m, Assert.Single(unaUnidad.Items).Total);

        var dosUnidades = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null)));
        Assert.Equal([-2727m, -2454m], dosUnidades.Items.Select(i => i.Total));
        Assert.Equal(-5181m, dosUnidades.Total);

        var enDosLineas = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(pancho, 1m, null)));
        Assert.Equal([-2727m, -2454m], enDosLineas.Items.Select(i => i.Total));
    }

    [Fact]
    public async Task ConElMismoArticuloEnUnItemDeComboYEnOtroFueraDelComboSeDevuelveElDelComboPrimeroYElExcedenteAlPrecioDeHoy()
    {
        var ctx = await PrepararAsync(
            nameof(ConElMismoArticuloEnUnItemDeComboYEnOtroFueraDelComboSeDevuelveElDelComboPrimeroYElExcedenteAlPrecioDeHoy));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 8000m, (pancho, 1m), (coca, 1m), (pancho, 1m));
        Assert.Equal([combo, combo, (int?)null], venta.Items.Select(i => i.IdCombo));
        await OfertaDePrecioAsync(ctx, pancho, 2900m);

        var una = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));
        var item = Assert.Single(una.Items);
        Assert.Equal(-2727m, item.Total);
        Assert.Equal(combo, item.IdCombo);

        // La segunda unidad ya no la cubre ningún item de combo: se precia con la lista y la oferta de hoy.
        var dos = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null)));
        Assert.Equal([-2727m, -2900m], dos.Items.Select(i => i.Total));
        Assert.Equal([combo, (int?)null], dos.Items.Select(i => i.IdCombo));
        Assert.Equal([-1m, -1m], dos.Items.Select(i => i.Cantidad));
        Assert.Equal(-5627m, dos.Total);
    }

    [Fact]
    public async Task LasUnidadesQueExcedenLasVendidasEnComboSePreciaComoSiempreSinDetectarUnComboNuevo()
    {
        var ctx = await PrepararAsync(nameof(LasUnidadesQueExcedenLasVendidasEnComboSePreciaComoSiempreSinDetectarUnComboNuevo));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null), new LineaDeVenta(coca, 1m, null)));

        Assert.Equal([-2727m, -3000m, -2273m], ncx.Items.Select(i => i.Total));
        Assert.Equal([-1m, -1m, -1m], ncx.Items.Select(i => i.Cantidad));
        Assert.Null(ncx.Items[1].IdCombo);
    }

    [Fact]
    public async Task DosNotasDeCreditoDeLaMismaVentaNoSeDescuentanLoYaDevueltoEntreSiHoyElTopeEsPorDocumento()
    {
        var ctx = await PrepararAsync(nameof(DosNotasDeCreditoDeLaMismaVentaNoSeDescuentanLoYaDevueltoEntreSiHoyElTopeEsPorDocumento));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var primera = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));
        var segunda = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));

        // Sin acumulado de lo devuelto por documentos anteriores: cada nota de crédito parte de la venta original.
        Assert.Equal(-2727m, primera.Total);
        Assert.Equal(-2727m, segunda.Total);
    }

    [Fact]
    public async Task LaDevolucionDeUnArticuloConLoteDeUnaVentaConComboExigeIdLoteYReponeElLoteIndicado()
    {
        var ctx = await PrepararAsync(nameof(LaDevolucionDeUnArticuloConLoteDeUnaVentaConComboExigeIdLoteYReponeElLoteIndicado));
        await HabilitarLotesAsync(ctx);
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m, controlaLote: true);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        int idLote;
        await using (var db = Db(ctx.IdTenant))
        {
            var ahora = DateTimeOffset.UtcNow;
            var lote = new Lote
            {
                IdArticulo = pancho, Codigo = "L-NCX-COMBO", FechaVencimiento = new DateOnly(2099, 12, 31),
                EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Lotes.Add(lote);
            await db.SaveChangesAsync();
            idLote = lote.Id;

            db.StockLotes.Add(new StockLote
            {
                IdArticulo = pancho, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = idLote, IdTenant = ctx.IdTenant, Cantidad = 10m
            });
            db.Stock.Add(new Stock
            {
                IdArticulo = pancho, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = 10m
            });
            await db.SaveChangesAsync();
        }

        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        var sinLote = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas", Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(coca, 1m, null)));
        Assert.Equal(HttpStatusCode.BadRequest, sinLote.StatusCode);
        Assert.Contains("lote_requerido", await sinLote.Content.ReadAsStringAsync());

        var ncx = await EmitirAsync(
            ctx,
            Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null, idLote), new LineaDeVenta(coca, 1m, null)));

        Assert.Equal(-5000m, ncx.Total);
        Assert.Equal(idLote, ncx.Items[0].IdLote);
        Assert.Equal(combo, ncx.Items[0].IdCombo);

        await using var lectura = Db(ctx.IdTenant);
        Assert.Equal(10m, await lectura.StockLotes
            .Where(sl => sl.IdArticulo == pancho && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).SingleAsync());
        Assert.Equal(10m, await StockAsync(ctx, pancho));
        Assert.Equal(0m, await StockAsync(ctx, coca));
    }

    [Fact]
    public async Task LaLineaDevueltaQueCruzaDosItemsDeComboReponeElStockYElLoteUnaVezPorTramo()
    {
        var ctx = await PrepararAsync(nameof(LaLineaDevueltaQueCruzaDosItemsDeComboReponeElStockYElLoteUnaVezPorTramo));
        await HabilitarLotesAsync(ctx);
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m, controlaLote: true);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        int idLote;
        await using (var db = Db(ctx.IdTenant))
        {
            var ahora = DateTimeOffset.UtcNow;
            var lote = new Lote
            {
                IdArticulo = pancho, Codigo = "L-NCX-TRAMOS", FechaVencimiento = new DateOnly(2099, 12, 31),
                EsSinIdentificar = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Lotes.Add(lote);
            await db.SaveChangesAsync();
            idLote = lote.Id;

            db.StockLotes.Add(new StockLote
            {
                IdArticulo = pancho, IdPuntoVenta = ctx.IdPuntoVenta, IdLote = idLote, IdTenant = ctx.IdTenant, Cantidad = 10m
            });
            db.Stock.Add(new Stock
            {
                IdArticulo = pancho, IdPuntoVenta = ctx.IdPuntoVenta, IdTenant = ctx.IdTenant, Cantidad = 10m
            });
            await db.SaveChangesAsync();
        }

        // El segundo pancho lleva −10 %: cada item original pagó un total distinto (2727 y 2454), así que el
        // total de cada item de la nota de crédito dice de cuál de los dos salió.
        var venta = await EmitirAsync(
            ctx,
            VentaConLineas(
                ctx, 9727m,
                new LineaDeVenta(pancho, 1m, null),
                new LineaDeVenta(coca, 1m, null),
                new LineaDeVenta(pancho, 1m, null, AjusteManualPorcentaje: -10m),
                new LineaDeVenta(coca, 1m, null)));
        Assert.Equal([2727m, 2273m, 2454m, 2273m], venta.Items.Select(i => i.Total));

        var ncx = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null, idLote)));

        Assert.Equal(2, ncx.Items.Count);
        Assert.All(ncx.Items, i => Assert.Equal(idLote, i.IdLote));
        Assert.Equal([-1m, -1m], ncx.Items.Select(i => i.Cantidad));
        Assert.Equal([-2727m, -2454m], ncx.Items.Select(i => i.Total));
        Assert.Equal([3000m, 3000m], ncx.Items.Select(i => i.PrecioUnitario));
        Assert.Equal([-273m, -546m], ncx.Items.Select(i => i.Descuento));
        Assert.Equal(-5181m, ncx.Total);

        var persistidos = await ItemsAsync(ctx, ncx.Id);
        Assert.Equal([-1m, -1m], persistidos.Select(i => i.Cantidad));
        Assert.Equal([-2727m, -2454m], persistidos.Select(i => i.Total));
        Assert.Equal([-273m, -546m], persistidos.Select(i => i.Descuento));
        Assert.Equal(10m, await StockAsync(ctx, pancho));

        await using var lectura = Db(ctx.IdTenant);
        Assert.Equal(10m, await lectura.StockLotes
            .Where(sl => sl.IdArticulo == pancho && sl.IdPuntoVenta == ctx.IdPuntoVenta && sl.IdLote == idLote)
            .Select(sl => sl.Cantidad).SingleAsync());
    }

    [Fact]
    public async Task ElItemDeLaNotaDeCreditoConservaLaOfertaYLaListaDelItemVendidoConComboYUnidadesFueraDelCombo()
    {
        var ctx = await PrepararAsync(
            nameof(ElItemDeLaNotaDeCreditoConservaLaOfertaYLaListaDelItemVendidoConComboYUnidadesFueraDelCombo));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var oferta = await OfertaDePrecioAsync(ctx, pancho, 2900m);

        // Un item de pancho con 2 unidades: una entra al combo y la otra conserva la oferta de la línea.
        var venta = await EmitirAsync(ctx, 7900m, (pancho, 2m), (coca, 1m));
        var vendido = (await ItemsAsync(ctx, venta.Id)).First(i => i.IdArticulo == pancho);
        Assert.Equal(combo, vendido.IdCombo);
        Assert.Equal(oferta, vendido.IdOferta);
        Assert.Equal(ctx.IdListaPrecio, vendido.IdListaPrecio);

        // Sin ofertas vigentes hoy, el id_oferta de la nota de crédito solo puede venir del item vendido.
        await DesactivarOfertasAsync(ctx);

        var ncx = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));

        var persistido = Assert.Single(await ItemsAsync(ctx, ncx.Id));
        Assert.Equal(oferta, persistido.IdOferta);
        Assert.Equal(ctx.IdListaPrecio, persistido.IdListaPrecio);
        Assert.Equal(combo, persistido.IdCombo);
        Assert.Equal(oferta, Assert.Single(ncx.Items).IdOferta);
        Assert.Equal(5627m, vendido.Total);
        Assert.Equal(-2814m, persistido.Total);
    }

    private async Task CerrarElPrecioVigenteAsync(Contexto ctx, int idArticulo)
    {
        await using var db = Db(ctx.IdTenant);
        foreach (var precio in await db.Precios.Where(p => p.IdArticulo == idArticulo).ToListAsync())
        {
            precio.VigenteHasta = DateTimeOffset.UtcNow.AddHours(-1);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UnArticuloDelComboSinPrecioVigenteSeDevuelveSiLoCubrenLosItemsVendidosEnComboYFallaSiHayExcedente()
    {
        var ctx = await PrepararAsync(
            nameof(UnArticuloDelComboSinPrecioVigenteSeDevuelveSiLoCubrenLosItemsVendidosEnComboYFallaSiHayExcedente));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));
        await CerrarElPrecioVigenteAsync(ctx, pancho);

        // Con excedente: la segunda unidad no la cubre ningún item de combo y se precia con la lista de hoy, que ya no existe.
        var conExcedente = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas", Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null)));
        Assert.Equal(HttpStatusCode.BadRequest, conExcedente.StatusCode);
        Assert.Contains("articulo_sin_precio_vigente", await conExcedente.Content.ReadAsStringAsync());

        // Cubierto por completo por el item vendido: se acredita lo cobrado sin necesitar el precio de hoy.
        var ncx = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null)));
        var item = Assert.Single(ncx.Items);
        Assert.Equal(-2727m, item.Total);
        Assert.Equal(3000m, item.PrecioUnitario);
        Assert.Equal(-273m, item.Descuento);
    }

    [Fact]
    public async Task LaNotaDeCreditoNoExigeCantidadEnteraDeUnArticuloDeUnidadYAcreditaLaFraccionProporcionalDeLoCobrado()
    {
        var ctx = await PrepararAsync(
            nameof(LaNotaDeCreditoNoExigeCantidadEnteraDeUnArticuloDeUnidadYAcreditaLaFraccionProporcionalDeLoCobrado));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        var venta = await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        // La regla de cantidad entera de una unidad de venta Unidad solo alcanza a las ventas (signo positivo): una
        // devolución fraccionaria llega al reparto, y el crédito es la mitad de lo cobrado por el item, 2727 / 2 = 1363.5, al peso 1364.
        var ncx = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 0.5m, null)));

        var item = Assert.Single(ncx.Items);
        Assert.Equal(-0.5m, item.Cantidad);
        Assert.Equal(-1364m, item.Total);
        Assert.Equal(-1364m, ncx.Total);
    }

    [Fact]
    public async Task LaDevolucionDeLasDosUnidadesDeUnItemConTotalImparAcreditaPesosEnterosQueSumanLoCobrado()
    {
        var ctx = await PrepararAsync(
            nameof(LaDevolucionDeLasDosUnidadesDeUnItemConTotalImparAcreditaPesosEnterosQueSumanLoCobrado));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        // Dos juegos; las dos unidades de pancho llevan −10 %: 5454 − 545 (545.4 al peso) = 4909, impar.
        var venta = await EmitirAsync(
            ctx,
            VentaConLineas(
                ctx, 9455m,
                new LineaDeVenta(pancho, 2m, null, AjusteManualPorcentaje: -10m),
                new LineaDeVenta(coca, 2m, null)));
        Assert.Equal([4909m, 4546m], venta.Items.Select(i => i.Total));

        var todas = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 2m, null)));
        Assert.Equal(-4909m, Assert.Single(todas.Items).Total);

        // De a una unidad en el mismo documento: 4909 / 2 = 2454.5 acredita 2455 la primera y el resto, 2454, la segunda.
        var deAUna = await EmitirAsync(
            ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(pancho, 1m, null), new LineaDeVenta(pancho, 1m, null)));
        Assert.Equal([-2455m, -2454m], deAUna.Items.Select(i => i.Total));
        Assert.Equal(-4909m, deAUna.Total);
    }

    [Fact]
    public async Task LaDevolucionDeUnaLineaDelComboConRecargoManualAcreditaMasQueElBrutoConDescuentoPositivo()
    {
        var ctx = await PrepararAsync(
            nameof(LaDevolucionDeUnaLineaDelComboConRecargoManualAcreditaMasQueElBrutoConDescuentoPositivo));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        // +20 % sobre la coca: 2273 de combo + 455 de recargo = 2728, más que su precio de lista (2500).
        var venta = await EmitirAsync(
            ctx,
            VentaConLineas(
                ctx, 5455m,
                new LineaDeVenta(pancho, 1m, null),
                new LineaDeVenta(coca, 1m, null, AjusteManualPorcentaje: 20m)));
        Assert.Equal([2727m, 2728m], venta.Items.Select(i => i.Total));

        var ncx = await EmitirAsync(ctx, Devolucion(ctx, venta.Id, new LineaDeVenta(coca, 1m, null)));

        // Se devuelve lo pagado (2728 > bruto 2500): el descuento del item tiene signo POSITIVO, al revés que el de
        // una nota de crédito común, porque bruto (−2500) − descuento (+228) = −crédito (−2728).
        var item = Assert.Single(ncx.Items);
        Assert.Equal(-2728m, item.Total);
        Assert.Equal(-2728m, ncx.Total);
        Assert.Equal(-2500m, item.Cantidad * item.PrecioUnitario);
        Assert.Equal(228m, item.Descuento);

        var persistido = Assert.Single(await ItemsAsync(ctx, ncx.Id));
        Assert.Equal(-2728m, persistido.Total);
        Assert.Equal(228m, persistido.Descuento);
    }

    // =================================================================================================
    // Respaldo de esquema de items_comprobante_venta.id_combo (fk_items_comprobante_venta_combo)
    // Viven acá y no en CombosEsquemaTests porque necesitan una venta emitida de verdad (comprobante, punto de
    // venta, turno) y este archivo ya tiene ese armado.
    // =================================================================================================

    private static async Task AssertViolacionDeFkDeItemAsync(Func<Task> accion)
    {
        var excepcion = await Assert.ThrowsAsync<Npgsql.PostgresException>(accion);
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_items_comprobante_venta_combo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnItemQueApuntaAUnComboDeOtroTenantViolaLaFkCompuestaDeComboDelItem()
    {
        var ctx = await PrepararAsync(nameof(UnItemQueApuntaAUnComboDeOtroTenantViolaLaFkCompuestaDeComboDelItem) + "A");
        var otro = await PrepararAsync(nameof(UnItemQueApuntaAUnComboDeOtroTenantViolaLaFkCompuestaDeComboDelItem) + "B");
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var panchoDeOtro = await ArticuloAsync(otro, "pancho", 3000m);
        var cocaDeOtro = await ArticuloAsync(otro, "coca", 2500m);
        var comboDeOtro = await ComboAsync(otro, 5000m, [Art(panchoDeOtro), Art(cocaDeOtro)]);
        var emitido = await EmitirAsync(ctx, 3000m, (pancho, 1m));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        await AssertViolacionDeFkDeItemAsync(async () =>
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "UPDATE items_comprobante_venta SET id_combo = $1 WHERE id_comprobante_venta = $2";
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = comboDeOtro });
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = emitido.Id });
            await comando.ExecuteNonQueryAsync();
        });

        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.All(persistidos, i => Assert.Null(i.IdCombo));
    }

    [Fact]
    public async Task BorrarFisicamenteUnComboReferenciadoPorUnItemDeVentaViolaLaFkYElComboSigueAhi()
    {
        var ctx = await PrepararAsync(nameof(BorrarFisicamenteUnComboReferenciadoPorUnItemDeVentaViolaLaFkYElComboSigueAhi));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);
        await EmitirAsync(ctx, 5000m, (pancho, 1m), (coca, 1m));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        await AssertViolacionDeFkDeItemAsync(async () =>
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "DELETE FROM combos WHERE id_combo = $1";
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = combo });
            await comando.ExecuteNonQueryAsync();
        });

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(2, await db.CombosComponentes.CountAsync(c => c.IdCombo == combo));
    }

    // =================================================================================================
    // Reintento
    // =================================================================================================

    [Fact]
    public async Task UnReintentoDeLaEscrituraDeUnaVentaConComboEmiteUnaSolaVentaIgualALaDeSiempre()
    {
        var ctx = await PrepararAsync(nameof(UnReintentoDeLaEscrituraDeUnaVentaConComboEmiteUnaSolaVentaIgualALaDeSiempre));
        var pancho = await ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await ArticuloAsync(ctx, "coca", 2500m);
        var combo = await ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("items_comprobante_venta", "40001");
        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", Venta(ctx, 5000m, (pancho, 1m), (coca, 1m)));
        }

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        Assert.Equal(2, interceptor.Intentos);

        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
        Assert.Equal(5000m, emitido.Total);

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(1, await db.ComprobantesVenta.CountAsync(c => c.IdPuntoVenta == ctx.IdPuntoVenta));
        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.Equal([2727m, 2273m], persistidos.Select(i => i.Total));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));
        Assert.Equal(-1m, await StockAsync(ctx, pancho));
    }
}
