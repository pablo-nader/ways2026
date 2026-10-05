using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Bajas;
using Ways.Application.Familias;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/articulos</c> con <c>idFamilia</c> (doc 10 §3, "Familias de artículos") contra Postgres real: el
/// alta de un artículo DENTRO de una familia existente. Entrar es un cambio de pertenencia: el alta toma el lock
/// de membresía exclusivo, toma como referencia al miembro vivo de menor id, exige que los trece campos
/// compartidos del pedido sean idénticos a los suyos y copia, en la misma transacción, su estado de precios en
/// cada lista fija —el vigente y el pendiente— con una fila de auditoría <c>precio.cambio</c> por cada precio
/// insertado.
///
/// <para>Los tests de locks son rendezvous determinísticos: una conexión cruda sostiene un lock o una
/// escritura sin comitear, el alta queda observada esperando en <c>pg_locks</c> y recién ahí se libera
/// (<c>mutation-proof-tests</c>, regla 13). Dos no observan ninguna espera.
/// <see cref="UnAltaSinFamiliaNoEsperaAlLockDeMembresiaAjeno"/> prueba que no espera: el lock exclusivo ajeno
/// sigue sostenido hasta el final de la prueba y el alta tiene que terminar dentro de 15 segundos.
/// <see cref="DosAltasSimultaneasEnLaMismaFamiliaEntranLasDosConElEstadoDePreciosCompleto"/> lanza dos altas a
/// la vez sin ningún punto de encuentro, así que prueba el resultado y no el orden.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosAltaEnFamiliaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private sealed class RelojReal : IRelojDelSistema
    {
        public DateTimeOffset Ahora => DateTimeOffset.UtcNow;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "contexto-fijo";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => idTenant;
    }

    private static AltaArticulo Alta(
        ValoresCompartidosDeFamilia v, int? idFamilia, string nombre = "nuevo", string? descripcion = null,
        int? idMarca = null, bool activo = true) => new(
        CodigoInterno: null, Nombre: nombre, Descripcion: descripcion, IdArea: v.IdArea, IdCategoria: v.IdCategoria,
        IdMarca: idMarca, IdGrupo: v.IdGrupo, IdProveedorHabitual: v.IdProveedorHabitual,
        IdAlicuotaIva: v.IdAlicuotaIva, UnidadVenta: v.UnidadVenta, UnidadesPorBulto: v.UnidadesPorBulto,
        EsProducto: v.EsProducto, CostoLista: v.CostoLista, DescuentoProveedor: v.DescuentoProveedor,
        CostoNominal: v.CostoNominal, Activo: activo, ControlaLote: v.ControlaLote,
        AcumulaEnVenta: v.AcumulaEnVenta, IdFamilia: idFamilia);

    private static Task<HttpResponseMessage> PostArticuloAsync(HttpClient admin, AltaArticulo alta) =>
        admin.PostAsJsonAsync("/api/articulos", alta, OpcionesJson);

    /// <summary><c>timestamptz</c> guarda microsegundos y <see cref="DateTimeOffset"/> tiene 100 ns de resolución.</summary>
    private static DateTimeOffset AMicrosegundos(DateTimeOffset instante) =>
        new(instante.Ticks - (instante.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), instante.Offset);

    private async Task<int> ContarArticulosAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters().CountAsync(a => a.IdTenant == idTenant);
    }

    private async Task<int> ContarPreciosAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Precios.IgnoreQueryFilters().CountAsync(p => p.IdTenant == idTenant);
    }

    public static TheoryData<string> Columnas()
    {
        var datos = new TheoryData<string>();
        foreach (var columna in ColumnasCompartidas)
        {
            datos.Add(columna);
        }

        return datos;
    }

    private static object Huella(Articulo a) => new
    {
        a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.DisponibleParaTodas, a.IdFamilia,
        Compartidos = ValoresCompartidosDeFamilia.De(a), a.UpdatedAt, a.DeletedAt
    };

    // =================================================================================================
    // Siembra: una familia de miembros con el mismo estado de precios
    // =================================================================================================

    private sealed record FamiliaSembrada(
        int Id, int DeBaja, int M1, int M2, DateTimeOffset DesdeDelPendiente, int ListaSinPrecios);

    /// <summary>La familia "Gaseosas": un miembro DADO DE BAJA con el id más bajo y valores y precios distintos
    /// (no es la referencia), y dos miembros vivos, idénticos entre sí. Lista general: precio vigente 100 y
    /// pendiente 130 dentro de tres días (el vigente se cierra donde empieza el pendiente). Lista mayorista:
    /// solo el vigente, 80. Una tercera lista fija, <see cref="FamiliaSembrada.ListaSinPrecios"/>, existe en el
    /// tenant y ningún miembro tiene precios en ella: el artículo que entra no recibe filas ahí.
    /// <paramref name="acumulaEnVenta"/> es el valor de <c>acumula_en_venta</c> de todos los miembros.</summary>
    private async Task<FamiliaSembrada> SembrarFamiliaConPreciosAsync(Entorno e, bool acumulaEnVenta = true)
    {
        var @base = ValoresBase(e) with { AcumulaEnVenta = acumulaEnVenta };
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var listaSinPrecios = await apoyo.SembrarListaFijaAsync(e, "Minorista");

        var deBaja = await apoyo.SembrarArticuloAsync(
            e, "de-baja", @base with { CostoLista = 999m }, familia, dadoDeBaja: true);
        await apoyo.SembrarPrecioVigenteAsync(e, deBaja, e.IdListaGeneral, 999m);

        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", @base, familia, idMarca: e.Marcas[0], descripcion: "primero");
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", @base, familia, idMarca: null, descripcion: "segundo");

        var desdeDelPendiente = DateTimeOffset.UtcNow.AddDays(3);
        foreach (var miembro in new[] { m1, m2 })
        {
            await apoyo.SembrarPrecioPendienteAsync(
                e, miembro, e.IdListaGeneral, montoVigente: 100m, montoPendiente: 130m, desdeDelPendiente);
            await apoyo.SembrarPrecioVigenteAsync(e, miembro, e.IdListaMayorista, 80m);
        }

        return new FamiliaSembrada(familia, deBaja, m1, m2, AMicrosegundos(desdeDelPendiente), listaSinPrecios);
    }

    // =================================================================================================
    // El alta feliz: valores, precios y auditoría
    // =================================================================================================

    [Fact]
    public async Task UnAltaDentroDeUnaFamiliaCopiaElEstadoDePreciosDelMiembroDeReferenciaConUnaAuditoriaPorPrecio()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaDentroDeUnaFamiliaCopiaElEstadoDePreciosDelMiembroDeReferenciaConUnaAuditoriaPorPrecio));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var huellasDeLosDemas = new[] { await apoyo.LeerAsync(f.M1), await apoyo.LeerAsync(f.M2), await apoyo.LeerAsync(f.DeBaja) }
            .Select(Huella).ToList();

        var respuesta = await PostArticuloAsync(
            e.Admin, Alta(ValoresBase(e), f.Id, nombre: "nuevo miembro", descripcion: "descripción propia", idMarca: e.Marcas[1], activo: false));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Equal(f.Id, creado.IdFamilia);
        Assert.Equal("nuevo miembro", creado.Nombre);

        var nuevo = await apoyo.LeerAsync(creado.Id);
        Assert.Equal(f.Id, nuevo.IdFamilia);
        Assert.Equal(ValoresBase(e), ValoresCompartidosDeFamilia.De(nuevo));
        Assert.Equal("descripción propia", nuevo.Descripcion);
        Assert.Equal(e.Marcas[1], nuevo.IdMarca);
        Assert.False(nuevo.Activo);

        // Lista general: el vigente arranca en el instante en que nace el artículo y se cierra donde empieza el
        // pendiente, que se hereda con su fecha y su monto.
        var general = await apoyo.FilasDePrecioAsync(nuevo.Id, e.IdListaGeneral);
        Assert.Equal(2, general.Count);
        Assert.Equal((100m, AMicrosegundos(nuevo.CreatedAt), (DateTimeOffset?)f.DesdeDelPendiente), (general[0].Monto, general[0].VigenteDesde, general[0].VigenteHasta));
        Assert.Equal((130m, f.DesdeDelPendiente, (DateTimeOffset?)null), (general[1].Monto, general[1].VigenteDesde, general[1].VigenteHasta));

        // Lista mayorista: solo el vigente, abierto.
        var mayorista = Assert.Single(await apoyo.FilasDePrecioAsync(nuevo.Id, e.IdListaMayorista));
        Assert.Equal((80m, AMicrosegundos(nuevo.CreatedAt), (DateTimeOffset?)null), (mayorista.Monto, mayorista.VigenteDesde, mayorista.VigenteHasta));

        // Una lista fija en la que la familia no tiene precios: el miembro nuevo no recibe ninguna fila ahí.
        Assert.Empty(await apoyo.FilasDePrecioAsync(nuevo.Id, f.ListaSinPrecios));

        // Una auditoría precio.cambio por cada fila insertada, sin estado anterior, y ninguna para los demás.
        var toda = await apoyo.AuditoriaDePreciosAsync(e.IdTenant);
        Assert.All(toda, a => Assert.Equal(nuevo.Id, a.IdEntidad));
        var auditoria = toda.Where(a => a.IdEntidad == nuevo.Id).ToList();
        Assert.Equal(3, auditoria.Count);
        Assert.All(auditoria, a =>
        {
            Assert.Equal("articulo", a.Entidad);
            Assert.Null(a.ValorAnterior);
        });

        var registrados = auditoria
            .Select(a => JsonDocument.Parse(a.ValorNuevo).RootElement)
            .Select(v => (
                Lista: v.GetProperty("id_lista_precio").GetInt32(),
                Monto: v.GetProperty("monto").GetDecimal(),
                Desde: v.GetProperty("vigente_desde").GetDateTimeOffset()))
            .OrderBy(v => v.Lista).ThenBy(v => v.Monto)
            .ToList();
        var esperados = new[]
        {
            (e.IdListaGeneral, 100m, AMicrosegundos(nuevo.CreatedAt)),
            (e.IdListaGeneral, 130m, f.DesdeDelPendiente),
            (e.IdListaMayorista, 80m, AMicrosegundos(nuevo.CreatedAt))
        }.OrderBy(v => v.Item1).ThenBy(v => v.Item2).ToList();
        Assert.Equal(esperados.Select(v => (v.Item1, v.Item2)), registrados.Select(v => (v.Lista, v.Monto)));
        Assert.Equal(esperados.Select(v => v.Item3), registrados.Select(v => AMicrosegundos(v.Desde)));

        // Los demás miembros no cambian: ni sus campos, ni su updated_at.
        var despues = new[] { await apoyo.LeerAsync(f.M1), await apoyo.LeerAsync(f.M2), await apoyo.LeerAsync(f.DeBaja) }
            .Select(Huella).ToList();
        Assert.Equal(huellasDeLosDemas, despues);
    }

    /// <summary>Sin <c>idFamilia</c> el alta no cambia: el artículo no pertenece a ninguna familia, no copia
    /// ningún precio ni registra ninguna auditoría de precios.</summary>
    [Fact]
    public async Task UnAltaSinIdFamiliaNoPerteneceANingunaFamiliaNiCopiaPrecios()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaSinIdFamiliaNoPerteneceANingunaFamiliaNiCopiaPrecios));
        await SembrarFamiliaConPreciosAsync(e);
        var preciosAntes = await ContarPreciosAsync(e.IdTenant);
        var auditoriaAntes = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), idFamilia: null));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Null(creado.IdFamilia);
        Assert.Null((await apoyo.LeerAsync(creado.Id)).IdFamilia);
        Assert.Equal(preciosAntes, await ContarPreciosAsync(e.IdTenant));
        Assert.Equal(auditoriaAntes, (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count);
    }

    /// <summary>La referencia es el miembro VIVO de menor id: el dado de baja, aunque tenga el id más bajo, no
    /// cuenta, y el de id más alto tampoco. Los tres tienen valores y precios distintos a propósito (la
    /// invariante de la familia está rota, solo para que la elección de la referencia sea observable): el alta
    /// con los valores de <c>m1</c> entra y copia el precio de <c>m1</c>.</summary>
    [Fact]
    public async Task LaReferenciaEsElMiembroVivoDeMenorId()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaReferenciaEsElMiembroVivoDeMenorId));
        var @base = ValoresBase(e);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con la invariante rota");
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", @base with { CostoLista = 999m }, familia, dadoDeBaja: true);
        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", @base, familia);
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", @base with { CostoLista = 777m }, familia);
        await apoyo.SembrarPrecioVigenteAsync(e, deBaja, e.IdListaGeneral, 999m);
        await apoyo.SembrarPrecioVigenteAsync(e, m1, e.IdListaGeneral, 111m);
        await apoyo.SembrarPrecioVigenteAsync(e, m2, e.IdListaGeneral, 777m);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(@base, familia));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        var fila = Assert.Single(await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaGeneral));
        Assert.Equal(111m, fila.Monto);
    }

    /// <summary>Una referencia sin precios en ninguna lista no transmite nada: el artículo nuevo entra sin filas
    /// de precios y sin auditoría de precios.</summary>
    [Fact]
    public async Task UnaReferenciaSinPreciosDejaAlMiembroNuevoSinPreciosNiAuditoria()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaSinPreciosDejaAlMiembroNuevoSinPreciosNiAuditoria));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Sin precios");
        await apoyo.SembrarArticuloAsync(e, "m1", ValoresBase(e), familia);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), familia));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Empty(await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaGeneral));
        Assert.Empty(await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaMayorista));
        Assert.DoesNotContain(await apoyo.AuditoriaDePreciosAsync(e.IdTenant), a => a.IdEntidad == creado.Id);
    }

    /// <summary>Con <c>acumula_en_venta</c> igual al de la referencia —acá un valor que no es el por defecto— el
    /// alta entra y el artículo nace con ese valor, en la respuesta y en la base.</summary>
    [Fact]
    public async Task UnAltaConElAcumulaEnVentaDeLaReferenciaEntraYNaceConEseValor()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConElAcumulaEnVentaDeLaReferenciaEntraYNaceConEseValor));
        var f = await SembrarFamiliaConPreciosAsync(e, acumulaEnVenta: false);
        var valores = ValoresBase(e) with { AcumulaEnVenta = false };

        var respuesta = await PostArticuloAsync(e.Admin, Alta(valores, f.Id));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.False(creado.AcumulaEnVenta);

        var nuevo = await apoyo.LeerAsync(creado.Id);
        Assert.Equal(f.Id, nuevo.IdFamilia);
        Assert.Equal(valores, ValoresCompartidosDeFamilia.De(nuevo));
    }

    /// <summary>El miembro nuevo es miembro de verdad: una escritura de precios con alcance <c>Familia</c> sobre
    /// otro miembro lo alcanza.</summary>
    [Fact]
    public async Task ElMiembroNuevoRecibeLasEscriturasSiguientesDeLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElMiembroNuevoRecibeLasEscriturasSiguientesDeLaFamilia));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var creado = (await (await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), f.Id))).Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        var cuerpo = new Dictionary<string, object?>
        {
            ["idListaPrecio"] = e.IdListaMayorista, ["precio"] = 95m, ["alcance"] = "Familia"
        };
        var escritura = await e.Admin.PostAsJsonAsync($"/api/articulos/{f.M1}/precios", cuerpo);

        Assert.Equal(HttpStatusCode.Created, escritura.StatusCode);
        foreach (var id in new[] { f.M1, f.M2, creado.Id })
        {
            var abierta = (await apoyo.FilasDePrecioAsync(id, e.IdListaMayorista)).Single(p => p.VigenteHasta is null);
            Assert.Equal(95m, abierta.Monto);
        }
    }

    // =================================================================================================
    // Rechazos: nada se escribe
    // =================================================================================================

    private async Task AfirmarQueNoSeEscribioNadaAsync(Entorno e, int articulosAntes, int preciosAntes, int auditoriaAntes)
    {
        Assert.Equal(articulosAntes, await ContarArticulosAsync(e.IdTenant));
        Assert.Equal(preciosAntes, await ContarPreciosAsync(e.IdTenant));
        Assert.Equal(auditoriaAntes, (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count);
    }

    /// <summary>Cada uno de los trece campos compartidos, distinto solo, rechaza el alta con 409
    /// <c>familia_valores_distintos</c> que nombra esa columna —y ninguna otra— y no escribe nada. Un caso por
    /// campo: cada uno tiene su mutante en el mapeo del pedido a los valores compartidos.</summary>
    [Theory]
    [MemberData(nameof(Columnas))]
    public async Task UnCampoCompartidoDistintoDeLaReferenciaDa409NombraLaColumnaYNoEscribeNada(string columna)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCampoCompartidoDistintoDeLaReferenciaDa409NombraLaColumnaYNoEscribeNada));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var articulos = await ContarArticulosAsync(e.IdTenant);
        var precios = await ContarPreciosAsync(e.IdTenant);
        var auditoria = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresConUnCampoCambiado(e, columna), f.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_valores_distintos", codigo);
        Assert.Contains("Gaseosas", mensaje, StringComparison.Ordinal);
        Assert.Contains(columna, mensaje, StringComparison.Ordinal);
        foreach (var otra in ColumnasCompartidas.Where(c => c != columna))
        {
            Assert.DoesNotContain(otra, mensaje, StringComparison.Ordinal);
        }

        await AfirmarQueNoSeEscribioNadaAsync(e, articulos, precios, auditoria);
    }

    /// <summary>El pedido que no trae <c>acumula_en_venta</c> lleva su valor por defecto, <c>true</c>, y se
    /// compara como cualquier otro campo compartido: contra una familia que no acumula en venta da 409
    /// <c>familia_valores_distintos</c> que nombra esa columna y ninguna otra, y no escribe nada.</summary>
    [Fact]
    public async Task UnAltaQueOmiteAcumulaEnVentaLlevaElValorPorDefectoYContraUnaFamiliaQueNoAcumulaDa409()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaQueOmiteAcumulaEnVentaLlevaElValorPorDefectoYContraUnaFamiliaQueNoAcumulaDa409));
        var f = await SembrarFamiliaConPreciosAsync(e, acumulaEnVenta: false);
        var articulos = await ContarArticulosAsync(e.IdTenant);
        var precios = await ContarPreciosAsync(e.IdTenant);
        var auditoria = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        var cuerpo = JsonSerializer.SerializeToNode(
            Alta(ValoresBase(e) with { AcumulaEnVenta = false }, f.Id), OpcionesJson)!.AsObject();
        Assert.True(cuerpo.Remove("acumulaEnVenta"));

        var respuesta = await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_valores_distintos", codigo);
        Assert.Contains("acumula_en_venta", mensaje, StringComparison.Ordinal);
        foreach (var otra in ColumnasCompartidas.Where(c => c != "acumula_en_venta"))
        {
            Assert.DoesNotContain(otra, mensaje, StringComparison.Ordinal);
        }

        await AfirmarQueNoSeEscribioNadaAsync(e, articulos, precios, auditoria);
    }

    [Fact]
    public async Task VariosCamposDistintosSeNombranTodos()
    {
        using var e = await apoyo.PrepararAsync(nameof(VariosCamposDistintosSeNombranTodos));
        var f = await SembrarFamiliaConPreciosAsync(e);

        var distintos = ValoresBase(e) with { IdGrupo = e.Grupos[1], CostoLista = 60m };
        var respuesta = await PostArticuloAsync(e.Admin, Alta(distintos, f.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_valores_distintos", codigo);
        Assert.Contains("id_grupo", mensaje, StringComparison.Ordinal);
        Assert.Contains("costo_lista", mensaje, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnaFamiliaInexistenteDa404YNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDa404YNoEscribeNada));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var articulos = await ContarArticulosAsync(e.IdTenant);
        var precios = await ContarPreciosAsync(e.IdTenant);
        var auditoria = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), f.Id + 1_000_000));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        await AfirmarQueNoSeEscribioNadaAsync(e, articulos, precios, auditoria);
    }

    [Fact]
    public async Task UnaFamiliaDadaDeBajaDa404YNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaDadaDeBajaDa404YNoEscribeNada));
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), dadaDeBaja);
        var articulos = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), dadaDeBaja));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(articulos, await ContarArticulosAsync(e.IdTenant));
    }

    /// <summary>La familia de OTRO tenant no existe para este: 404, y el artículo no entra a ella.</summary>
    [Fact]
    public async Task UnaFamiliaDeOtroTenantDa404()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaDeOtroTenantDa404));
        using var otro = await apoyo.PrepararAsync(nameof(UnaFamiliaDeOtroTenantDa404) + "-ajeno");
        var familiaAjena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var miembroAjeno = await apoyo.SembrarArticuloAsync(otro, "miembro-ajeno", ValoresBase(otro), familiaAjena);
        var articulos = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), familiaAjena));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(articulos, await ContarArticulosAsync(e.IdTenant));
        Assert.Equal(familiaAjena, (await apoyo.LeerAsync(miembroAjeno)).IdFamilia);
    }

    [Fact]
    public async Task UnaFamiliaInactivaDa409FamiliaInactivaYNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInactivaDa409FamiliaInactivaYNoEscribeNada));
        var inactiva = await apoyo.SembrarFamiliaAsync(e, "Inactiva", activa: false);
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), inactiva);
        var articulos = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), inactiva));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_inactiva", codigo);
        Assert.Contains("Inactiva", mensaje, StringComparison.Ordinal);
        Assert.Equal(articulos, await ContarArticulosAsync(e.IdTenant));
    }

    /// <summary>La familia inactiva se informa ANTES que sus valores distintos: la precedencia de la regla de
    /// ingreso, observada de punta a punta.</summary>
    [Fact]
    public async Task UnaFamiliaInactivaConValoresDistintosDa409FamiliaInactiva()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInactivaConValoresDistintosDa409FamiliaInactiva));
        var inactiva = await apoyo.SembrarFamiliaAsync(e, "Inactiva con otros valores", activa: false);
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), inactiva);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresConUnCampoCambiado(e, "costo_lista"), inactiva));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_inactiva", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>La familia inactiva se informa también ANTES que su falta de artículos: la otra mitad de la
    /// precedencia de la regla de ingreso (inactiva, sin artículos, valores distintos).</summary>
    [Fact]
    public async Task UnaFamiliaInactivaYSinMiembrosVivosDa409FamiliaInactiva()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInactivaYSinMiembrosVivosDa409FamiliaInactiva));
        var inactiva = await apoyo.SembrarFamiliaAsync(e, "Inactiva y vacía", activa: false);
        await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), inactiva, dadoDeBaja: true);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), inactiva));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_inactiva", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>Una familia cuyos miembros están todos dados de baja no tiene valores contra los que comparar ni
    /// precios que copiar: 409 <c>familia_sin_articulos</c>.</summary>
    [Fact]
    public async Task UnaFamiliaSinMiembrosVivosDa409FamiliaSinArticulosYNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaSinMiembrosVivosDa409FamiliaSinArticulosYNoEscribeNada));
        var vacia = await apoyo.SembrarFamiliaAsync(e, "Vacía");
        await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), vacia, dadoDeBaja: true);
        var articulos = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), vacia));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_sin_articulos", codigo);
        Assert.Contains("Vacía", mensaje, StringComparison.Ordinal);
        Assert.Equal(articulos, await ContarArticulosAsync(e.IdTenant));
    }

    /// <summary>Los 5 chequeos de catálogo corren ANTES que la validación de la familia: una referencia de
    /// catálogo inexistente es un 400 <c>referencia_invalida</c> aunque el pedido además difiera de la familia
    /// —que, evaluada primero, daría 409 <c>familia_valores_distintos</c>—. El pedido difiere de la familia en
    /// <c>costo_lista</c> y lleva un id inexistente en UNO de los cinco catálogos. Un caso por catálogo: cada
    /// chequeo es su propio mutante, porque la validación de la familia puesta entre dos de ellos solo se anticipa
    /// a los que le siguen.</summary>
    [Theory]
    [InlineData("el área")]
    [InlineData("la categoría")]
    [InlineData("la marca")]
    [InlineData("el grupo")]
    [InlineData("el proveedor")]
    public async Task UnaReferenciaDeCatalogoInexistenteDa400AunQueElPedidoDifieraDeLaFamilia(string catalogo)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaDeCatalogoInexistenteDa400AunQueElPedidoDifieraDeLaFamilia));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var articulos = await ContarArticulosAsync(e.IdTenant);
        var precios = await ContarPreciosAsync(e.IdTenant);
        var auditoria = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        const int inexistente = 987_654_321;
        var distintos = ValoresBase(e) with { CostoLista = 60m };
        var pedido = catalogo switch
        {
            "el área" => Alta(distintos with { IdArea = inexistente }, f.Id),
            "la categoría" => Alta(distintos with { IdCategoria = inexistente }, f.Id),
            "la marca" => Alta(distintos, f.Id, idMarca: inexistente),
            "el grupo" => Alta(distintos with { IdGrupo = inexistente }, f.Id),
            "el proveedor" => Alta(distintos with { IdProveedorHabitual = inexistente }, f.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(catalogo), catalogo, "Catálogo desconocido.")
        };

        var respuesta = await PostArticuloAsync(e.Admin, pedido);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("referencia_invalida", codigo);
        Assert.Contains($"No existe {catalogo} {inexistente}", mensaje, StringComparison.Ordinal);
        await AfirmarQueNoSeEscribioNadaAsync(e, articulos, precios, auditoria);
    }

    // =================================================================================================
    // Todo o nada
    // =================================================================================================

    private (WaysDbContext Db, ServicioDeArticulos Servicio) CrearServicio(
        Entorno e, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptores)
    {
        var db = fixture.CrearContextoDeAplicacionConReintentos(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), interceptores);
        var reloj = new RelojReal();
        var contexto = new ContextoFijo(e.IdTenant, e.IdActorAdmin);

        return (db, new ServicioDeArticulos(
            db, reloj, contexto, new ServicioDeLotes(db, reloj, contexto), new GuardaDeReferencias(db, new InspectorDeUso(db)),
            new ServicioDePrecios(db, reloj, contexto, new Ways.Application.Auditoria.ServicioDeAuditoria(db, reloj, contexto))));
    }

    /// <summary>Si el guardado de las filas de precio falla, se revierte el alta ENTERA —el artículo ya
    /// insertado dentro de la transacción también— y no se reintenta: el contexto es el de la API, con
    /// <c>EnableRetryOnFailure</c>, y el interceptor rompe solo el primer <c>INSERT INTO precios</c>. Con
    /// reintento el segundo intento daría de alta un segundo artículo.</summary>
    [Fact]
    public async Task UnFalloAlInsertarLosPreciosDelMiembroNuevoRevierteElAltaEnteraYNoSeReintenta()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnFalloAlInsertarLosPreciosDelMiembroNuevoRevierteElAltaEnteraYNoSeReintenta));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var articulos = await ContarArticulosAsync(e.IdTenant);
        var precios = await ContarPreciosAsync(e.IdTenant);
        var auditoria = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("precios", "40001");
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearAsync(Alta(ValoresBase(e), f.Id)));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        await AfirmarQueNoSeEscribioNadaAsync(e, articulos, precios, auditoria);
    }

    /// <summary>Un fallo al guardar revierte la transacción, pero las entidades que el alta agregó seguirían
    /// rastreadas por el contexto —el artículo ya insertado, las filas de precio y de auditoría encoladas— y el
    /// guardado de la escritura siguiente sobre el MISMO contexto las escribiría por detrás: filas de precio de un
    /// artículo que no existe. El interceptor rompe el primer <c>INSERT INTO precios</c> con un <c>40001</c>; el
    /// alta no reintenta, así que el error llega tal cual. Se sueltan solo las entidades que la operación agregó:
    /// una que el llamador ya tenía rastreada antes de llamar sigue rastreada y sin cambios, a diferencia de lo que
    /// haría <c>ChangeTracker.Clear()</c>.</summary>
    [Fact]
    public async Task UnFalloAlGuardarSueltaLasEntidadesAgregadasYLaSiguienteAltaDelMismoContextoEntraUnaSolaVez()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnFalloAlGuardarSueltaLasEntidadesAgregadasYLaSiguienteAltaDelMismoContextoEntraUnaSolaVez));
        var f = await SembrarFamiliaConPreciosAsync(e);
        var articulos = await ContarArticulosAsync(e.IdTenant);
        var precios = await ContarPreciosAsync(e.IdTenant);
        var auditoria = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count;

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("precios", "40001");
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var rastreadaDeAntes = await db.Familias.SingleAsync(familia => familia.Id == f.Id);
        Assert.Equal(EntityState.Unchanged, db.Entry(rastreadaDeAntes).State);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.CrearAsync(Alta(ValoresBase(e), f.Id, nombre: "falla")));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        Assert.DoesNotContain(
            db.ChangeTracker.Entries(),
            entrada => entrada.Entity is Articulo or Precio or Ways.Domain.Auditoria.Auditoria);
        Assert.Equal(EntityState.Unchanged, db.Entry(rastreadaDeAntes).State);
        await AfirmarQueNoSeEscribioNadaAsync(e, articulos, precios, auditoria);

        // El interceptor ya no rompe nada: la misma alta sobre el MISMO contexto entra una sola vez, con sus tres
        // filas de precio y sus tres auditorías, y no arrastra las de la que falló.
        var creado = await servicio.CrearAsync(Alta(ValoresBase(e), f.Id, nombre: "entra"));

        Assert.Equal(articulos + 1, await ContarArticulosAsync(e.IdTenant));
        Assert.Equal(precios + 3, await ContarPreciosAsync(e.IdTenant));
        Assert.Equal(2, (await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaGeneral)).Count);
        Assert.Single(await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaMayorista));

        var auditoriaNueva = (await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Skip(auditoria).ToList();
        Assert.Equal(3, auditoriaNueva.Count);
        Assert.All(auditoriaNueva, a => Assert.Equal(creado.Id, a.IdEntidad));
    }

    private static PostgresException ErrorDePostgres(Exception error)
    {
        for (Exception? actual = error; actual is not null; actual = actual.InnerException)
        {
            if (actual is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new InvalidOperationException($"La excepción no envuelve ninguna PostgresException: {error}");
    }

    // =================================================================================================
    // Locks y concurrencia
    // =================================================================================================

    /// <summary>Entrar a una familia cambia la pertenencia y por eso pide el lock EXCLUSIVO: con un compartido
    /// ajeno sostenido (stand-in de una escritura de precios de la familia en curso) queda esperando —observado
    /// pidiendo <c>ExclusiveLock</c>— ANTES de tomar ningún otro lock: ni uno advisory, ni un lock de fila de
    /// los chequeos de catálogo (que le habrían asignado una transacción). Cuando el otro lo libera, el alta
    /// termina.</summary>
    [Fact]
    public async Task UnAltaConFamiliaPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtroLock()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConFamiliaPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtroLock));
        var f = await SembrarFamiliaConPreciosAsync(e);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var alta = PostArticuloAsync(e.Admin, Alta(ValoresBase(e), f.Id));

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "El alta dentro de una familia nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(alta.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.Created, (await alta.WaitAsync(EsperaMaxima)).StatusCode);
    }

    /// <summary>Un alta SIN familia no toma el lock de membresía: con el exclusivo sostenido por otra conexión
    /// hasta el final de la prueba, termina con 201 antes de 15 segundos. Si lo pidiera, en el modo que fuera,
    /// quedaría esperando a un lock que nadie libera y la prueba fallaría por tiempo. No observa nada en
    /// <c>pg_locks</c>: lo que prueba es que el alta terminó con el lock todavía sostenido.</summary>
    [Fact]
    public async Task UnAltaSinFamiliaNoEsperaAlLockDeMembresiaAjeno()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaSinFamiliaNoEsperaAlLockDeMembresiaAjeno));

        await using var sostenedor = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var transaccion = await sostenedor.BeginTransactionAsync();

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var respuesta = await PostArticuloAsync(e.Admin, Alta(ValoresBase(e), idFamilia: null)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    /// <summary>La razón del lock exclusivo: una escritura de precios de la familia está a mitad de camino
    /// —sostiene el lock compartido y cambió el precio del miembro de referencia sin comitear—. El alta espera;
    /// cuando la escritura comitea, el miembro nuevo copia el precio NUEVO y queda alineado con la familia.
    /// Con el lock compartido el alta no esperaría, copiaría el precio viejo ya comiteado, y la familia
    /// quedaría con un miembro de precio distinto.</summary>
    [Fact]
    public async Task UnAltaEnUnaFamiliaConUnaEscrituraDePreciosEnCursoCopiaElPrecioQueEsaEscrituraDeja()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaEnUnaFamiliaConUnaEscrituraDePreciosEnCursoCopiaElPrecioQueEsaEscrituraDeja));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con una escritura en curso");
        var referencia = await apoyo.SembrarArticuloAsync(e, "referencia", ValoresBase(e), familia);
        await apoyo.SembrarPrecioVigenteAsync(e, referencia, e.IdListaGeneral, 100m);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EjecutarAsync(
            sostenedor, transaccion,
            "UPDATE precios SET vigente_hasta = now() WHERE id_articulo = $1 AND id_lista_precio = $2 AND vigente_hasta IS NULL",
            referencia, e.IdListaGeneral);
        await EjecutarAsync(
            sostenedor, transaccion,
            "INSERT INTO precios (id_tenant, id_articulo, id_lista_precio, precio, vigente_desde, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 150, now(), now(), now())",
            e.IdTenant, referencia, e.IdListaGeneral);

        var alta = PostArticuloAsync(e.Admin, Alta(ValoresBase(e), familia));

        await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "El alta nunca se observó esperando el lock de membresía: la prueba no está probando la carrera.");

        await transaccion.CommitAsync();

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        var abierta = (await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaGeneral)).Single(p => p.VigenteHasta is null);
        Assert.Equal(150m, abierta.Monto);
        Assert.Equal(150m, (await apoyo.FilasDePrecioAsync(referencia, e.IdListaGeneral)).Single(p => p.VigenteHasta is null).Monto);
    }

    /// <summary>La referencia y sus campos compartidos se leen DESPUÉS del lock de membresía. Una escritura de
    /// la familia en curso (sostiene el lock compartido) cambió <c>costo_lista</c> de todos los miembros sin
    /// comitear; el pedido lleva el valor NUEVO. El alta espera, y al comitear la otra escritura lee la
    /// referencia ya con ese valor: los trece campos coinciden y entra. Si la referencia se leyera antes de
    /// esperar, vería el valor viejo y el pedido daría <c>familia_valores_distintos</c>.</summary>
    [Fact]
    public async Task LaReferenciaSeLeeDespuesDelLockDeMembresiaYNoConLaFotoPrevia()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaReferenciaSeLeeDespuesDelLockDeMembresiaYNoConLaFotoPrevia));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con un cambio compartido en curso");
        await apoyo.SembrarArticuloAsync(e, "m1", ValoresBase(e), familia);
        await apoyo.SembrarArticuloAsync(e, "m2", ValoresBase(e), familia);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EjecutarAsync(
            sostenedor, transaccion, "UPDATE articulos SET costo_lista = $1 WHERE id_familia = $2", 60m, familia);

        var alta = PostArticuloAsync(e.Admin, Alta(ValoresConUnCampoCambiado(e, "costo_lista"), familia));

        await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "El alta nunca se observó esperando el lock de membresía.");

        await transaccion.CommitAsync();

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Equal(60m, (await apoyo.LeerAsync(creado.Id)).CostoLista);
    }

    /// <summary>La familia se lee viva y activa BLOQUEADA <c>FOR SHARE</c>: un cambio de <c>activo</c> de la
    /// familia en curso —que no toma el lock de membresía— la hace esperar la fila de la familia, y al comitear
    /// el alta ve la familia ya inactiva: 409 <c>familia_inactiva</c>. Con una lectura sin lock vería la
    /// familia activa y el artículo entraría a una familia que se estaba desactivando.</summary>
    [Fact]
    public async Task UnCambioDeActivoDeLaFamiliaEnCursoHaceEsperarAlAltaYElAltaLaVeInactiva()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCambioDeActivoDeLaFamiliaEnCursoHaceEsperarAlAltaYElAltaLaVeInactiva));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Se está desactivando");
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);
        var articulos = await ContarArticulosAsync(e.IdTenant);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE familias SET activo = false WHERE id_familia = $1", familia);

        var alta = PostArticuloAsync(e.Admin, Alta(ValoresBase(e), familia));

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El alta nunca se observó esperando la fila de la familia.");

        await transaccion.CommitAsync();

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_inactiva", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(articulos, await ContarArticulosAsync(e.IdTenant));
    }

    /// <summary>Lo mismo con una baja lógica de la familia en curso: el alta espera su fila y, al comitear la
    /// baja, no la encuentra: 404 y el artículo no entra.</summary>
    [Fact]
    public async Task UnaBajaDeLaFamiliaEnCursoHaceEsperarAlAltaYElAltaNoLaEncuentra()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaBajaDeLaFamiliaEnCursoHaceEsperarAlAltaYElAltaNoLaEncuentra));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Se está dando de baja");
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);
        var articulos = await ContarArticulosAsync(e.IdTenant);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE familias SET deleted_at = now() WHERE id_familia = $1", familia);

        var alta = PostArticuloAsync(e.Admin, Alta(ValoresBase(e), familia));

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El alta nunca se observó esperando la fila de la familia.");

        await transaccion.CommitAsync();

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal(articulos, await ContarArticulosAsync(e.IdTenant));
    }

    /// <summary>Dos altas lanzadas a la vez en la misma familia terminan las dos con 201, ninguna se cuelga, las
    /// dos son miembros y cada una copió el estado de precios completo de la familia. No distingue si se
    /// serializaron o corrieron una después de la otra: ningún punto de encuentro fuerza que se solapen. Que el alta
    /// pide el lock de membresía en modo exclusivo —lo que hace que dos altas no convivan— lo prueba
    /// <see cref="UnAltaConFamiliaPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtroLock"/>.</summary>
    [Fact]
    public async Task DosAltasSimultaneasEnLaMismaFamiliaEntranLasDosConElEstadoDePreciosCompleto()
    {
        using var e = await apoyo.PrepararAsync(nameof(DosAltasSimultaneasEnLaMismaFamiliaEntranLasDosConElEstadoDePreciosCompleto));
        var f = await SembrarFamiliaConPreciosAsync(e);

        using var segundo = fixture.CreateClient();
        var login = await segundo.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(e.MailAdmin, e.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var primera = PostArticuloAsync(e.Admin, Alta(ValoresBase(e), f.Id, nombre: "uno"));
        var segunda = PostArticuloAsync(segundo, Alta(ValoresBase(e), f.Id, nombre: "dos"));

        var respuestas = await Task.WhenAll(primera, segunda).WaitAsync(EsperaMaxima);

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var auditoria = await apoyo.AuditoriaDePreciosAsync(e.IdTenant);

        foreach (var respuesta in respuestas)
        {
            var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
            Assert.Equal(f.Id, creado.IdFamilia);
            Assert.Equal([100m, 130m], (await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaGeneral)).Select(p => p.Monto));
            Assert.Equal([80m], (await apoyo.FilasDePrecioAsync(creado.Id, e.IdListaMayorista)).Select(p => p.Monto));
            Assert.Empty(await apoyo.FilasDePrecioAsync(creado.Id, f.ListaSinPrecios));
            Assert.Equal(3, auditoria.Count(a => a.IdEntidad == creado.Id));
        }
    }
}
