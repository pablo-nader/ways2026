using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-9-costo-congelado, Slice 1 (tasks 1.9/1.10/1.12/1.13/1.15, design: Testing Strategy —
/// "false green" / Backstop Map): snapshot del costo al emitir (tres estados: real/estimado/
/// desconocido), el signo de una NCX, el backfill de una sola vez en DOS pruebas de honestidad
/// creciente — una multi-tenant "ingenua" contra <c>ways_owner</c> (superusuario del contenedor,
/// design finding 2: no prueba RLS de verdad) y una a nivel statement contra <c>ways_app</c>
/// (<c>NOSUPERUSER NOBYPASSRLS</c>, la única que prueba el trap real) — y la no-exposición del
/// costo en el payload de venta (decisión 5).
///
/// El SQL del backfill está DUPLICADO a propósito respecto de la migración
/// <c>CostoCongeladoEnVentaEtapa9</c> (mismo criterio que <c>ComprasTipoSeedTests</c>, doc-comment
/// en ambos lados): una migración es un snapshot congelado, no debe depender de una constante
/// compartida que un edit futuro reapunte en silencio.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CostoCongeladoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string MigracionStage8 = "20260805181153_ComprasYTransferenciasEtapa8";

    /// <summary>El mismo statement que <c>CostoCongeladoEnVentaEtapa9.Up</c> — ver doc-comment de
    /// clase.</summary>
    private const string SqlDelBackfill =
        """
        UPDATE items_comprobante_venta i
           SET costo_unitario    = a.costo_nominal,
               costo_es_estimado = true
          FROM articulos a
         WHERE a.id_articulo = i.id_articulo
           AND a.id_tenant   = i.id_tenant
           AND i.id_articulo IS NOT NULL
           AND a.costo_nominal IS NOT NULL
           AND i.costo_unitario IS NULL;
        """;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdEmpleadoAdmin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Costo-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Costo", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        db.TurnosCaja.Add(new TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva, lista.Id, idMedioEfectivo,
            resultado.IdUsuarioAdmin);
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, decimal precio, decimal? costoNominal)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CostoNominal = costoNominal, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio,
            Monto = precio, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = 10_000m,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private async Task<(decimal? CostoUnitario, bool CostoEsEstimado)> LeerCostoDelItemAsync(
        Contexto ctx, int idComprobanteVenta, int orden = 1)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var item = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == idComprobanteVenta && i.Orden == orden)
            .Select(i => new { i.CostoUnitario, i.CostoEsEstimado })
            .FirstAsync();
        return (item.CostoUnitario, item.CostoEsEstimado);
    }

    private async Task ActualizarCostoNominalAsync(Contexto ctx, int idArticulo, decimal? nuevoCosto)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var articulo = await db.Articulos.FirstAsync(a => a.Id == idArticulo);
        articulo.CostoNominal = nuevoCosto;
        await db.SaveChangesAsync();
    }

    // ---- task 1.9: snapshot al emitir — tres estados -------------------------------------------

    [Fact]
    public async Task EmisionCongelaElCostoNominalVigenteEnLaLinea()
    {
        var ctx = await PrepararAsync(nameof(EmisionCongelaElCostoNominalVigenteEnLaLinea));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-con-costo", 200m, costoNominal: 121.00m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Costo Real");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 200m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var (costoUnitario, costoEsEstimado) = await LeerCostoDelItemAsync(ctx, emitido.Id);
        Assert.Equal(121.00m, costoUnitario);
        Assert.False(costoEsEstimado);
    }

    [Fact]
    public async Task UnArticuloSinCostoNominalProduceUnaLineaConCostoNuloNuncaCero()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloSinCostoNominalProduceUnaLineaConCostoNuloNuncaCero));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-costo", 50m, costoNominal: null);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sin Costo");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 50m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var (costoUnitario, costoEsEstimado) = await LeerCostoDelItemAsync(ctx, emitido.Id);
        Assert.Null(costoUnitario);
        Assert.False(costoEsEstimado);
    }

    [Fact]
    public async Task UnArticuloConCostoNominalCeroPersisteCeroDistinguibleDeNulo()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloConCostoNominalCeroPersisteCeroDistinguibleDeNulo));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-costo-cero", 30m, costoNominal: 0m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Costo Cero");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 30m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var (costoUnitario, costoEsEstimado) = await LeerCostoDelItemAsync(ctx, emitido.Id);
        Assert.NotNull(costoUnitario);
        Assert.Equal(0m, costoUnitario!.Value);
        Assert.False(costoEsEstimado);
    }

    [Fact]
    public async Task LaReimpresionNoRederivaElCostoAunqueElCostoVivoCambieDespues()
    {
        var ctx = await PrepararAsync(nameof(LaReimpresionNoRederivaElCostoAunqueElCostoVivoCambieDespues));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-reimpresion", 80m, costoNominal: 55.00m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Reimpresion");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 80m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        // El costo vivo se mueve DESPUÉS de emitir — la reimpresión no tiene que notarlo.
        await ActualizarCostoNominalAsync(ctx, idArticulo, 999.00m);

        var reimpreso = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitido.Id}", OpcionesJson);
        Assert.Equal(emitido.Total, reimpreso!.Total);

        var (costoUnitario, _) = await LeerCostoDelItemAsync(ctx, emitido.Id);
        Assert.Equal(55.00m, costoUnitario);
    }

    // ---- task 1.10: una NCX congela su PROPIO costo, no el del TX original ---------------------

    [Fact]
    public async Task UnaNcxCongelaSuPropioCostoDeEmisionYElProductoConCantidadDaNegativo()
    {
        var ctx = await PrepararAsync(nameof(UnaNcxCongelaSuPropioCostoDeEmisionYElProductoConCantidadDaNegativo));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-ncx-costo", 100m, costoNominal: 100.00m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente NCX Costo");

        var original = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);
        var respuestaOriginal = await ctx.Admin.PostAsJsonAsync("/api/ventas", original);
        Assert.Equal(HttpStatusCode.Created, respuestaOriginal.StatusCode);
        var emitidoOriginal = (await respuestaOriginal.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var (costoOriginal, _) = await LeerCostoDelItemAsync(ctx, emitidoOriginal.Id);
        Assert.Equal(100.00m, costoOriginal);

        // El costo vivo se mueve ENTRE la venta y la devolución.
        await ActualizarCostoNominalAsync(ctx, idArticulo, 110.00m);

        var devolucion = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", emitidoOriginal.Id,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [],
            null, null);
        var respuestaDevolucion = await ctx.Admin.PostAsJsonAsync("/api/ventas", devolucion);
        Assert.Equal(HttpStatusCode.Created, respuestaDevolucion.StatusCode);
        var emitidoDevolucion = (await respuestaDevolucion.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        // La NCX congela su PROPIO costo (110), no el que el TX original congeló (100).
        var (costoDevolucion, estimadoDevolucion) = await LeerCostoDelItemAsync(ctx, emitidoDevolucion.Id);
        Assert.Equal(110.00m, costoDevolucion);
        Assert.False(estimadoDevolucion);

        // Sin signo (igual que precio_unitario): el signo vive en cantidad, ya negativa en la NCX.
        var cantidadNcx = emitidoDevolucion.Items[0].Cantidad;
        Assert.True(cantidadNcx < 0);
        Assert.True(costoDevolucion!.Value * cantidadNcx < 0);
    }

    // ---- task 1.15: el costo nunca cruza el payload de venta ------------------------------------

    [Fact]
    public void ItemEmitidoYComprobanteEmitidoNoTienenNingunMiembroDeCosto()
    {
        static void AssertSinMiembroDeCosto(Type tipo)
        {
            var miembroDeCosto = tipo.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name.Contains("costo", StringComparison.OrdinalIgnoreCase));
            Assert.True(
                miembroDeCosto is null,
                $"{tipo.Name} expone {miembroDeCosto?.Name} — el costo nunca debe cruzar el payload de venta.");
        }

        AssertSinMiembroDeCosto(typeof(ItemEmitido));
        AssertSinMiembroDeCosto(typeof(ComprobanteEmitido));
    }

    [Fact]
    public async Task ElCuerpoJsonDeLaRespuestaDeCheckoutNoContieneNingunaClaveDeCosto()
    {
        var ctx = await PrepararAsync(nameof(ElCuerpoJsonDeLaRespuestaDeCheckoutNoContieneNingunaClaveDeCosto));
        var idArticulo = await SembrarArticuloAsync(ctx, "articulo-sin-leak", 40m, costoNominal: 15.50m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sin Leak");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 40m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        using var documento = JsonDocument.Parse(cuerpo);
        Assert.False(ContieneClaveDeCosto(documento.RootElement), $"la respuesta filtró una clave de costo: {cuerpo}");
    }

    private static bool ContieneClaveDeCosto(JsonElement elemento)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var propiedad in elemento.EnumerateObject())
                {
                    if (propiedad.Name.Contains("costo", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (ContieneClaveDeCosto(propiedad.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    if (ContieneClaveDeCosto(item))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    // ---- task 1.13: el backfill a nivel statement, sobre ways_app (la única prueba real de RLS,
    // design finding 2 — 1.12 abajo es la "ingenua" que este test compensa) ---------------------

    [Fact]
    public async Task ElBackfillSoloAlcanzaFilasEnModoPlataformaYEsIdempotente()
    {
        var ctx1 = await PrepararAsync($"{nameof(ElBackfillSoloAlcanzaFilasEnModoPlataformaYEsIdempotente)}A");
        var ctx2 = await PrepararAsync($"{nameof(ElBackfillSoloAlcanzaFilasEnModoPlataformaYEsIdempotente)}B");

        var idComprobante1 = await SembrarComprobantePreexistenteSinCostoAsync(ctx1, "art-backfill-a", 71.50m);
        var idComprobante2 = await SembrarComprobantePreexistenteSinCostoAsync(ctx2, "art-backfill-b", 88.25m);

        // (a) SIN el prefijo SET LOCAL: bajo NOSUPERUSER NOBYPASSRLS y ningún GUC seteado,
        // app_es_plataforma() = false y app_tenant_actual() = NULL — el UPDATE no puede ver NADA,
        // no revienta: afecta CERO filas y "reporta éxito" (design finding 1/decisión 6).
        await using (var cruda = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText = SqlDelBackfill;
            var afectadas = await comando.ExecuteNonQueryAsync();
            Assert.Equal(0, afectadas);
        }

        // (b) CON el prefijo, en el mismo bloque Sql() (misma transacción implícita) — el trámite
        // exacto de la migración: alcanza las filas de AMBOS tenants de una sola pasada.
        await using (var cruda = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlDelBackfill;
            var afectadas = await comando.ExecuteNonQueryAsync();
            Assert.Equal(2, afectadas);
        }

        var (costo1, estimado1) = await LeerCostoDelItemAsync(ctx1, idComprobante1);
        var (costo2, estimado2) = await LeerCostoDelItemAsync(ctx2, idComprobante2);
        Assert.Equal(71.50m, costo1);
        Assert.True(estimado1);
        Assert.Equal(88.25m, costo2);
        Assert.True(estimado2);

        // (c) Re-ejecutar es un no-op: WHERE costo_unitario IS NULL ya excluye las filas recién
        // completadas.
        await using (var cruda = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await cruda.OpenAsync();
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "SET LOCAL app.acceso = 'plataforma';\n" + SqlDelBackfill;
            var afectadas = await comando.ExecuteNonQueryAsync();
            Assert.Equal(0, afectadas);
        }
    }

    /// <summary>Simula una fila "legacy" pre-stage-9: un item ya emitido con
    /// <c>costo_unitario IS NULL</c> aunque su artículo YA tiene <c>costo_nominal</c> (el estado
    /// real de cualquier fila que existía antes de que esta migración corriera). Vía EF porque el
    /// esquema de la fixture compartida YA incluye las columnas de esta etapa — a diferencia de
    /// <see cref="LosCatalogosPreexistentesDeDosTenantsGananCostoEstimadoTrasElBackfillYLosGapsQuedanIntactos"/>,
    /// que necesita una base propia migrada SOLO hasta stage 8.</summary>
    private async Task<int> SembrarComprobantePreexistenteSinCostoAsync(Contexto ctx, string nombreArticulo, decimal costoNominal)
    {
        var idArticulo = await SembrarArticuloAsync(ctx, nombreArticulo, costoNominal * 2, costoNominal);
        var idCliente = await SembrarClienteAsync(ctx, $"cliente-{nombreArticulo}");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = idTipoComprobanteTx, Numero = Random.Shared.Next(1, 1_000_000),
            Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleado = ctx.IdEmpleadoAdmin, IdCliente = idCliente,
            Subtotal = costoNominal * 2, DescuentoTotal = 0m, Total = costoNominal * 2,
            Estado = EstadoComprobante.Emitido, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        // CostoUnitario/CostoEsEstimado quedan en su default (NULL / false) — así es exactamente
        // como una fila pre-stage-9 llega a la migración.
        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = idArticulo,
            Descripcion = nombreArticulo, IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = 1m,
            PrecioUnitario = costoNominal * 2, Descuento = 0m, Total = costoNominal * 2,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    // ---- task 1.12: backfill multi-tenant sobre una base fresca migrada solo hasta stage 8
    // (ways_owner — la prueba "ingenua", compensada por la de arriba) ----------------------------

    [Fact]
    public async Task LosCatalogosPreexistentesDeDosTenantsGananCostoEstimadoTrasElBackfillYLosGapsQuedanIntactos()
    {
        var nombreBase = $"ways_stage9_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaNueva = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            var opciones = new DbContextOptionsBuilder<WaysDbContext>()
                .UseNpgsql(cadenaNueva, npgsql =>
                {
                    npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                    npgsql.MapEnum<EstadoTenant>("estado_tenant");
                    npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                    npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                    npgsql.MapEnum<TipoDocumento>("tipo_documento");
                    npgsql.MapEnum<ModoLista>("modo_lista");
                    npgsql.MapEnum<UnidadVenta>("unidad_venta");
                    npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                    npgsql.MapEnum<MotivoStock>("motivo_stock");
                    npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                    npgsql.MapEnum<EstadoTurno>("estado_turno");
                    npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                    npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                    npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                    npgsql.MapEnum<EstadoCompra>("estado_compra");
                })
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (var migrando = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionStage8);
            }

            (int IdComprobante, int IdArticuloConCosto, int IdArticuloSinCosto) tenantA;
            (int IdComprobante, int IdArticuloConCosto, int IdArticuloSinCosto) tenantB;

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                // roles es [global] y NO se siembra sola (InicializadorDeBaseDeDatos.EjecutarAsync
                // nunca corre contra esta base propia) — hace falta a mano, una sola vez, para que
                // fk_usuarios_rol no reviente al sembrar el empleado de cada comprobante.
                var ahoraRol = DateTimeOffset.UtcNow;
                db.Roles.Add(new Rol
                {
                    Id = (int)RolConocido.Vendedor, Nombre = "vendedor", CreatedAt = ahoraRol, UpdatedAt = ahoraRol
                });
                await db.SaveChangesAsync();

                tenantA = await SembrarTenantPreEtapa9Async(db, cadenaNueva, "TenantBackfillA");
                tenantB = await SembrarTenantPreEtapa9Async(db, cadenaNueva, "TenantBackfillB");
            }

            await using (var migrando = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = migrando.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica CostoCongeladoEnVentaEtapa9, la única pendiente
            }

            await using var verificacion = new NpgsqlConnection(cadenaNueva);
            await verificacion.OpenAsync();

            async Task<(decimal? Costo, bool Estimado)> LeerFilaAsync(int idComprobante, int idArticulo)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText =
                    "SELECT costo_unitario, costo_es_estimado FROM items_comprobante_venta " +
                    "WHERE id_comprobante_venta = $1 AND id_articulo = $2";
                comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
                comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });
                await using var lector = await comando.ExecuteReaderAsync();
                await lector.ReadAsync();
                return (lector.IsDBNull(0) ? null : lector.GetDecimal(0), lector.GetBoolean(1));
            }

            async Task<(decimal? Costo, bool Estimado)> LeerFilaSinArticuloAsync(int idComprobante)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText =
                    "SELECT costo_unitario, costo_es_estimado FROM items_comprobante_venta " +
                    "WHERE id_comprobante_venta = $1 AND id_articulo IS NULL";
                comando.Parameters.Add(new NpgsqlParameter { Value = idComprobante });
                await using var lector = await comando.ExecuteReaderAsync();
                await lector.ReadAsync();
                return (lector.IsDBNull(0) ? null : lector.GetDecimal(0), lector.GetBoolean(1));
            }

            // Ambos tenants: la línea con artículo costeado gana el backfill.
            var (costoA, estimadoA) = await LeerFilaAsync(tenantA.IdComprobante, tenantA.IdArticuloConCosto);
            Assert.NotNull(costoA);
            Assert.True(estimadoA);

            var (costoB, estimadoB) = await LeerFilaAsync(tenantB.IdComprobante, tenantB.IdArticuloConCosto);
            Assert.NotNull(costoB);
            Assert.True(estimadoB);

            // Los dos gaps honestos quedan intactos en AMBOS tenants: artículo sin costo, y línea
            // sin artículo (concepto libre).
            var (costoSinCostoA, estimadoSinCostoA) = await LeerFilaAsync(tenantA.IdComprobante, tenantA.IdArticuloSinCosto);
            Assert.Null(costoSinCostoA);
            Assert.False(estimadoSinCostoA);

            var (costoLibreA, estimadoLibreA) = await LeerFilaSinArticuloAsync(tenantA.IdComprobante);
            Assert.Null(costoLibreA);
            Assert.False(estimadoLibreA);

            var (costoSinCostoB, estimadoSinCostoB) = await LeerFilaAsync(tenantB.IdComprobante, tenantB.IdArticuloSinCosto);
            Assert.Null(costoSinCostoB);
            Assert.False(estimadoSinCostoB);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Siembra un tenant completo al estado de esquema de <c>ComprasYTransferenciasEtapa8</c>
    /// (todavía SIN <c>costo_unitario</c>/<c>costo_es_estimado</c>): vía EF para toda tabla cuya
    /// forma no cambió en stage 9 (tenant, empresa, punto de venta, area, alicuota, tipo de
    /// comprobante, condición fiscal, lista de precio, cliente, artículos, comprobante) y con SQL
    /// crudo SOLO para <c>items_comprobante_venta</c> — la única tabla cuyo esquema físico en este
    /// punto todavía no tiene las dos columnas nuevas (usar EF ahí generaría un INSERT contra
    /// columnas inexistentes). Tres líneas por tenant: un artículo costeado (gana el backfill), un
    /// artículo sin costo, y una línea de concepto libre (<c>id_articulo NULL</c>) — los dos gaps
    /// honestos que el backfill nunca debe tocar.</summary>
    private static async Task<(int IdComprobante, int IdArticuloConCosto, int IdArticuloSinCosto)> SembrarTenantPreEtapa9Async(
        WaysDbContext db, string cadenaConexion, string nombre)
    {
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenant.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var alicuota = new AlicuotaIva { Nombre = $"{nombre}-21%", Porcentaje = 21m, CreatedAt = ahora, UpdatedAt = ahora };
        db.AlicuotasIva.Add(alicuota);
        await db.SaveChangesAsync();

        var tipoTx = new TipoComprobante
        {
            Clase = ClaseComprobante.Venta, Codigo = $"{nombre}-TX", Nombre = "Ticket X", Letra = null,
            Signo = 1, DiscriminaIva = false, EsFiscal = false, AfectaStock = true, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.TiposComprobante.Add(tipoTx);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var listaPrecio = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaPrecio);
        await db.SaveChangesAsync();

        var cliente = new Cliente
        {
            IdTenant = tenant.Id, Numero = 2, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = listaPrecio.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id, NombreUsuario = "vendedor", Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Vendedor, PasswordHash = "hash-de-prueba", PasswordAlgoritmo = "test",
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        // Esquema todavía en stage 8 acá: SQL crudo con columnas explícitas, inmune a columnas
        // que etapas posteriores agregan a articulos (controla_lote de la etapa 12 no existe todavía).
        int idArticuloConCosto;
        int idArticuloSinCosto;
        await using (var crudaArticulos = new NpgsqlConnection(cadenaConexion))
        {
            await crudaArticulos.OpenAsync();

            async Task<int> InsertarArticuloAsync(string codigo, decimal? costoNominal)
            {
                await using var comando = crudaArticulos.CreateCommand();
                comando.CommandText =
                    "INSERT INTO articulos (id_tenant, codigo_interno, nombre, id_area, id_alicuota_iva, " +
                    "unidad_venta, es_producto, costo_nominal, created_at, updated_at) " +
                    "VALUES ($1, $2, $2, $3, $4, 'unidad'::unidad_venta, true, $5, now(), now()) " +
                    "RETURNING id_articulo";
                comando.Parameters.Add(new NpgsqlParameter { Value = tenant.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
                comando.Parameters.Add(new NpgsqlParameter { Value = area.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = alicuota.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = (object?)costoNominal ?? DBNull.Value });
                var resultado = await comando.ExecuteScalarAsync();
                return (int)resultado!;
            }

            idArticuloConCosto = await InsertarArticuloAsync($"{nombre}-con-costo", 60.00m);
            idArticuloSinCosto = await InsertarArticuloAsync($"{nombre}-sin-costo", null);
        }

        var comprobante = new ComprobanteVenta
        {
            IdTenant = tenant.Id, IdTipoComprobante = tipoTx.Id, Numero = 1, Fecha = ahora,
            IdPuntoVenta = puntoVenta.Id, IdEmpleado = usuario.Id, IdCliente = cliente.Id,
            Subtotal = 200m, DescuentoTotal = 0m, Total = 200m, Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        // Esquema todavía en stage 8 acá: SQL crudo, sin las columnas de costo (no existen todavía).
        await using (var cruda = new NpgsqlConnection(cadenaConexion))
        {
            await cruda.OpenAsync();

            async Task InsertarItemAsync(int? idArticulo, string descripcion)
            {
                await using var comando = cruda.CreateCommand();
                comando.CommandText =
                    "INSERT INTO items_comprobante_venta (id_tenant, id_comprobante_venta, orden, id_articulo, " +
                    "descripcion, id_area, id_lista_precio, id_alicuota_iva, porcentaje_iva, cantidad, " +
                    "precio_unitario, descuento, total, created_at, updated_at) " +
                    "VALUES ($1, $2, (SELECT COALESCE(MAX(orden), 0) + 1 FROM items_comprobante_venta " +
                    "WHERE id_comprobante_venta = $2), $3, $4, $5, $6, $7, 0, 1, 100, 0, 100, now(), now())";
                comando.Parameters.Add(new NpgsqlParameter { Value = tenant.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = comprobante.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idArticulo ?? DBNull.Value });
                comando.Parameters.Add(new NpgsqlParameter { Value = descripcion });
                comando.Parameters.Add(new NpgsqlParameter { Value = area.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = listaPrecio.Id });
                comando.Parameters.Add(new NpgsqlParameter { Value = alicuota.Id });
                await comando.ExecuteNonQueryAsync();
            }

            await InsertarItemAsync(idArticuloConCosto, "linea-con-costo");
            await InsertarItemAsync(idArticuloSinCosto, "linea-sin-costo");
            await InsertarItemAsync(null, "linea-concepto-libre");
        }

        return (comprobante.Id, idArticuloConCosto, idArticuloSinCosto);
    }
}
