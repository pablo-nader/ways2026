using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Ofertas;
using Ways.Application.Pos;
using Ways.Application.Ventas;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.CombosApoyo;

namespace Ways.IntegrationTests;

/// <summary>
/// Presupuesto de consultas de los combos, con la misma técnica que <c>VentasCheckoutTests</c>: el servicio real contra
/// Postgres con un <see cref="DbCommandInterceptor"/> que cuenta cada comando que abre un lector. El total pinea el techo
/// de cada camino; los contadores por tabla dicen cuáles de esas consultas son las de combos:
/// <list type="bullet">
/// <item>CANDIDATOS: la consulta que une <c>combos</c> con <c>combos_componentes</c> (la misma de la venta online).</item>
/// <item>IDS: la lectura de <c>combos</c> por id que valida los ids que trae el dispositivo (sin componentes).</item>
/// </list>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosConsultasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly CombosApoyo apoyo = new(fixture);

    private sealed class ContadorDeConsultas : DbCommandInterceptor
    {
        private readonly List<string> textos = [];

        public int Total => textos.Count;

        public int TocanLaTabla(string tabla) =>
            textos.Count(t => Regex.IsMatch(t, $@"\b{tabla}\b", RegexOptions.IgnoreCase));

        public int Candidatos => textos.Count(t =>
            Regex.IsMatch(t, @"\bcombos\b", RegexOptions.IgnoreCase) && t.Contains("combos_componentes", StringComparison.OrdinalIgnoreCase));

        public int Ids => TocanLaTabla("combos") - Candidatos;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            textos.Add(command.CommandText);

            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            textos.Add(command.CommandText);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId, RolConocido rol, int? idDispositivo) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => rol;
        public int? IdTenant { get; } = idTenant;
        public int? IdDispositivo => idDispositivo;
    }

    /// <summary>El cajero de dispositivo que dejó logueado <see cref="CombosApoyo.PrepararAsync"/> y el dispositivo vinculado,
    /// como actor de servicio: lo que la API le pasa al servicio en una sesión de dispositivo.</summary>
    private async Task<ContextoFijo> ActorDeDispositivoAsync(Escenario ctx)
    {
        await using var db = apoyo.Db(ctx.IdTenant);
        var idDispositivo = await db.Dispositivos.Where(d => d.IdPuntoVenta == ctx.IdPuntoVenta).Select(d => d.Id).SingleAsync();
        var idCajero = await db.Usuarios.Where(u => u.NombreUsuario == "cajero-combos").Select(u => u.Id).SingleAsync();

        return new ContextoFijo(ctx.IdTenant, idCajero, RolConocido.Vendedor, idDispositivo);
    }

    private async Task<(Func<SolicitudDeVenta, Task<ComprobanteEmitido>> Emitir, ContadorDeConsultas Contador)> VentasContadasAsync(
        Escenario ctx)
    {
        var contador = new ContadorDeConsultas();
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant), contador);
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var actor = await ActorDeDispositivoAsync(ctx);

        var servicioDePrecios = new Ways.Application.Precios.ServicioDePrecios(db, reloj, actor);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, actor, servicioDePrecios);
        var lectorDeMovimientos = new Ways.Application.Caja.LectorDeMovimientosDelTurno(db);
        var servicioDeTurnos = new Ways.Application.Caja.ServicioDeTurnos(
            db, reloj, actor, lectorDeMovimientos,
            new Ways.Application.Caja.LectorDeResumenDeCierrePorRetiro(db, lectorDeMovimientos),
            new Ways.Application.Caja.LectorDeRendicionDeDispositivos(db),
            new ServicioDeAuditoria(db, reloj, actor));
        var servicioDeLotes = new Ways.Application.Stock.ServicioDeLotes(db, reloj, actor);
        var servicioDeVentas = new ServicioDeVentas(
            db, reloj, actor, servicioDeOfertas, servicioDeTurnos, servicioDeLotes, new ServicioDeAuditoria(db, reloj, actor));

        return (solicitud => servicioDeVentas.EmitirAsync(solicitud), contador);
    }

    private static SolicitudDeVenta Venta(Escenario ctx, long numero, decimal total, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, null, "TX", null, [.. lineas],
            [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)], null, null, NumeroPreasignado: numero);

    private static LineaDeVenta Offline(
        int idArticulo, decimal precio, int? unidades = null, decimal? ahorro = null, int? idCombo = null) =>
        new(idArticulo, 1m, null, PrecioUnitario: precio, DescuentoUnitario: 0m,
            UnidadesConsumidasPorCombo: unidades, AhorroDeCombo: ahorro, IdCombo: idCombo);

    private async Task<(Escenario Ctx, int Pancho, int Coca, int Combo)> EscenarioOfflineAsync(string nombre)
    {
        var ctx = await apoyo.PrepararAsync(nombre, ModoPuntoVenta.Escritorio);
        await ReservarBloqueAsync(ctx);
        var pancho = await apoyo.ArticuloAsync(ctx, "pancho", 3000m);
        var coca = await apoyo.ArticuloAsync(ctx, "coca", 2500m);
        var combo = await apoyo.ComboAsync(ctx, 5000m, [Art(pancho), Art(coca)]);

        return (ctx, pancho, coca, combo);
    }

    // ---- Venta offline ----

    /// <summary>Una venta offline de 2 unidades con datos de combo paga DOS consultas de combos y ninguna más que una venta
    /// de 2 unidades sin ellos: los CANDIDATOS (la detección del servidor, que solo sirve para comparar) y los IDS (validar
    /// el id que trajo el dispositivo). El techo total incluye todo lo demás del checkout.</summary>
    [Fact]
    public async Task UnaVentaOfflineConDatosDeComboPagaLaConsultaDeCandidatosYLaDeIds()
    {
        var (ctx, pancho, coca, combo) = await EscenarioOfflineAsync(nameof(UnaVentaOfflineConDatosDeComboPagaLaConsultaDeCandidatosYLaDeIds));
        var (emitir, contador) = await VentasContadasAsync(ctx);

        var emitido = await emitir(Venta(ctx, 1, 5000m,
            Offline(pancho, 3000m, unidades: 1, ahorro: 272.73m, idCombo: combo),
            Offline(coca, 2500m, unidades: 1, ahorro: 227.27m, idCombo: combo)));

        Assert.Equal(combo, emitido.Items[0].IdCombo);
        Assert.Equal((1, 1), (contador.Candidatos, contador.Ids));
        Assert.Equal(21, contador.Total);
    }

    /// <summary>Con 2 unidades y SIN datos de combo la venta offline paga solo los CANDIDATOS: la consulta de IDS se omite
    /// cuando ninguna línea trae <c>idCombo</c>. La diferencia con el caso de arriba es exactamente esa consulta.</summary>
    [Fact]
    public async Task UnaVentaOfflineDeDosUnidadesSinDatosDeComboPagaSoloLaConsultaDeCandidatos()
    {
        var (ctx, pancho, coca, _) = await EscenarioOfflineAsync(nameof(UnaVentaOfflineDeDosUnidadesSinDatosDeComboPagaSoloLaConsultaDeCandidatos));
        var (emitir, contador) = await VentasContadasAsync(ctx);

        await emitir(Venta(ctx, 1, 5500m, Offline(pancho, 3000m), Offline(coca, 2500m)));

        Assert.Equal((1, 0), (contador.Candidatos, contador.Ids));
        Assert.Equal(20, contador.Total);
    }

    /// <summary>Una venta offline de UNA unidad no paga ninguna consulta de combos (con una unidad ningún combo se
    /// completa). Es la base de comparación de los dos casos de arriba: las 16 de la venta online de una línea
    /// (<c>VentasCheckoutTests</c>) más 3 propias del camino de dispositivo con número pre-asignado, que no son de combos.</summary>
    [Fact]
    public async Task UnaVentaOfflineDeUnaUnidadNoPagaNingunaConsultaDeCombos()
    {
        var (ctx, pancho, _, _) = await EscenarioOfflineAsync(nameof(UnaVentaOfflineDeUnaUnidadNoPagaNingunaConsultaDeCombos));
        var (emitir, contador) = await VentasContadasAsync(ctx);

        await emitir(Venta(ctx, 1, 3000m, Offline(pancho, 3000m)));

        Assert.Equal(0, contador.TocanLaTabla("combos"));
        Assert.Equal(19, contador.Total);
    }

    /// <summary>Cláusula: las líneas de artículos por importe no cuentan para la detección. Una unidad más una línea
    /// por importe sigue sin pagar la consulta de candidatos; la misma venta con dos unidades la paga (caso de arriba).</summary>
    [Fact]
    public async Task UnaUnidadMasUnaLineaPorImporteNoPagaLaConsultaDeCandidatos()
    {
        var (ctx, pancho, _, _) = await EscenarioOfflineAsync(nameof(UnaUnidadMasUnaLineaPorImporteNoPagaLaConsultaDeCandidatos));
        var fiambreria = await apoyo.ArticuloAsync(ctx, "fiambreria", 100m);
        await using (var db = apoyo.Db(ctx.IdTenant))
        {
            await db.Articulos.Where(a => a.Id == fiambreria).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.UnidadVenta, Ways.Domain.Articulos.UnidadVenta.Importe)
                .SetProperty(a => a.AcumulaEnVenta, false));
        }

        var (emitir, contador) = await VentasContadasAsync(ctx);

        var emitido = await emitir(Venta(ctx, 1, 3150m, Offline(pancho, 3000m), new LineaDeVenta(fiambreria, 1m, null, Importe: 150m)));

        Assert.Equal(3150m, emitido.Total);
        Assert.Equal(0, contador.Candidatos);
    }

    // ---- Vista previa ----

    private async Task<(ServicioDeOfertas Servicio, ContadorDeConsultas Contador)> OfertasContadasAsync(Escenario ctx)
    {
        var contador = new ContadorDeConsultas();
        var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant), contador);
        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var actor = await ActorDeDispositivoAsync(ctx);

        return (new ServicioDeOfertas(db, reloj, actor, new Ways.Application.Precios.ServicioDePrecios(db, reloj, actor)), contador);
    }

    private static SolicitudDeResolucion Carrito(Escenario ctx, params (int IdArticulo, decimal Cantidad)[] lineas) =>
        new([.. lineas.Select(l => new LineaDeResolucion(l.IdArticulo, ctx.IdEmpresa, ctx.IdListaPrecio, l.Cantidad))]);

    /// <summary>La vista previa de un documento de 2 unidades suma UNA consulta (los CANDIDATOS) a las 5 de la resolución;
    /// con una sola unidad esa consulta se omite y quedan las 5.</summary>
    [Fact]
    public async Task LaVistaPreviaPagaLaConsultaDeCandidatosSoloConDosOMasUnidades()
    {
        var (ctx, pancho, coca, _) = await EscenarioOfflineAsync(nameof(LaVistaPreviaPagaLaConsultaDeCandidatosSoloConDosOMasUnidades));

        var (conDosUnidades, contadorDeDos) = await OfertasContadasAsync(ctx);
        var resultado = await conDosUnidades.ResolverConCombosAsync(Carrito(ctx, (pancho, 1m), (coca, 1m)));
        Assert.Single(resultado.Combos);
        Assert.Equal((1, 0), (contadorDeDos.Candidatos, contadorDeDos.Ids));
        Assert.Equal(6, contadorDeDos.Total);

        var (conUnaUnidad, contadorDeUna) = await OfertasContadasAsync(ctx);
        await conUnaUnidad.ResolverConCombosAsync(Carrito(ctx, (pancho, 1m)));
        Assert.Equal(0, contadorDeUna.TocanLaTabla("combos"));
        Assert.Equal(5, contadorDeUna.Total);
    }

    // ---- Instantánea ----

    /// <summary>La instantánea con <c>version=2</c> suma UNA consulta de combos (los componentes viajan en esa misma
    /// consulta); el formato original no toca la tabla.</summary>
    [Fact]
    public async Task LaInstantaneaVersionDosPagaUnaConsultaDeCombosYLaOriginalNinguna()
    {
        var (ctx, _, _, _) = await EscenarioOfflineAsync(nameof(LaInstantaneaVersionDosPagaUnaConsultaDeCombosYLaOriginalNinguna));

        async Task<ContadorDeConsultas> ContarAsync(bool soloListaDelConsumidorFinal)
        {
            var contador = new ContadorDeConsultas();
            var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant), contador);
            var reloj = new RelojFijo(DateTimeOffset.UtcNow);
            var actor = await ActorDeDispositivoAsync(ctx);
            var ofertas = new ServicioDeOfertas(db, reloj, actor, new Ways.Application.Precios.ServicioDePrecios(db, reloj, actor));
            var servicio = new ServicioDeInstantaneaDePos(db, reloj, actor, ofertas, new MemoryCache(new MemoryCacheOptions()));

            await servicio.ObtenerAsync(soloListaDelConsumidorFinal);

            return contador;
        }

        Assert.Equal(1, (await ContarAsync(soloListaDelConsumidorFinal: false)).TocanLaTabla("combos"));
        Assert.Equal(0, (await ContarAsync(soloListaDelConsumidorFinal: true)).TocanLaTabla("combos"));
    }
}
