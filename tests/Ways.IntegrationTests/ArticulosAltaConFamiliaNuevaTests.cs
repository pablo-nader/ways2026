using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Articulos;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeAgrupacion;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/articulos</c> con <c>nombreFamiliaNueva</c> (doc 10 §3, "Alta con una familia nueva") contra Postgres
/// real: el artículo y su familia nacen en UNA transacción y el artículo es su único miembro. Es excluyente con
/// <c>idFamilia</c>, un nombre presente pero vacío se rechaza en vez de descartarse, y un fallo en cualquier punto
/// —el nombre en uso, un catálogo, un guardado posterior— no deja ni el artículo ni la familia.
///
/// <para>La carrera por el nombre usa un rendezvous determinístico: una conexión cruda sostiene el lock de membresía, los
/// dos pedidos quedan observados esperándolo en <c>pg_locks</c> habiendo pasado el chequeo previo, y recién ahí se libera
/// (<c>mutation-proof-tests</c>, regla 13).</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosAltaConFamiliaNuevaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const int LargoMaximoDelNombre = 150;

    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static AltaArticulo Alta(
        Entorno e, string nombre, string? nombreFamiliaNueva, int? idFamilia = null, string? codigoProveedor = null)
    {
        var v = ValoresBase(e);

        return new AltaArticulo(
            CodigoInterno: null, Nombre: nombre, Descripcion: null, IdArea: v.IdArea, IdCategoria: v.IdCategoria,
            IdMarca: null, IdGrupo: v.IdGrupo, IdProveedorHabitual: v.IdProveedorHabitual, IdAlicuotaIva: v.IdAlicuotaIva,
            UnidadVenta: v.UnidadVenta, UnidadesPorBulto: v.UnidadesPorBulto, EsProducto: v.EsProducto,
            CostoLista: v.CostoLista, CostoListaPor: v.CostoListaPor, CostoListaIncluyeIva: v.CostoListaIncluyeIva,
            DescuentoProveedor: v.DescuentoProveedor, ImpuestoInterno: v.ImpuestoInterno,
            ControlaLote: v.ControlaLote, CodigoProveedor: codigoProveedor, AcumulaEnVenta: v.AcumulaEnVenta,
            IdFamilia: idFamilia, NombreFamiliaNueva: nombreFamiliaNueva);
    }

    private static Task<HttpResponseMessage> PostArticuloAsync(HttpClient cliente, AltaArticulo alta) =>
        cliente.PostAsJsonAsync("/api/articulos", alta, OpcionesJson);

    private async Task<int> ContarArticulosAsync(int idTenant, string? nombre = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters()
            .CountAsync(a => a.IdTenant == idTenant && (nombre == null || a.Nombre == nombre));
    }

    private async Task<List<Familia>> FamiliasAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Familias.IgnoreQueryFilters().AsNoTracking().Where(f => f.IdTenant == idTenant).ToListAsync();
    }

    private async Task<int> ContarPreciosAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Precios.IgnoreQueryFilters().CountAsync(p => p.IdTenant == idTenant);
    }

    private async Task<List<int>> MiembrosAsync(int idFamilia)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters()
            .Where(a => a.IdFamilia == idFamilia)
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .ToListAsync();
    }

    /// <summary>Nada persistido por el pedido: ni el artículo de ese nombre ni ninguna familia más.</summary>
    private async Task AssertNadaPersistidoAsync(Entorno e, string nombreDelArticulo, int familiasAntes, int articulosAntes)
    {
        Assert.Equal(0, await ContarArticulosAsync(e.IdTenant, nombreDelArticulo));
        Assert.Equal(articulosAntes, await ContarArticulosAsync(e.IdTenant));
        Assert.Equal(familiasAntes, (await FamiliasAsync(e.IdTenant)).Count);
    }

    // =================================================================================================
    // El camino feliz
    // =================================================================================================

    /// <summary>El pedido crea el artículo y la familia (activa, con el nombre sin espacios en los extremos) en la misma
    /// transacción: el artículo es su único miembro, la respuesta trae su <c>idFamilia</c>, sus dieciséis campos compartidos
    /// y sus campos propios son los del pedido, no se escribe ningún precio y la familia y el artículo comparten el
    /// instante de creación.</summary>
    [Fact]
    public async Task UnAltaConNombreDeFamiliaNuevaCreaLaFamiliaYElArticuloEsSuUnicoMiembro()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConNombreDeFamiliaNuevaCreaLaFamiliaYElArticuloEsSuUnicoMiembro));
        var preciosAntes = await ContarPreciosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "primero de la familia", "  Bebidas nuevas  "));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.NotNull(creado.IdFamilia);

        var familia = Assert.Single(await FamiliasAsync(e.IdTenant));
        Assert.Equal(creado.IdFamilia, familia.Id);
        Assert.Equal("Bebidas nuevas", familia.Nombre);
        Assert.True(familia.Activo);
        Assert.Null(familia.DeletedAt);

        Assert.Equal([creado.Id], await MiembrosAsync(familia.Id));

        var articulo = await apoyo.LeerAsync(creado.Id);
        Assert.Equal(familia.Id, articulo.IdFamilia);
        Assert.Equal("primero de la familia", articulo.Nombre);
        Assert.Equal(ValoresBase(e), ValoresCompartidosDeFamilia.De(articulo));
        Assert.Equal(articulo.CreatedAt, familia.CreatedAt);
        Assert.Equal(articulo.CreatedAt, familia.UpdatedAt);

        Assert.Equal(preciosAntes, await ContarPreciosAsync(e.IdTenant));
    }

    /// <summary>El artículo creado con su familia ya es una referencia usable: otro alta con <c>idFamilia</c> de la
    /// familia nueva y los mismos campos compartidos entra, y la familia queda con los dos miembros.</summary>
    [Fact]
    public async Task LaFamiliaCreadaPorElAltaAdmiteDespuesUnSegundoMiembroPorIdFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaFamiliaCreadaPorElAltaAdmiteDespuesUnSegundoMiembroPorIdFamilia));

        var primero = (await (await PostArticuloAsync(e.Admin, Alta(e, "uno", "Pares"))).Content
            .ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        var segundo = await PostArticuloAsync(e.Admin, Alta(e, "dos", nombreFamiliaNueva: null, idFamilia: primero.IdFamilia));

        Assert.Equal(HttpStatusCode.Created, segundo.StatusCode);
        var creado = (await segundo.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Equal([primero.Id, creado.Id], await MiembrosAsync(primero.IdFamilia!.Value));
    }

    // =================================================================================================
    // Sin el campo, el alta no cambia
    // =================================================================================================

    /// <summary>Un cuerpo sin <c>nombreFamiliaNueva</c>, o con el campo en <c>null</c>, es el alta de siempre: el
    /// artículo no pertenece a ninguna familia y no se crea ninguna.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnAltaSinElCampoOConElCampoNuloNoCreaNingunaFamilia(bool conElCampoNulo)
    {
        using var e = await apoyo.PrepararAsync($"{nameof(UnAltaSinElCampoOConElCampoNuloNoCreaNingunaFamilia)}{conElCampoNulo}");
        var cuerpo = JsonSerializer.SerializeToNode(Alta(e, "suelto", nombreFamiliaNueva: null), OpcionesJson)!.AsObject();
        Assert.True(cuerpo.ContainsKey("nombreFamiliaNueva"));
        if (!conElCampoNulo)
        {
            cuerpo.Remove("nombreFamiliaNueva");
        }

        var respuesta = await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Null(creado.IdFamilia);
        Assert.Null((await apoyo.LeerAsync(creado.Id)).IdFamilia);
        Assert.Empty(await FamiliasAsync(e.IdTenant));
    }

    // =================================================================================================
    // Rechazos del pedido
    // =================================================================================================

    /// <summary>Con <c>idFamilia</c> y <c>nombreFamiliaNueva</c> a la vez el pedido es ambiguo: 400
    /// <c>familia_existente_y_nueva</c>, aunque la familia exista y aunque el nombre esté libre, y no se escribe nada.
    /// No se elige una en silencio.</summary>
    [Fact]
    public async Task IdFamiliaYNombreFamiliaNuevaALaVezDan400YNoEscribenNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(IdFamiliaYNombreFamiliaNuevaALaVezDan400YNoEscribenNada));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Existente");
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);
        var familiasAntes = (await FamiliasAsync(e.IdTenant)).Count;
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "ambiguo", "Otra familia", idFamilia: familia));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("familia_existente_y_nueva", (await ProblemaAsync(respuesta)).Codigo);
        await AssertNadaPersistidoAsync(e, "ambiguo", familiasAntes, articulosAntes);
    }

    /// <summary>Un nombre presente pero vacío o de solo espacios es <c>400 nombre_requerido</c>, y uno de más de 150
    /// caracteres (después de recortar), <c>400 nombre_muy_largo</c>: el campo aceptado nunca se descarta en silencio.
    /// Uno de exactamente 150 entra.</summary>
    [Theory]
    [InlineData("", "nombre_requerido")]
    [InlineData("   ", "nombre_requerido")]
    [InlineData("LARGO", "nombre_muy_largo")]
    public async Task UnNombrePresenteVacioOMuyLargoSeRechazaYNoEscribeNada(string nombreFamilia, string codigoEsperado)
    {
        using var e = await apoyo.PrepararAsync($"{nameof(UnNombrePresenteVacioOMuyLargoSeRechazaYNoEscribeNada)}{codigoEsperado}{nombreFamilia.Length}");
        var enviado = nombreFamilia == "LARGO" ? new string('x', LargoMaximoDelNombre + 1) : nombreFamilia;
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "rechazado", enviado));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(codigoEsperado, (await ProblemaAsync(respuesta)).Codigo);
        await AssertNadaPersistidoAsync(e, "rechazado", 0, articulosAntes);
    }

    [Fact]
    public async Task UnNombreDeExactamenteElLargoMaximoEntra()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnNombreDeExactamenteElLargoMaximoEntra));
        var nombre = new string('x', LargoMaximoDelNombre);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "justo", $" {nombre} "));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(nombre, Assert.Single(await FamiliasAsync(e.IdTenant)).Nombre);
    }

    /// <summary>Un nombre que ya tiene una familia viva —sin distinguir mayúsculas ni espacios en los extremos— da 409
    /// <c>familia_nombre_duplicado</c> y no deja ni el artículo ni una segunda familia. El de una familia dada de baja se
    /// puede reutilizar.</summary>
    [Fact]
    public async Task UnNombreEnUsoDa409SinPersistirNadaYElDeUnaFamiliaDadaDeBajaSePuedeReutilizar()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnNombreEnUsoDa409SinPersistirNadaYElDeUnaFamiliaDadaDeBajaSePuedeReutilizar));
        await apoyo.SembrarFamiliaAsync(e, "Existente");
        await apoyo.SembrarFamiliaAsync(e, "Vieja", dadaDeBaja: true);
        var familiasAntes = (await FamiliasAsync(e.IdTenant)).Count;
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "no debe existir", "  eXISTENTE "));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(
            ("familia_nombre_duplicado", "Ya existe una familia llamada \"eXISTENTE\" en este tenant."), await ProblemaAsync(respuesta));
        await AssertNadaPersistidoAsync(e, "no debe existir", familiasAntes, articulosAntes);

        Assert.Equal(HttpStatusCode.Created, (await PostArticuloAsync(e.Admin, Alta(e, "reusa el nombre", "Vieja"))).StatusCode);
    }

    // =================================================================================================
    // Atomicidad
    // =================================================================================================

    /// <summary>Un catálogo del pedido que no existe se rechaza bajo el lock, antes de insertar: 400
    /// <c>referencia_invalida</c> y ninguna familia.</summary>
    [Fact]
    public async Task UnCatalogoInexistenteRechazaElAltaYNoDejaLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCatalogoInexistenteRechazaElAltaYNoDejaLaFamilia));
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "con area inexistente", "Familia fantasma") with { IdArea = 999_999_999 });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("referencia_invalida", (await ProblemaAsync(respuesta)).Codigo);
        await AssertNadaPersistidoAsync(e, "con area inexistente", 0, articulosAntes);
    }

    /// <summary>El 409 <c>codigo_proveedor_duplicado</c> ocurre DESPUÉS de insertar la familia y el artículo: la
    /// transacción se revierte entera y no queda ninguno de los dos, ni rastreado en el contexto (un guardado posterior
    /// del mismo request no los reescribe).</summary>
    [Fact]
    public async Task UnFalloPosteriorAlInsertDelArticuloRevierteTambienLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnFalloPosteriorAlInsertDelArticuloRevierteTambienLaFamilia));
        Assert.Equal(
            HttpStatusCode.Created, (await PostArticuloAsync(e.Admin, Alta(e, "dueño del codigo", null, codigoProveedor: "COD-1"))).StatusCode);
        var articulosAntes = await ContarArticulosAsync(e.IdTenant);

        var respuesta = await PostArticuloAsync(e.Admin, Alta(e, "repite el codigo", "Familia que se revierte", codigoProveedor: "COD-1"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("codigo_proveedor_duplicado", (await ProblemaAsync(respuesta)).Codigo);
        await AssertNadaPersistidoAsync(e, "repite el codigo", 0, articulosAntes);
    }

    // =================================================================================================
    // Locks y carrera
    // =================================================================================================

    /// <summary>El alta con familia nueva pide el lock de membresía EXCLUSIVO como primera sentencia: con uno compartido
    /// ajeno sostenido queda esperando, sin haber tomado ningún otro lock ni haber escrito nada.</summary>
    [Fact]
    public async Task UnAltaConFamiliaNuevaPideElLockExclusivoAntesDeTomarNingunOtroLock()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaConFamiliaNuevaPideElLockExclusivoAntesDeTomarNingunOtroLock));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;
        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var alta = PostArticuloAsync(e.Admin, Alta(e, "espera el lock", "Familia que espera"));

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "El alta con familia nueva nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(alta.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.Created, (await alta.WaitAsync(EsperaMaxima)).StatusCode);
    }

    /// <summary>La carrera por el nombre llega al respaldo. Dos altas al MISMO nombre de familia pasan el chequeo previo
    /// —nadie lo tiene todavía— y quedan esperando el lock de membresía que otra conexión sostiene; con los dos esperando
    /// observados se libera, y se serializan: el primero crea la familia y el segundo recibe, al insertarla, el 23505 de
    /// <c>ux_familias_nombre</c> —observado en el comando, con su SQLSTATE—, que la API traduce a 409 con el mensaje del
    /// respaldo. El perdedor no deja su artículo ni una segunda familia.</summary>
    [Fact]
    public async Task DosAltasAlMismoNombreDeFamiliaDejanUnaFamiliaYUnPerdedorConElRespaldoDe23505()
    {
        using var e = await apoyo.PrepararAsync(nameof(DosAltasAlMismoNombreDeFamiliaDejanUnaFamiliaYUnPerdedorConElRespaldoDe23505));
        const string nombre = "Nombre en carrera";

        var registro = new RegistroDeFallosDeComandos();
        var (host, cliente) = await ClienteDeUnHostConAsync(fixture, e, registro);
        await using var _ = host;
        using var __ = cliente;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var a = PostArticuloAsync(cliente, Alta(e, "articulo-a", nombre));
        var b = PostArticuloAsync(cliente, Alta(e, "articulo-b", nombre));

        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EsperarAsync(
            async () =>
            {
                var esperando = await CandadosAdvisoryAsync(
                    poll, "NOT granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo);

                return esperando.Count == 2 ? esperando : null;
            },
            "Los dos pedidos nunca se observaron esperando el lock de membresía.");

        await transaccion.CommitAsync();

        var respuestas = await Task.WhenAll(a, b).WaitAsync(EsperaMaxima);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], respuestas.Select(r => r.StatusCode).Order());

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(("familia_nombre_duplicado", "Ya existe una familia con ese nombre."), await ProblemaAsync(perdedora));

        var violacion = Assert.Single(registro.Fallos);
        Assert.Equal(("23505", "ux_familias_nombre"), (violacion.SqlState, violacion.ConstraintName));

        var ganadora = (await respuestas.Single(r => r.StatusCode == HttpStatusCode.Created).Content
            .ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        var perdedor = ganadora.Nombre == "articulo-a" ? "articulo-b" : "articulo-a";

        var familia = Assert.Single(await FamiliasAsync(e.IdTenant));
        Assert.Equal(ganadora.IdFamilia, familia.Id);
        Assert.Equal([ganadora.Id], await MiembrosAsync(familia.Id));
        Assert.Equal(0, await ContarArticulosAsync(e.IdTenant, perdedor));
        Assert.Equal(1, await ContarArticulosAsync(e.IdTenant));
    }
}
