using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
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
        int? idAlicuotaIva = null)
    {
        await using var db = Db(ctx.IdTenant);
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = idAlicuotaIva ?? ctx.IdAlicuotaIva, UnidadVenta = unidad,
            IdFamilia = idFamilia, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
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
        Assert.Equal([2727.27m, 2272.73m], emitido.Items.Select(i => i.Total));
        Assert.Equal([272.73m, 227.27m], emitido.Items.Select(i => i.Descuento));
        Assert.All(emitido.Items, i => Assert.Equal(combo, i.IdCombo));

        var persistidos = await ItemsAsync(ctx, emitido.Id);
        Assert.Equal([2727.27m, 2272.73m], persistidos.Select(i => i.Total));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));

        var releido = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitido.Id}", OpcionesJson);
        Assert.All(releido!.Items, i => Assert.Equal(combo, i.IdCombo));
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

        var netoBajo = Math.Round(2727.27m / (1 + (baja.Porcentaje / 100m)), 2, MidpointRounding.AwayFromZero);
        var netoAlto = Math.Round(2272.73m / (1 + (alta.Porcentaje / 100m)), 2, MidpointRounding.AwayFromZero);
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
        Assert.Equal([5727.27m, 2272.73m], emitido.Items.Select(i => i.Total));
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

        // El pancho consumido va a lista menos su parte (272.73); el no consumido conserva los 300 de la oferta.
        Assert.Equal(572.73m, emitido.Items[0].Descuento);
        Assert.Equal(5427.27m, emitido.Items[0].Total);
        Assert.Equal(oferta, emitido.Items[0].IdOferta);
        Assert.Equal(combo, emitido.Items[0].IdCombo);
        Assert.Equal(2272.73m, emitido.Items[1].Total);
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
        Assert.Equal(272.73m, emitido.Items[0].Descuento);
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
    public async Task UnaNotaDeCreditoNoDetectaCombos()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoNoDetectaCombos));
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
        Assert.Equal([2727.27m, 2272.73m], persistidos.Select(i => i.Total));
        Assert.All(persistidos, i => Assert.Equal(combo, i.IdCombo));
        Assert.Equal(-1m, await StockAsync(ctx, pancho));
    }
}
