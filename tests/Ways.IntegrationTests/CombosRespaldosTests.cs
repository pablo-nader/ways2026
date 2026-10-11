using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Combos;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// Los respaldos de esquema de los combos alcanzados por HTTP, y el lock por tenant de sus escrituras. Cada respaldo
/// se prueba con un rendezvous determinístico: una conexión cruda sostiene SIN comitear una fila que choca con la que
/// va a escribir el endpoint (así el pre-chequeo del servicio no la ve y el INSERT queda esperando en
/// <c>pg_locks</c>), y recién al comitear el endpoint recibe el 23505 que traduce <c>ManejadorDeErrores</c>. La
/// traducción por constraint está además en <c>ManejadorDeErroresCombosTests</c> y que cada índice dispara 23505 de
/// verdad, en <see cref="CombosEsquemaTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosRespaldosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static AltaCombo Alta(int? codigo, int a, int b) =>
        new(codigo, "Combo", 5000m, null, [new ComponenteDeSolicitudDeCombo(a, null, 1m), new ComponenteDeSolicitudDeCombo(b, null, 1m)]);

    private static async Task<ComboListado> CrearAsync(HttpClient cliente, AltaCombo alta)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/combos", alta, OpcionesJson);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<ComboListado>(OpcionesJson))!;
    }

    private async Task<(int A, int B)> DosArticulosAsync(Entorno e) =>
        (await apoyo.SembrarArticuloAsync(e, "a", ValoresBase(e)), await apoyo.SembrarArticuloAsync(e, "b", ValoresBase(e)));

    /// <summary>ux_combos_codigo: una fila de otra transacción con el mismo código, todavía sin comitear. El pre-chequeo
    /// de código no la ve, el INSERT espera el índice y, al comitear la otra, falla con 23505: 409 con el código propio
    /// (no <c>codigo_duplicado</c>, el brazo genérico que también matchea "_codigo").</summary>
    [Fact]
    public async Task UnCodigoOcupadoPorUnaFilaSinComitearDa409ComboCodigoDuplicadoPorElIndice()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCodigoOcupadoPorUnaFilaSinComitearDa409ComboCodigoDuplicadoPorElIndice));
        var (a, b) = await DosArticulosAsync(e);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion,
            "INSERT INTO combos (id_tenant, codigo, nombre, precio, created_at, updated_at) VALUES ($1, 42, 'fuera de banda', 10, now(), now())",
            e.IdTenant);

        var alta = e.Admin.PostAsJsonAsync("/api/combos", Alta(42, a, b), OpcionesJson);

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El alta nunca se observó esperando el índice único: la prueba no prueba el respaldo de esquema.");
        Assert.False(alta.IsCompleted);

        await transaccion.CommitAsync();

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("combo_codigo_duplicado", (await ProblemaAsync(respuesta)).Codigo);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.Equal(["fuera de banda"], await db.Combos.IgnoreQueryFilters().Where(c => c.IdTenant == e.IdTenant).Select(c => c.Nombre).ToListAsync());
        Assert.Empty(await db.CombosComponentes.IgnoreQueryFilters().Where(c => c.IdTenant == e.IdTenant).ToListAsync());
    }

    /// <summary>ux_combos_componentes_articulo / _familia: una fila de componente de otra transacción, sin comitear, para
    /// el mismo combo y el mismo artículo (o familia) que el PUT va a insertar. El PUT espera y falla con 23505 al
    /// comitear la otra: 409 <c>combo_componente_duplicado</c>, y la transacción del PUT se revierte entera (el
    /// conjunto anterior sigue en la base).</summary>
    [Theory]
    [InlineData("articulo")]
    [InlineData("familia")]
    public async Task UnComponenteRepetidoPorUnaFilaSinComitearDa409ComboComponenteDuplicadoYRevierteElPut(string tipo)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnComponenteRepetidoPorUnaFilaSinComitearDa409ComboComponenteDuplicadoYRevierteElPut) + tipo);
        var (a, b) = await DosArticulosAsync(e);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Fam");
        var combo = await CrearAsync(e.Admin, Alta(1, a, b));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        var (columna, valor) = tipo == "articulo"
            ? ("id_articulo", await apoyo.SembrarArticuloAsync(e, "c", ValoresBase(e)))
            : ("id_familia", familia);
        var nuevo = tipo == "articulo"
            ? new ComponenteDeSolicitudDeCombo(valor, null, 1m)
            : new ComponenteDeSolicitudDeCombo(null, valor, 1m);

        await EjecutarAsync(
            sostenedor, transaccion,
            $"INSERT INTO combos_componentes (id_tenant, id_combo, {columna}, cantidad) VALUES ($1, $2, $3, 1)",
            e.IdTenant, combo.Id, valor);

        var put = e.Admin.PutAsJsonAsync(
            $"/api/combos/{combo.Id}",
            new EdicionCombo(null, "Editado", 4000m, null, [new ComponenteDeSolicitudDeCombo(a, null, 1m), nuevo]),
            OpcionesJson);

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El PUT nunca se observó esperando el índice único: la prueba no prueba el respaldo de esquema.");
        Assert.False(put.IsCompleted);

        await transaccion.CommitAsync();

        var respuesta = await put.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("combo_componente_duplicado", (await ProblemaAsync(respuesta)).Codigo);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var guardado = await db.Combos.IgnoreQueryFilters().SingleAsync(c => c.Id == combo.Id);
        Assert.Equal("Combo", guardado.Nombre);
        var articulos = await db.CombosComponentes.IgnoreQueryFilters()
            .Where(c => c.IdCombo == combo.Id && c.IdArticulo != null).Select(c => c.IdArticulo!.Value).OrderBy(x => x).ToListAsync();
        Assert.Contains(a, articulos);
        Assert.Contains(b, articulos);
    }

    private static async Task<Candado?> EsperandoElLockDeCombosAsync(NpgsqlConnection poll, int idTenant)
    {
        var (alto, bajo) = PartesDeLaClave(ServicioDeCombos.ClaveDeLockDeCombos(idTenant));

        var esperando = await CandadosAdvisoryAsync(
            poll, "NOT granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo);

        return esperando.SingleOrDefault();
    }

    /// <summary>Alta, edición y baja piden el lock de combos del tenant como PRIMERA sentencia de su transacción: con
    /// el lock sostenido por otra conexión quedan esperándolo sin haber concedido ningún otro lock ni haber escrito
    /// nada, y al soltarlo terminan bien. Es lo que serializa la asignación del código y el reemplazo del conjunto.</summary>
    [Theory]
    [InlineData("alta")]
    [InlineData("edicion")]
    [InlineData("baja")]
    public async Task LasEscriturasEsperanElLockDeCombosDelTenantAntesDeTomarNingunOtro(string operacion)
    {
        using var e = await apoyo.PrepararAsync(nameof(LasEscriturasEsperanElLockDeCombosDelTenantAntesDeTomarNingunOtro) + operacion);
        var (a, b) = await DosArticulosAsync(e);
        var existente = await CrearAsync(e.Admin, Alta(1, a, b));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", ServicioDeCombos.ClaveDeLockDeCombos(e.IdTenant));

        var escritura = operacion switch
        {
            "alta" => e.Admin.PostAsJsonAsync("/api/combos", Alta(null, a, b), OpcionesJson),
            "edicion" => e.Admin.PutAsJsonAsync(
                $"/api/combos/{existente.Id}",
                new EdicionCombo(null, "Editado", 4000m, null, [new ComponenteDeSolicitudDeCombo(a, null, 1m), new ComponenteDeSolicitudDeCombo(b, null, 2m)]),
                OpcionesJson),
            _ => e.Admin.DeleteAsync($"/api/combos/{existente.Id}")
        };

        var esperando = await EsperarAsync(
            () => EsperandoElLockDeCombosAsync(poll, e.IdTenant),
            "La escritura nunca se observó esperando el lock de combos del tenant.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(escritura.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.True(respuesta.IsSuccessStatusCode, $"{operacion} terminó con {respuesta.StatusCode}");
    }
}
