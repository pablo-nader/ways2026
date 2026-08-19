using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 3 (tasks 3.1-3.24; design: Transactions — "RAMA DEL
/// SNAPSHOT"/"POSICIÓN 1.5"; state.yaml CRITERIO DE VERIFY VINCULANTE). El slice MÁS delicado de
/// la etapa: toca el checkout bajo un criterio de diff acotado — una cláusula en el resolver, la
/// rama del snapshot en la fase decide, y UNA llamada guardeada en <c>EjecutarTransaccionAsync</c>
/// en la POSICIÓN 1.5. La venta fantasma (ambas redes del PRE), la fidelidad del precio congelado,
/// la carrera convertir×convertir y el criterio de cero-statements-extra son los binding tests de
/// este archivo — cada uno referenciado por su número de target de <c>design.md</c>.
///
/// <see cref="VentasCheckoutTests"/>/<see cref="AnulacionTests"/>/
/// <see cref="VentasAtomicidadYConcurrenciaTests"/> quedan INTOCADOS por esta slice (non-regression,
/// task 3.24) — este archivo es enteramente nuevo, nunca modifica esos tres.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeVentasConversionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdPuntoVenta2, HttpClient Admin, int IdArea,
        int IdAlicuotaIva, int IdListaPrecio, int IdCliente, int IdArticulo, int IdArticulo2, int IdMedioEfectivo,
        int IdUsuarioAdmin, string MailAdmin, string PasswordAdmin);

    /// <summary>Decisión 13 (tasks.md): ids deliberadamente desincronizados — cada entidad nace en
    /// su propia tabla con su propia identidad autoincremental, nunca forzada a coincidir. Combina
    /// el fixture de <c>ServicioDePresupuestosTests</c> (dos puntos de venta, cliente/artículos de
    /// presupuesto) con el de <c>VentasCheckoutTests</c> (turno abierto + medio de pago) — esta
    /// slice ejercita AMBAS superficies en la misma prueba.</summary>
    private async Task<Contexto> PrepararAsync(string nombre) => await PrepararConFactoryAsync(nombre, null);

    private async Task<Contexto> PrepararConFactoryAsync(string nombre, WebApplicationFactory<Program>? factory)
    {
        var root = factory is null ? fixture.CreateClient() : factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory is null ? fixture.CreateClient() : factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = $"{nombre}-PV2",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta2);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Conv-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista de Conversión", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = $"{nombre}-cliente",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 0,
            CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-conv-1-{Guid.NewGuid():N}", Nombre = "nombre-original",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CostoNominal = 80m, CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-conv-2-{Guid.NewGuid():N}", Nombre = "Conv Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CostoNominal = 40m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        db.Precios.AddRange(
            new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo1.Id, IdListaPrecio = lista.Id, Monto = 100m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            },
            new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo2.Id, IdListaPrecio = lista.Id, Monto = 250m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // Turno abierto en AMBOS puntos de venta — la conversión, como el checkout, exige uno.
        db.TurnosCaja.AddRange(
            new Ways.Domain.Caja.TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
                IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
                Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            },
            new Ways.Domain.Caja.TurnoCaja
            {
                IdTenant = resultado.IdTenant, IdPuntoVenta = puntoVenta2.Id,
                IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
                Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, puntoVenta2.Id, admin, area.Id,
            idAlicuotaIva, lista.Id, cliente.Id, articulo1.Id, articulo2.Id, idMedioEfectivo, resultado.IdUsuarioAdmin,
            mailAdmin, resultado.PasswordTemporal);
    }

    /// <summary>Login FRESCO contra un factory nuevo (típicamente instrumentado con un
    /// interceptor) — nunca reusa <see cref="Contexto.Admin"/>, que quedó atado al factory de
    /// <see cref="PrepararAsync"/>. Necesario para aislar un interceptor de rendezvous a SOLO las
    /// dos llamadas concurrentes bajo prueba, sin contaminarlo con las transacciones del setup
    /// secuencial (p.ej. el propio <c>enviar</c> del presupuesto, que también abre dos
    /// transacciones por request).</summary>
    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, Contexto ctx)
    {
        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private static DateOnly FechaFutura() => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

    private static async Task<PresupuestoDetalle> CrearBorradorAsync(
        HttpClient cliente, Contexto ctx, decimal cantidad = 2m, int? idPuntoVenta = null, int? idOferta = null)
    {
        var solicitud = new SolicitudDePresupuesto(
            idPuntoVenta ?? ctx.IdPuntoVenta, ctx.IdCliente, "obs", [new LineaDePresupuesto(ctx.IdArticulo, cantidad)]);
        var respuesta = await cliente.PostAsJsonAsync("/api/presupuestos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<PresupuestoDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<PresupuestoDetalle> EnviarAsync(HttpClient cliente, int id, DateOnly? vencimiento = null)
    {
        var respuesta = await cliente.PostAsJsonAsync($"/api/presupuestos/{id}/enviar", new SolicitudDeEnvio(vencimiento ?? FechaFutura()));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PresupuestoDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<PresupuestoDetalle> CrearYEnviarAsync(
        HttpClient cliente, Contexto ctx, decimal cantidad = 2m, int? idPuntoVenta = null, DateOnly? vencimiento = null)
    {
        var creado = await CrearBorradorAsync(cliente, ctx, cantidad, idPuntoVenta);
        return await EnviarAsync(cliente, creado.Id, vencimiento);
    }

    private static SolicitudDeVenta SolicitudDeConversion(
        Contexto ctx, int idPresupuestoOrigen, decimal importe, int? idPuntoVenta = null, int? idCliente = null) =>
        new(
            idPuntoVenta ?? ctx.IdPuntoVenta, idCliente, "TX", null, null,
            [new PagoDeVenta(ctx.IdMedioEfectivo, importe, null, 0m)], null, null, idPresupuestoOrigen);

    // ---- task 3.2/3.3: venta fantasma 400 SIEMPRE, las dos redes del PRE ----------------------------

    [Fact]
    public async Task UnaVentaConElTipoPreSembradoInactivoEsRechazada400SinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaConElTipoPreSembradoInactivoEsRechazada400SinEscribirNada));

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "PRE", null,
            [new LineaDeVenta(ctx.IdArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)], null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tipo_comprobante_invalido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(0, await db.MovimientosStock.CountAsync());
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync());
    }

    /// <summary>Mutation target 23 (task 3.2): la RED 2 (la cláusula del resolver) tiene que
    /// atrapar un tipo <c>afecta_stock = false</c> INDEPENDIENTEMENTE de la red 1 (el <c>PRE</c>
    /// sembrado sigue inactivo acá, sin tocarlo) — un tipo activo, no fiscal, clase venta, fuera de
    /// banda, con <c>afecta_stock = false</c>, todavía tiene que ser rechazado.</summary>
    [Fact]
    public async Task UnTipoDeVentaFueraDeBandaConAfectaStockFalseEsRechazadoAunqueEsteActivo()
    {
        var ctx = await PrepararAsync(nameof(UnTipoDeVentaFueraDeBandaConAfectaStockFalseEsRechazadoAunqueEsteActivo));

        // tipos_comprobante es [global] (ADR-11), sin id_tenant — RLS exige el modo Plataforma,
        // nunca Tenant, para escribirlo (mismo criterio que InicializadorDeBaseDeDatos, el único
        // otro escritor de esta tabla).
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.TiposComprobante.Add(new TipoComprobante
            {
                Codigo = "ZZZ", Nombre = "Tipo fuera de banda", Clase = ClaseComprobante.Venta,
                Letra = null, Signo = 1, EsFiscal = false, DiscriminaIva = false, AfectaStock = false, Activo = true,
                CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "ZZZ", null,
            [new LineaDeVenta(ctx.IdArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)], null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tipo_comprobante_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.14: fidelidad del precio congelado — fixture DISCRIMINANTE -------------------------

    /// <summary>Mutation targets 24-28 (mutation-proof-tests rule 11): el fixture NUNCA deja que
    /// cotizado y actual coincidan — lista de precio, oferta, alícuota y descripción cambian TODOS
    /// después de enviar, antes de convertir. Si cualquiera de los seis campos congelados se
    /// re-derivara del catálogo de hoy, este test lo detecta.</summary>
    [Fact]
    public async Task LaConversionRespetaElPrecioLaOfertaYLaAlicuotaCongeladosTrasCambiosPosterioresAlEnvio()
    {
        var ctx = await PrepararAsync(nameof(LaConversionRespetaElPrecioLaOfertaYLaAlicuotaCongeladosTrasCambiosPosterioresAlEnvio));

        var altaOferta = new AltaOferta(
            Nombre: "oferta de conversión", IdEmpresa: null, IdArticulo: ctx.IdArticulo, IdGrupo: null, IdCategoria: null,
            FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
            CantidadMinima: null, PrecioUnitario: null, Porcentaje: 10m, ImporteFijo: null,
            Prioridad: 0, Acumulable: false);
        var respuestaOferta = await ctx.Admin.PostAsJsonAsync("/api/ofertas", altaOferta);
        var cuerpoOferta = await respuestaOferta.Content.ReadAsStringAsync();
        Assert.True(respuestaOferta.StatusCode == HttpStatusCode.Created, cuerpoOferta);
        var ofertaCreada = JsonSerializer.Deserialize<OfertaListado>(cuerpoOferta, OpcionesJson)!;
        var idOferta = ofertaCreada.Id;

        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx, cantidad: 2m);
        var item = enviado.Items.Single();
        Assert.Equal(100m, item.PrecioUnitario);
        Assert.Equal(20m, item.Descuento); // 10% de 200
        Assert.Equal(180m, item.Total);
        Assert.Equal(idOferta, item.IdOferta);
        Assert.Equal(ctx.IdAlicuotaIva, item.IdAlicuotaIva);
        Assert.Equal(21m, item.PorcentajeIva);

        // Cambia TODO lo que la conversión debería IGNORAR: el precio de lista, la oferta
        // (desactivada), la alícuota (21 → 10.5) y el nombre del artículo.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var precio = await db.Precios.FirstAsync(p => p.IdArticulo == ctx.IdArticulo);
            precio.Monto = 130m;

            var oferta = await db.Ofertas.FirstAsync(o => o.Id == idOferta);
            oferta.Activo = false;

            var alicuota105 = await db.AlicuotasIva.FirstAsync(a => a.Nombre == "10.5%");

            var articulo = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo);
            articulo.IdAlicuotaIva = alicuota105.Id;
            articulo.Nombre = "nombre-cambiado";

            await db.SaveChangesAsync();
        }

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 180m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        var itemEmitido = emitido.Items.Single();
        Assert.Equal("nombre-original", itemEmitido.Descripcion);
        Assert.Equal(100m, itemEmitido.PrecioUnitario);
        Assert.Equal(20m, itemEmitido.Descuento);
        Assert.Equal(180m, itemEmitido.Total);
        Assert.Equal(ctx.IdListaPrecio, itemEmitido.IdListaPrecio);
        Assert.Equal(idOferta, itemEmitido.IdOferta);
        Assert.Equal(ctx.IdAlicuotaIva, itemEmitido.IdAlicuotaIva);
        Assert.Equal(21m, itemEmitido.PorcentajeIva);
        Assert.Equal(enviado.Id, emitido.IdPresupuestoOrigen);
    }

    // ---- task 3.15: el costo se congela a HOY, nunca a la cotización --------------------------------

    [Fact]
    public async Task LaConversionCongelaElCostoDeHoyNoElDeLaCotizacion()
    {
        var ctx = await PrepararAsync(nameof(LaConversionCongelaElCostoDeHoyNoElDeLaCotizacion));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx, cantidad: 1m);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var articulo = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo);
            articulo.CostoNominal = 95m; // era 80m al cotizar (PrepararAsync)
            await db.SaveChangesAsync();
        }

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 100m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        await using var dbLectura = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var itemPersistido = await dbLectura.ItemsComprobanteVenta.FirstAsync(i => i.IdComprobanteVenta == emitido.Id);
        Assert.Equal(95m, itemPersistido.CostoUnitario);
    }

    // ---- task 3.16: vencido rechazado, con el borde -03:00 -----------------------------------------

    [Fact]
    public async Task LaConversionDeUnPresupuestoVencidoEsRechazada409PresupuestoVencido()
    {
        var ctx = await PrepararAsync(nameof(LaConversionDeUnPresupuestoVencidoEsRechazada409PresupuestoVencido));

        // Vence mañana (aceptado al enviar) — lo vencemos por SQL directo para no depender de un
        // segundo RelojFijo sobre este fixture ya sembrado con DateTimeOffset.UtcNow.
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx, vencimiento: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var presupuesto = await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id);
            presupuesto.Vencimiento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);
            await db.SaveChangesAsync();
        }

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 100m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_vencido", problema.GetProperty("codigo").GetString());

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await dbFinal.ComprobantesVenta.CountAsync());
    }

    // ---- task 3.17: convertir × convertir — un 201, un 409, número quemado, cero escritura del perdedor --

    private sealed class InterceptorDeRendezvousEnLaSegundaTransaccion : DbTransactionInterceptor
    {
        private readonly HashSet<object> _contextosVistos = [];
        private readonly TaskCompletionSource _primeraLlego = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _segundaLlego = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            bool esLaSegundaTransaccionDeEsteContexto;
            lock (_contextosVistos)
            {
                esLaSegundaTransaccionDeEsteContexto = eventData.Context is not null
                    && !_contextosVistos.Add(eventData.Context);
            }

            if (esLaSegundaTransaccionDeEsteContexto)
            {
                if (!_primeraLlego.TrySetResult())
                {
                    _segundaLlego.TrySetResult();
                }
                else
                {
                    await _segundaLlego.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }

            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>Mutation target 35 (task 3.17, OD9/T6): el interceptor fuerza que AMBAS
    /// conversiones ya hayan reservado su número de venta (mini-transacción del asignador) antes
    /// de que cualquiera intente el UPDATE guardado de <c>presupuestos</c> — así el perdedor de la
    /// carrera queda demostrado escribiendo CERO filas (ni comprobante, ni items, ni stock, ni CC)
    /// mientras igual quema un número de la serie de venta (OD9/T6, aceptado con registro).</summary>
    [Fact]
    public async Task LaCarreraConvertirXConvertirDaUn201YUn409ConNumeroQuemadoYCeroEscrituraDelPerdedor()
    {
        // Setup SECUENCIAL contra un factory PLANO (sin interceptor) — enviar un presupuesto
        // también abre dos transacciones por request (mini-tx del asignador + la de
        // EjecutarEnvioAsync); si el interceptor de rendezvous viera ESE par, se quedaría
        // esperando un compañero de carrera que nunca llega (timeout). El interceptor se
        // instala DESPUÉS, sobre un factory nuevo, atado SOLO a las dos llamadas concurrentes.
        var ctx = await PrepararAsync(nameof(LaCarreraConvertirXConvertirDaUn201YUn409ConNumeroQuemadoYCeroEscrituraDelPerdedor));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        var interceptor = new InterceptorDeRendezvousEnLaSegundaTransaccion();
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteA = await LoginAsync(factory, ctx);
        using var clienteB = await LoginAsync(factory, ctx);

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 200m);
        var tareaA = clienteA.PostAsJsonAsync("/api/ventas", conversion);
        var tareaB = clienteB.PostAsJsonAsync("/api/ventas", conversion);
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        var ganadores = respuestas.Count(r => r.StatusCode == HttpStatusCode.Created);
        var perdedores = respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, ganadores);
        Assert.Equal(1, perdedores);

        var perdedor = respuestas.First(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await perdedor.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_ya_convertido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        // Ganador: exactamente UN comprobante ligado al presupuesto.
        Assert.Equal(1, await db.ComprobantesVenta.CountAsync(c => c.IdPresupuestoOrigen == enviado.Id));
        // El presupuesto quedó convertido — terminal.
        Assert.Equal(EstadoPresupuesto.Convertido, (await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id)).Estado);

        // El número quemado: la venta SIGUIENTE en el mismo PV salta el hueco del perdedor.
        var siguienteEnviado = await CrearYEnviarAsync(ctx.Admin, ctx);
        var siguienteConversion = SolicitudDeConversion(ctx, siguienteEnviado.Id, importe: 200m);
        var siguienteRespuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", siguienteConversion);
        var siguienteCuerpo = await siguienteRespuesta.Content.ReadAsStringAsync();
        Assert.True(siguienteRespuesta.StatusCode == HttpStatusCode.Created, siguienteCuerpo);
        var siguienteEmitido = JsonSerializer.Deserialize<ComprobanteEmitido>(siguienteCuerpo, OpcionesJson)!;
        Assert.Equal(3, siguienteEmitido.Numero); // 1 = ganador, 2 = quemado por el perdedor, 3 = este
    }

    // ---- task 3.18 (CONFLICT #3): cross-PV rechazado --------------------------------------------

    [Fact]
    public async Task LaConversionEnOtroPuntoDeVentaEsRechazada400PuntoVentaNoCoincide()
    {
        var ctx = await PrepararAsync(nameof(LaConversionEnOtroPuntoDeVentaEsRechazada400PuntoVentaNoCoincide));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx, idPuntoVenta: ctx.IdPuntoVenta);

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 200m, idPuntoVenta: ctx.IdPuntoVenta2);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("punto_venta_no_coincide", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync());
        Assert.Equal(EstadoPresupuesto.Enviado, (await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id)).Estado);
    }

    // ---- task 3.19: lineas_no_admitidas + cliente en conflicto (mutation target 36) ----------------

    [Fact]
    public async Task LineasEnLaSolicitudDeConversionSonRechazadas400LineasNoAdmitidas()
    {
        var ctx = await PrepararAsync(nameof(LineasEnLaSolicitudDeConversionSonRechazadas400LineasNoAdmitidas));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        var conversion = new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null, [new LineaDeVenta(ctx.IdArticulo2, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 200m, null, 0m)], null, null, enviado.Id);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lineas_no_admitidas", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnIdClienteEnConflictoConElDelPresupuestoEsRechazado400ClienteNoCoincide()
    {
        var ctx = await PrepararAsync(nameof(UnIdClienteEnConflictoConElDelPresupuestoEsRechazado400ClienteNoCoincide));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        int idOtroCliente;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
            var otroCliente = new Cliente
            {
                IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "otro-cliente",
                IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = 0,
                CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(otroCliente);
            await db.SaveChangesAsync();
            idOtroCliente = otroCliente.Id;
        }

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 200m, idCliente: idOtroCliente);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("cliente_no_coincide", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.9/3.20 (CONFLICT #4, mutation target 30): totales desincronizados ------------------

    [Fact]
    public async Task UnRawUpdateQueDesincronizaElTotalDelHeaderEsRechazado409PresupuestoInconsistente()
    {
        var ctx = await PrepararAsync(nameof(UnRawUpdateQueDesincronizaElTotalDelHeaderEsRechazado409PresupuestoInconsistente));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var presupuesto = await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id);
            presupuesto.Total = 999999m; // desincronizado a mano — nunca lo que recomputan los items
            await db.SaveChangesAsync();
        }

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 200m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("presupuesto_inconsistente", problema.GetProperty("codigo").GetString());

        await using var dbFinal = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await dbFinal.ComprobantesVenta.CountAsync());
    }

    // ---- task 3.21: cero statements extra — RED 1, estructural (mutation target 34) ----------------

    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public Ways.Domain.Usuarios.RolConocido Rol => Ways.Domain.Usuarios.RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    /// <summary>Mutation target 34 (task 3.21): una venta SIN <c>idPresupuestoOrigen</c> tiene
    /// que emitir EXACTAMENTE la misma cantidad de consultas que antes de esta slice.
    /// <c>ServicioDeVentas</c> instanciado DIRECTO (mismo criterio que
    /// <c>VentasCheckoutTests.EmitirYContarConsultasAsync</c>) — sin pasar por HTTP/login, así
    /// el conteo aísla exclusivamente el checkout, sin el ruido de aprovisionamiento. Vive en ESTE
    /// archivo (co-ubicada con la rama del snapshot que introduce el riesgo), no solo en
    /// <c>VentasCheckoutTests</c> (INTOCADO — RED 1 de esa non-regression).
    ///
    /// Honestidad documental (judgment-day slice-3, juez B, re-documentado): <c>ContadorDeComandos</c>
    /// solo ve <c>ReaderExecuting[Async]</c> del pipeline de EF — es CIEGO a SQL crudo corrido vía
    /// <c>ExecuteScalarAsync</c> (así corre <c>EscriturasDePresupuesto.MarcarConvertidoAsync</c>),
    /// así que este contador por sí solo NO distingue "el bloque de conversión nunca corre" de "el
    /// bloque corre pero su statement crudo es invisible acá" — sigue probando el pipeline EF (16
    /// consultas, sin cambios), pero la red REAL de "cero statements extra para una venta común" es
    /// el guard ESTRUCTURAL del <c>if (plan.IdPresupuestoOrigen is { } ...)</c>, verificado por
    /// texto fuente en
    /// <c>ServicioDeVentasPosicionDeConversionTests.LaLlamadaAMarcarConvertidoAsyncNuncaOcurreFueraDelGuardNuloDeIdPresupuestoOrigen</c>
    /// (<c>tests/Ways.Application.Tests/Ventas</c>).</summary>
    [Fact]
    public async Task UnaVentaComunSigueEmitiendoDieciseisConsultasConLaRamaDelSnapshotPresente()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaComunSigueEmitiendoDieciseisConsultasConLaRamaDelSnapshotPresente));

        var contador = new ContadorDeComandos();
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
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
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<Ways.Domain.Caja.EstadoTurno>("estado_turno");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, ctx.IdUsuarioAdmin);
        var servicioDePrecios = new Ways.Application.Precios.ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var lectorDeMovimientos = new Ways.Application.Caja.LectorDeMovimientosDelTurno(db);
        var servicioDeTurnos = new Ways.Application.Caja.ServicioDeTurnos(db, reloj, contexto, lectorDeMovimientos);
        var servicioDeLotes = new Ways.Application.Stock.ServicioDeLotes(db, reloj, contexto);
        var servicioDeVentas = new ServicioDeVentas(db, reloj, contexto, servicioDeOfertas, servicioDeTurnos, servicioDeLotes);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "TX", null,
            [new LineaDeVenta(ctx.IdArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)], null, null);

        await servicioDeVentas.EmitirAsync(solicitud);

        Assert.Equal(16, contador.Consultas);
    }

    // ---- task 3.22: round-trip de IdPresupuestoOrigen + carrera de índice único con presupuestos DISTINTOS --

    [Fact]
    public async Task ElRoundTripDeIdPresupuestoOrigenYDosConversionesDeDistintosPresupuestosSucedenAmbas()
    {
        var ctx = await PrepararAsync(nameof(ElRoundTripDeIdPresupuestoOrigenYDosConversionesDeDistintosPresupuestosSucedenAmbas));
        var enviadoA = await CrearYEnviarAsync(ctx.Admin, ctx);
        var enviadoB = await CrearYEnviarAsync(ctx.Admin, ctx);

        var tareaA = ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudDeConversion(ctx, enviadoA.Id, importe: 200m));
        var tareaB = ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudDeConversion(ctx, enviadoB.Id, importe: 200m));
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var emitidoA = (await respuestas[0].Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        var emitidoB = (await respuestas[1].Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        Assert.Equal(enviadoA.Id, emitidoA.IdPresupuestoOrigen);
        Assert.Equal(enviadoB.Id, emitidoB.IdPresupuestoOrigen);
        Assert.NotEqual(emitidoA.Id, emitidoB.Id);

        // Reprint — el round-trip también sobrevive a una relectura, no solo a la respuesta de
        // creación.
        var reimpreso = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitidoA.Id}", OpcionesJson);
        Assert.Equal(enviadoA.Id, reimpreso!.IdPresupuestoOrigen);
    }

    // ---- spec: anular la venta convertida NO reabre el presupuesto (OD8/T1) ------------------------

    [Fact]
    public async Task AnularLaVentaConvertidaDejaElPresupuestoConvertidoYUnLinkIntacto()
    {
        var ctx = await PrepararAsync(nameof(AnularLaVentaConvertidaDejaElPresupuestoConvertidoYUnLinkIntacto));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudDeConversion(ctx, enviado.Id, importe: 200m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var emitido = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpoAnulacion = await anulacion.Content.ReadAsStringAsync();
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var presupuesto = await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id);
        Assert.Equal(EstadoPresupuesto.Convertido, presupuesto.Estado);

        var comprobante = await db.ComprobantesVenta.FirstAsync(c => c.Id == emitido.Id);
        Assert.Equal(EstadoComprobante.Anulado, comprobante.Estado);
        Assert.Equal(enviado.Id, comprobante.IdPresupuestoOrigen); // el link NUNCA se limpia (T1)

        // Una segunda conversión del MISMO presupuesto sigue rechazada — convertido es terminal.
        var segundaConversion = await ctx.Admin.PostAsJsonAsync("/api/ventas", SolicitudDeConversion(ctx, enviado.Id, importe: 200m));
        Assert.Equal(HttpStatusCode.Conflict, segundaConversion.StatusCode);
        var problema = await segundaConversion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_ya_convertido", problema.GetProperty("codigo").GetString());
    }

    // ---- para-venta: shape exacto + 409 de un presupuesto vencido -----------------------------------

    [Fact]
    public async Task ParaVentaDevuelveElShapeCongeladoDeUnPresupuestoEnviado()
    {
        var ctx = await PrepararAsync(nameof(ParaVentaDevuelveElShapeCongeladoDeUnPresupuestoEnviado));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx, cantidad: 3m);

        var paraVenta = await ctx.Admin.GetFromJsonAsync<PresupuestoParaVenta>(
            $"/api/presupuestos/{enviado.Id}/para-venta", OpcionesJson);

        Assert.NotNull(paraVenta);
        Assert.Equal(enviado.Id, paraVenta!.IdPresupuesto);
        Assert.Equal(enviado.Numero, paraVenta.Numero);
        Assert.Equal(ctx.IdPuntoVenta, paraVenta.IdPuntoVenta);
        Assert.Equal(ctx.IdCliente, paraVenta.IdCliente);
        Assert.False(paraVenta.Vencido);
        Assert.True(paraVenta.Convertible);
        Assert.Equal(enviado.Total, paraVenta.Total);
        Assert.Single(paraVenta.Items);
    }

    [Fact]
    public async Task ParaVentaDeUnPresupuestoVencidoEsRechazada409PresupuestoVencido()
    {
        var ctx = await PrepararAsync(nameof(ParaVentaDeUnPresupuestoVencidoEsRechazada409PresupuestoVencido));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var presupuesto = await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id);
            presupuesto.Vencimiento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);
            await db.SaveChangesAsync();
        }

        var respuesta = await ctx.Admin.GetAsync($"/api/presupuestos/{enviado.Id}/para-venta");
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_vencido", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.16 (borrador/anulado): conversión de un presupuesto no-enviado ----------------------

    [Fact]
    public async Task LaConversionDeUnPresupuestoBorradorEsRechazada409PresupuestoNoConvertible()
    {
        var ctx = await PrepararAsync(nameof(LaConversionDeUnPresupuestoBorradorEsRechazada409PresupuestoNoConvertible));
        var borrador = await CrearBorradorAsync(ctx.Admin, ctx);

        var conversion = SolicitudDeConversion(ctx, borrador.Id, importe: 200m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("presupuesto_no_convertible", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task LaConversionDeUnPresupuestoAnuladoEsRechazada409PresupuestoNoConvertible()
    {
        var ctx = await PrepararAsync(nameof(LaConversionDeUnPresupuestoAnuladoEsRechazada409PresupuestoNoConvertible));
        var creado = await CrearBorradorAsync(ctx.Admin, ctx);
        var anulacion = await ctx.Admin.PostAsync($"/api/presupuestos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var conversion = SolicitudDeConversion(ctx, creado.Id, importe: 200m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", conversion);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("presupuesto_no_convertible", problema.GetProperty("codigo").GetString());
    }

    // ---- judgment-day slice-3 (juez B): mutantes sobrevivientes del WHERE guardado ------------------

    /// <summary>Mutation targets 31-33 (judgment-day slice-3, juez B, CRITICAL+MAJOR): las tres
    /// cláusulas del <c>WHERE</c> de <see cref="EscriturasDePresupuesto.MarcarConvertidoAsync"/>
    /// (<c>estado = 'enviado'</c>, <c>vencimiento >= $hoy</c>, <c>id_punto_venta = $pv</c>)
    /// sobrevivían borradas porque <c>ResolverConversionDesdePresupuestoAsync</c> las eclipsa
    /// secuencialmente — cualquier fila que llegue hasta el UPDATE guardado YA pasó ese
    /// pre-chequeo con los mismos tres predicados, así que un mutante que borra una cláusula del
    /// UPDATE nunca se nota a través del servicio completo (mutation-proof-tests regla 3: rutear
    /// POR DEBAJO del confound). Los tres tests de acá llaman
    /// <see cref="EscriturasDePresupuesto.MarcarConvertidoAsync"/> DIRECTO, contra una conexión
    /// cruda (mismo patrón que <c>AbrirConexionCrudaAsync</c> usa en las pruebas de RLS de este
    /// mismo archivo de tests), nunca a través de
    /// <c>ServicioDeVentas</c>/<c>ResolverConversionDesdePresupuestoAsync</c> — así cada cláusula
    /// queda probada en aislamiento, sin el pre-chequeo por delante. La carrera real (pre-check
    /// pasa, la fila cambia DESPUÉS, el UPDATE guardado es la red) está más abajo, la JOYA.</summary>
    [Fact]
    public async Task MarcarConvertidoAsyncDevuelveCeroFilasSiElEstadoYaNoEsEnviadoAlMomentoDelUpdate()
    {
        var ctx = await PrepararAsync(nameof(MarcarConvertidoAsyncDevuelveCeroFilasSiElEstadoYaNoEsEnviadoAlMomentoDelUpdate));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        var anulacion = await ctx.Admin.PostAsync($"/api/presupuestos/{enviado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var convertido = await EscriturasDePresupuesto.MarcarConvertidoAsync(
            conexion, null, ctx.IdTenant, enviado.Id, ctx.IdPuntoVenta,
            DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(convertido);
    }

    /// <summary>Mutation target 32 (judgment-day slice-3, juez B): la cláusula <c>vencimiento >=
    /// $hoy</c>, probada pasándole a <see cref="EscriturasDePresupuesto.MarcarConvertidoAsync"/> un
    /// <c>hoyEnZonaDelPuntoVenta</c> POSTERIOR al vencimiento real — la fila sigue en
    /// <c>enviado</c>, sin tocar por SQL, así que si esta cláusula estuviera borrada el UPDATE la
    /// convertiría igual.</summary>
    [Fact]
    public async Task MarcarConvertidoAsyncDevuelveCeroFilasSiHoyYaPasoElVencimiento()
    {
        var ctx = await PrepararAsync(nameof(MarcarConvertidoAsyncDevuelveCeroFilasSiHoyYaPasoElVencimiento));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var convertido = await EscriturasDePresupuesto.MarcarConvertidoAsync(
            conexion, null, ctx.IdTenant, enviado.Id, ctx.IdPuntoVenta,
            enviado.Vencimiento!.Value.AddDays(1), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(convertido);
    }

    /// <summary>Mutation target 33 (judgment-day slice-3, juez B): la cláusula <c>id_punto_venta =
    /// $pv</c>, probada pasándole a <see cref="EscriturasDePresupuesto.MarcarConvertidoAsync"/> el
    /// PV equivocado — el presupuesto sigue vigente y sin tocar en <c>ctx.IdPuntoVenta</c>.</summary>
    [Fact]
    public async Task MarcarConvertidoAsyncDevuelveCeroFilasSiElPuntoVentaNoCoincide()
    {
        var ctx = await PrepararAsync(nameof(MarcarConvertidoAsyncDevuelveCeroFilasSiElPuntoVentaNoCoincide));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx, idPuntoVenta: ctx.IdPuntoVenta);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var convertido = await EscriturasDePresupuesto.MarcarConvertidoAsync(
            conexion, null, ctx.IdTenant, enviado.Id, ctx.IdPuntoVenta2,
            DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(convertido);
    }

    /// <summary>Pausa la SEGUNDA transacción abierta por el <see cref="DbContext"/> de la
    /// conversión (la de <c>EjecutarTransaccionAsync</c> — la primera es la mini-transacción del
    /// asignador de número) justo al abrirse, ANTES de que corra
    /// <see cref="EscriturasDePresupuesto.MarcarConvertidoAsync"/>. Variante de un solo
    /// rendez-vous de <see cref="InterceptorDeRendezvousEnLaSegundaTransaccion"/> (arriba, task
    /// 3.17): acá no empareja dos transacciones concurrentes, solo bloquea UNA hasta que el test la
    /// libera — así se puede correr una operación distinta (la anulación) a completitud, con commit
    /// real, mientras la conversión sigue parada.</summary>
    private sealed class InterceptorDePausaEnLaSegundaTransaccion : DbTransactionInterceptor
    {
        private readonly HashSet<object> _contextosVistos = [];
        private readonly TaskCompletionSource _pausada = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reanudar = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EsperarPausaAsync() => _pausada.Task;

        public void Reanudar() => _reanudar.TrySetResult();

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            bool esLaSegundaTransaccionDeEsteContexto;
            lock (_contextosVistos)
            {
                esLaSegundaTransaccionDeEsteContexto = eventData.Context is not null
                    && !_contextosVistos.Add(eventData.Context);
            }

            if (esLaSegundaTransaccionDeEsteContexto)
            {
                _pausada.TrySetResult();
                await _reanudar.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>LA JOYA (judgment-day slice-3, juez B, targets 31/35): la carrera TOCTOU real —
    /// anular×convertir — probada de punta a punta con el interceptor determinista de arriba. El
    /// pre-chequeo de <c>ResolverConversionDesdePresupuestoAsync</c> lee <c>enviado</c> bien atrás,
    /// ANTES de que exista cualquier transacción — en ese momento la anulación todavía no corrió.
    /// La conversión PAUSA justo al abrir su transacción de escritura; mientras está parada, la
    /// anulación corre y COMITEA completo, dejando el presupuesto en <c>anulado</c>. Al reanudar,
    /// el UPDATE guardado (bajo la MISMA cláusula <c>estado = 'enviado'</c> del target 31) ya no
    /// matchea la fila — 0 filas, <c>ExigirCausaDelRechazoAsync</c> bajo <c>FOR UPDATE</c> deriva
    /// <c>presupuesto_no_convertible</c> (el estado real, <c>anulado</c>, no es <c>convertido</c>
    /// ni <c>enviado</c>), y CERO venta se crea — la prueba de que la cláusula <c>estado</c> del
    /// UPDATE guardado es la RED REAL de producción, nunca el pre-chequeo (que para esta fila YA
    /// había pasado).</summary>
    [Fact]
    public async Task LaCarreraAnularXConvertirDejaCeroVentasYElUpdateGuardeadoRechazaConPresupuestoNoConvertible()
    {
        var ctx = await PrepararAsync(nameof(LaCarreraAnularXConvertirDejaCeroVentasYElUpdateGuardeadoRechazaConPresupuestoNoConvertible));
        var enviado = await CrearYEnviarAsync(ctx.Admin, ctx);

        var interceptor = new InterceptorDePausaEnLaSegundaTransaccion();
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteConversion = await LoginAsync(factory, ctx);
        using var clienteAnular = await LoginAsync(factory, ctx);

        var conversion = SolicitudDeConversion(ctx, enviado.Id, importe: 200m);
        var tareaConversion = clienteConversion.PostAsJsonAsync("/api/ventas", conversion);

        await interceptor.EsperarPausaAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var anulacion = await clienteAnular.PostAsync($"/api/presupuestos/{enviado.Id}/anular", null);
        var cuerpoAnulacion = await anulacion.Content.ReadAsStringAsync();
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);

        interceptor.Reanudar();
        var respuestaConversion = await tareaConversion;
        var cuerpoConversion = await respuestaConversion.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, respuestaConversion.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoConversion, OpcionesJson);
        Assert.Equal("presupuesto_no_convertible", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync(c => c.IdPresupuestoOrigen == enviado.Id));
        Assert.Equal(EstadoPresupuesto.Anulado, (await db.Presupuestos.FirstAsync(p => p.Id == enviado.Id)).Estado);
    }
}
