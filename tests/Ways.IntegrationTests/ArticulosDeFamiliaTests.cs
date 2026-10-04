using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Bajas;
using Ways.Application.Familias;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>PUT /api/articulos/{id}</c> con alcance de familia (doc 10 §3, "Familias de artículos") contra
/// Postgres real: qué escribe la edición de un artículo que pertenece a una familia, qué rechaza, y el
/// protocolo de locks que lo sostiene — membresía (compartida o exclusiva), filas de <c>articulos</c> de
/// los miembros y catálogos. Las familias y sus miembros se siembran directo por EF, con los trece campos
/// compartidos escritos uno por uno.
///
/// <para>Los tests de concurrencia son rendezvous determinísticos: una conexión cruda sostiene un lock, la
/// edición queda observada esperando en <c>pg_locks</c> y recién ahí se libera. Los statements de lock son
/// SQL crudo que un interceptor de EF no ve, así que el orden se afirma mirando <c>pg_locks</c> desde
/// afuera (<c>mutation-proof-tests</c>, regla 13).</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosDeFamiliaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    // =================================================================================================
    // Siembra común: una familia con tres miembros vivos de campos propios distintos, un miembro dado de
    // baja, un artículo suelto y otra familia con un miembro
    // =================================================================================================

    private sealed record Familiares(int Familia, int A1, int A2, int A3, int DeBaja, int Suelto, int OtraFamilia, int DeOtraFamilia);

    private async Task<Familiares> SembrarFamiliaConVecinosAsync(Entorno e, bool acumulaEnVenta = true)
    {
        var @base = ValoresBase(e) with { AcumulaEnVenta = acumulaEnVenta };

        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var a1 = await apoyo.SembrarArticuloAsync(
            e, "a1", @base, familia, idMarca: e.Marcas[0], descripcion: "primero");
        var a2 = await apoyo.SembrarArticuloAsync(
            e, "a2", @base, familia, idMarca: e.Marcas[0], descripcion: "segundo");
        var a3 = await apoyo.SembrarArticuloAsync(
            e, "a3", @base, familia, idMarca: null, descripcion: null, activo: false);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", @base, familia, dadoDeBaja: true);
        var suelto = await apoyo.SembrarArticuloAsync(e, "suelto", @base);
        var otraFamilia = await apoyo.SembrarFamiliaAsync(e, "Otra familia");
        var deOtraFamilia = await apoyo.SembrarArticuloAsync(e, "de-otra", @base, otraFamilia);

        return new Familiares(familia, a1, a2, a3, deBaja, suelto, otraFamilia, deOtraFamilia);
    }

    /// <summary>Todo lo que una edición puede tocar de un artículo, para afirmar que NO lo tocó: los campos
    /// propios, la pertenencia, los trece compartidos, los sellos de auditoría y la baja.</summary>
    private static object Huella(Articulo a) => new
    {
        a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.DisponibleParaTodas, a.IdFamilia,
        Compartidos = ValoresCompartidosDeFamilia.De(a), a.UpdatedAt, a.DeletedAt
    };

    private async Task<List<object>> HuellasAsync(params int[] ids)
    {
        var huellas = new List<object>();
        foreach (var id in ids)
        {
            huellas.Add(Huella(await apoyo.LeerAsync(id)));
        }

        return huellas;
    }

    private async Task AfirmarSinCambiosAsync(IReadOnlyList<int> ids, IReadOnlyList<object> huellasAntes)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            Assert.Equal(huellasAntes[i], Huella(await apoyo.LeerAsync(ids[i])));
        }
    }

    // =================================================================================================
    // Artículo sin familia
    // =================================================================================================

    [Fact]
    public async Task UnArticuloSinFamiliaSeEditaSoloYSinAlcance()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnArticuloSinFamiliaSeEditaSoloYSinAlcance));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var otroSuelto = await apoyo.SembrarArticuloAsync(e, "otro-suelto", ValoresBase(e));
        var antes = await apoyo.LeerAsync(f.Suelto);
        var huellasDeLosDemas = await HuellasAsync(f.A1, f.A2, f.A3, f.DeBaja, otroSuelto, f.DeOtraFamilia);

        var nuevos = ValoresConUnCampoCambiado(e, "costo_lista");
        var pedido = ConCompartidos(
            EdicionIgualA(antes) with { Nombre = "suelto editado", Descripcion = "con descripción", IdMarca = e.Marcas[1], Activo = false },
            nuevos);

        var respuesta = await PutArticuloAsync(e.Admin, f.Suelto, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var devuelto = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Equal(f.Suelto, devuelto.Id);
        Assert.Null(devuelto.IdFamilia);

        var despues = await apoyo.LeerAsync(f.Suelto);
        Assert.Equal("suelto editado", despues.Nombre);
        Assert.Equal("con descripción", despues.Descripcion);
        Assert.Equal(e.Marcas[1], despues.IdMarca);
        Assert.False(despues.Activo);
        Assert.Equal(nuevos, ValoresCompartidosDeFamilia.De(despues));
        Assert.Null(despues.IdFamilia);

        await AfirmarSinCambiosAsync([f.A1, f.A2, f.A3, f.DeBaja, otroSuelto, f.DeOtraFamilia], huellasDeLosDemas);
    }

    /// <summary>Sobre un artículo que no es miembro un alcance explícito no tiene a qué referirse: el cliente
    /// miraba una pantalla vieja. 409 y nada escrito, tampoco los campos propios del pedido.</summary>
    [Theory]
    [InlineData(AlcanceDeFamilia.Familia)]
    [InlineData(AlcanceDeFamilia.SoloEste)]
    public async Task UnAlcanceExplicitoSobreUnArticuloSinFamiliaDa409FamiliaCambioYNoEscribeNada(AlcanceDeFamilia alcance)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAlcanceExplicitoSobreUnArticuloSinFamiliaDa409FamiliaCambioYNoEscribeNada));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.Suelto);
        var huellaAntes = Huella(antes);

        var pedido = ConCompartidos(
            EdicionIgualA(antes, alcance) with { Nombre = "no se escribe" }, ValoresConUnCampoCambiado(e, "costo_lista"));

        var respuesta = await PutArticuloAsync(e.Admin, f.Suelto, pedido);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(huellaAntes, Huella(await apoyo.LeerAsync(f.Suelto)));
    }

    // =================================================================================================
    // Miembro que no cambia ningún campo compartido: no hace falta decidir
    // =================================================================================================

    /// <summary>Sin cambio compartido no hay nada que replicar y no se exige alcance: se escriben los campos
    /// propios del artículo editado, incluida su disponibilidad por empresa, y nada más. Ningún otro miembro,
    /// ni el dado de baja, ni los de otra familia, ni un artículo suelto cambian —ni siquiera su
    /// <c>updated_at</c>—, y el artículo sigue siendo miembro.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(AlcanceDeFamilia.Familia)]
    public async Task UnMiembroSinCambioCompartidoEscribeSoloSusCamposPropiosYSigueEnLaFamilia(AlcanceDeFamilia? alcance)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnMiembroSinCambioCompartidoEscribeSoloSusCamposPropiosYSigueEnLaFamilia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasDeLosDemas = await HuellasAsync(f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia);

        var pedido = EdicionIgualA(antes, alcance) with
        {
            Nombre = "a2 editado", Descripcion = "segundo editado", IdMarca = e.Marcas[1], Activo = false,
            DisponibleParaTodas = false, IdsEmpresas = [e.IdEmpresa]
        };

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var devuelto = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Equal(f.Familia, devuelto.IdFamilia);
        Assert.Equal([e.IdEmpresa], devuelto.IdsEmpresas);

        var despues = await apoyo.LeerAsync(f.A2);
        Assert.Equal("a2 editado", despues.Nombre);
        Assert.Equal("segundo editado", despues.Descripcion);
        Assert.Equal(e.Marcas[1], despues.IdMarca);
        Assert.False(despues.Activo);
        Assert.False(despues.DisponibleParaTodas);
        Assert.Equal(f.Familia, despues.IdFamilia);
        Assert.Equal(ValoresCompartidosDeFamilia.De(antes), ValoresCompartidosDeFamilia.De(despues));
        Assert.True(despues.UpdatedAt > antes.UpdatedAt);

        await AfirmarSinCambiosAsync([f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia], huellasDeLosDemas);
        Assert.Equal([e.IdEmpresa], await EmpresasDeAsync(f.A2));
        Assert.Empty(await EmpresasDeAsync(f.A1));
        Assert.Empty(await EmpresasDeAsync(f.A3));
    }

    /// <summary>"Solo este" es una decisión explícita de salir de la familia y se respeta aunque la edición no
    /// toque ningún campo compartido: el artículo sale y se escriben sus campos propios. Los demás miembros
    /// siguen en la familia, sin cambios.</summary>
    [Fact]
    public async Task ConAlcanceSoloEsteSinCambioCompartidoElArticuloSaleDeLaFamiliaYEscribeSusCamposPropios()
    {
        using var e = await apoyo.PrepararAsync(nameof(ConAlcanceSoloEsteSinCambioCompartidoElArticuloSaleDeLaFamiliaYEscribeSusCamposPropios));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasDeLosDemas = await HuellasAsync(f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia);

        var pedido = EdicionIgualA(antes, AlcanceDeFamilia.SoloEste) with { Nombre = "a2 solo este", IdMarca = e.Marcas[1] };

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Null((await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!.IdFamilia);

        var despues = await apoyo.LeerAsync(f.A2);
        Assert.Null(despues.IdFamilia);
        Assert.Equal("a2 solo este", despues.Nombre);
        Assert.Equal(e.Marcas[1], despues.IdMarca);
        Assert.Equal(ValoresCompartidosDeFamilia.De(antes), ValoresCompartidosDeFamilia.De(despues));

        await AfirmarSinCambiosAsync([f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia], huellasDeLosDemas);
    }

    private async Task<List<int>> EmpresasDeAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.ArticulosEmpresas
            .Where(ae => ae.IdArticulo == idArticulo)
            .Select(ae => ae.IdEmpresa)
            .OrderBy(id => id)
            .ToListAsync();
    }

    // =================================================================================================
    // Miembro que cambia un campo compartido: alcance_requerido, o la decisión del cliente
    // =================================================================================================

    [Fact]
    public async Task UnCambioCompartidoSinAlcanceDa409AlcanceRequeridoNombraLaFamiliaYNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCambioCompartidoSinAlcanceDa409AlcanceRequeridoNombraLaFamiliaYNoEscribeNada));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var todos = new[] { f.A1, f.A2, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia };
        var huellasAntes = await HuellasAsync(todos);

        var pedido = ConCompartidos(
            EdicionIgualA(antes) with { Nombre = "no se escribe" }, ValoresConUnCampoCambiado(e, "costo_lista"));

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("alcance_requerido", codigo);

        // Nombra la familia y cuenta solo los miembros VIVOS (3, no los 4 que apuntan a ella).
        Assert.Contains("Gaseosas", mensaje, StringComparison.Ordinal);
        Assert.Contains("3 artículos", mensaje, StringComparison.Ordinal);

        await AfirmarSinCambiosAsync(todos, huellasAntes);
    }

    /// <summary>Cada uno de los trece campos compartidos, cambiado solo, con alcance <c>Familia</c>: llega a
    /// TODOS los miembros vivos y a ningún otro artículo, y los campos propios del pedido son solo del
    /// artículo editado. Un caso por campo, porque cada uno tiene su mutante: olvidar uno en el mapeo del
    /// pedido, en la comparación o en la copia deja verdes los otros doce.</summary>
    [Theory]
    [MemberData(nameof(Columnas))]
    public async Task ConAlcanceFamiliaUnCambioDeUnCampoCompartidoLlegaATodosLosMiembrosVivosYSoloEseCampo(string columna)
    {
        using var e = await apoyo.PrepararAsync(nameof(ConAlcanceFamiliaUnCambioDeUnCampoCompartidoLlegaATodosLosMiembrosVivosYSoloEseCampo));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antesDeA1 = await apoyo.LeerAsync(f.A1);
        var antesDeA2 = await apoyo.LeerAsync(f.A2);
        var antesDeA3 = await apoyo.LeerAsync(f.A3);
        var huellasDeLosAjenos = await HuellasAsync(f.DeBaja, f.Suelto, f.DeOtraFamilia);

        var esperados = ValoresConUnCampoCambiado(e, columna);
        var pedido = ConCompartidos(
            EdicionIgualA(antesDeA2, AlcanceDeFamilia.Familia) with
            {
                Nombre = "a2 editado", Descripcion = "segundo editado", IdMarca = e.Marcas[1], Activo = false
            },
            esperados);

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var devuelto = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Equal(f.A2, devuelto.Id);
        Assert.Equal(f.Familia, devuelto.IdFamilia);

        foreach (var (anterior, id) in new[] { (antesDeA1, f.A1), (antesDeA2, f.A2), (antesDeA3, f.A3) })
        {
            var miembro = await apoyo.LeerAsync(id);

            Assert.Equal(esperados, ValoresCompartidosDeFamilia.De(miembro));
            Assert.Equal(f.Familia, miembro.IdFamilia);
            Assert.True(miembro.UpdatedAt > anterior.UpdatedAt);
            Assert.Null(miembro.DeletedAt);
        }

        // Los campos propios: los del pedido son solo de a2; a1 y a3 conservan los suyos.
        var a2 = await apoyo.LeerAsync(f.A2);
        Assert.Equal("a2 editado", a2.Nombre);
        Assert.Equal("segundo editado", a2.Descripcion);
        Assert.Equal(e.Marcas[1], a2.IdMarca);
        Assert.False(a2.Activo);

        foreach (var (antes, id) in new[] { (antesDeA1, f.A1), (antesDeA3, f.A3) })
        {
            var miembro = await apoyo.LeerAsync(id);

            Assert.Equal(antes.CodigoInterno, miembro.CodigoInterno);
            Assert.Equal(antes.Nombre, miembro.Nombre);
            Assert.Equal(antes.Descripcion, miembro.Descripcion);
            Assert.Equal(antes.IdMarca, miembro.IdMarca);
            Assert.Equal(antes.Activo, miembro.Activo);
            Assert.Equal(antes.DisponibleParaTodas, miembro.DisponibleParaTodas);
        }

        await AfirmarSinCambiosAsync([f.DeBaja, f.Suelto, f.DeOtraFamilia], huellasDeLosAjenos);
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

    /// <summary><c>acumula_en_venta</c> cambiado por un miembro es un cambio compartido como cualquier otro:
    /// sin alcance se rechaza con 409 <c>alcance_requerido</c> y no se escribe nada, ni los campos propios
    /// del pedido ni el campo en ningún artículo.</summary>
    [Fact]
    public async Task UnCambioDeAcumulaEnVentaSinAlcanceDa409AlcanceRequeridoYNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCambioDeAcumulaEnVentaSinAlcanceDa409AlcanceRequeridoYNoEscribeNada));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var todos = new[] { f.A1, f.A2, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia };
        var huellasAntes = await HuellasAsync(todos);

        var pedido = ConCompartidos(
            EdicionIgualA(antes) with { Nombre = "no se escribe" }, ValoresConUnCampoCambiado(e, "acumula_en_venta"));

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("alcance_requerido", (await ProblemaAsync(respuesta)).Codigo);
        await AfirmarSinCambiosAsync(todos, huellasAntes);
    }

    /// <summary><c>acumula_en_venta</c> es el único campo compartido cuyo <c>null</c> en el pedido significa
    /// "conservar el valor guardado". Dejarlo en <c>null</c> no es un cambio compartido: sin alcance no se exige
    /// decidir, con <c>Familia</c> no hay nada que replicar y con "solo este" el artículo sale de la familia con
    /// su valor guardado. En los tres casos el campo sigue valiendo lo guardado en todos los miembros y solo el
    /// artículo editado escribe sus campos propios. Se prueba con los dos valores guardados porque cada uno mata
    /// a un mutante distinto: leer el <c>null</c> como <c>true</c> o como <c>false</c> lo convierte en un cambio
    /// solo cuando lo guardado es lo contrario.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, AlcanceDeFamilia.Familia)]
    [InlineData(false, AlcanceDeFamilia.Familia)]
    [InlineData(true, AlcanceDeFamilia.SoloEste)]
    [InlineData(false, AlcanceDeFamilia.SoloEste)]
    public async Task UnPedidoQueOmiteAcumulaEnVentaConservaElValorGuardadoYNoCuentaComoCambioCompartido(
        bool guardado, AlcanceDeFamilia? alcance)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnPedidoQueOmiteAcumulaEnVentaConservaElValorGuardadoYNoCuentaComoCambioCompartido));
        var f = await SembrarFamiliaConVecinosAsync(e, acumulaEnVenta: guardado);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasDeLosDemas = await HuellasAsync(f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia);

        var pedido = EdicionIgualA(antes, alcance) with
        {
            AcumulaEnVenta = null, Nombre = "a2 editado", IdMarca = e.Marcas[1]
        };

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(guardado, (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!.AcumulaEnVenta);

        var despues = await apoyo.LeerAsync(f.A2);
        int? familiaEsperada = alcance == AlcanceDeFamilia.SoloEste ? null : f.Familia;
        Assert.Equal(guardado, despues.AcumulaEnVenta);
        Assert.Equal(ValoresCompartidosDeFamilia.De(antes), ValoresCompartidosDeFamilia.De(despues));
        Assert.Equal("a2 editado", despues.Nombre);
        Assert.Equal(e.Marcas[1], despues.IdMarca);
        Assert.Equal(familiaEsperada, despues.IdFamilia);

        await AfirmarSinCambiosAsync([f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia], huellasDeLosDemas);
    }

    /// <summary>Con alcance <c>Familia</c> y un cambio en OTRO campo compartido, el pedido que omite
    /// <c>acumula_en_venta</c> aplica a todos los miembros vivos el valor que ese campo tiene en el artículo
    /// editado: reciben el cambio de <c>costo_lista</c> y siguen con el <c>acumula_en_venta</c> guardado, sea
    /// <c>true</c> o <c>false</c>. Ni el miembro dado de baja, ni un artículo suelto, ni uno de otra familia
    /// cambian.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConAlcanceFamiliaUnPedidoQueOmiteAcumulaEnVentaAplicaElValorGuardadoDelEditadoATodosLosMiembros(bool guardado)
    {
        using var e = await apoyo.PrepararAsync(nameof(ConAlcanceFamiliaUnPedidoQueOmiteAcumulaEnVentaAplicaElValorGuardadoDelEditadoATodosLosMiembros));
        var f = await SembrarFamiliaConVecinosAsync(e, acumulaEnVenta: guardado);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasDeLosAjenos = await HuellasAsync(f.DeBaja, f.Suelto, f.DeOtraFamilia);

        var esperados = ValoresBase(e) with { AcumulaEnVenta = guardado, CostoLista = 60m };
        var pedido = ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), esperados) with { AcumulaEnVenta = null };

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        foreach (var id in new[] { f.A1, f.A2, f.A3 })
        {
            var miembro = await apoyo.LeerAsync(id);

            Assert.Equal(esperados, ValoresCompartidosDeFamilia.De(miembro));
            Assert.Equal(f.Familia, miembro.IdFamilia);
        }

        await AfirmarSinCambiosAsync([f.DeBaja, f.Suelto, f.DeOtraFamilia], huellasDeLosAjenos);
    }

    /// <summary>La disponibilidad por empresa es un campo propio: con alcance <c>Familia</c> la restricción del
    /// pedido (<c>disponible_para_todas = false</c> con su subconjunto) es solo del artículo editado, y los
    /// demás miembros no ganan ni pierden filas de <c>articulos_empresas</c>.</summary>
    [Fact]
    public async Task ConAlcanceFamiliaLaDisponibilidadPorEmpresaEsSoloDelArticuloEditado()
    {
        using var e = await apoyo.PrepararAsync(nameof(ConAlcanceFamiliaLaDisponibilidadPorEmpresaEsSoloDelArticuloEditado));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);

        var pedido = ConCompartidos(
            EdicionIgualA(antes, AlcanceDeFamilia.Familia) with { DisponibleParaTodas = false, IdsEmpresas = [e.IdEmpresa] },
            ValoresConUnCampoCambiado(e, "costo_nominal"));

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.False((await apoyo.LeerAsync(f.A2)).DisponibleParaTodas);
        Assert.Equal([e.IdEmpresa], await EmpresasDeAsync(f.A2));

        foreach (var id in new[] { f.A1, f.A3 })
        {
            Assert.True((await apoyo.LeerAsync(id)).DisponibleParaTodas);
            Assert.Empty(await EmpresasDeAsync(id));
            Assert.Equal(45m, (await apoyo.LeerAsync(id)).CostoNominal);
        }
    }

    [Fact]
    public async Task ConAlcanceSoloEsteUnCambioCompartidoSoloLoAplicaAlArticuloEditadoYLoSacaDeLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(ConAlcanceSoloEsteUnCambioCompartidoSoloLoAplicaAlArticuloEditadoYLoSacaDeLaFamilia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasDeLosDemas = await HuellasAsync(f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia);

        var nuevos = ValoresConUnCampoCambiado(e, "id_categoria");
        var pedido = ConCompartidos(
            EdicionIgualA(antes, AlcanceDeFamilia.SoloEste) with { Nombre = "a2 solo este", IdMarca = e.Marcas[1] }, nuevos);

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var devuelto = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
        Assert.Null(devuelto.IdFamilia);
        Assert.Equal(e.Categorias[1], devuelto.IdCategoria);

        var despues = await apoyo.LeerAsync(f.A2);
        Assert.Null(despues.IdFamilia);
        Assert.Equal("a2 solo este", despues.Nombre);
        Assert.Equal(nuevos, ValoresCompartidosDeFamilia.De(despues));

        await AfirmarSinCambiosAsync([f.A1, f.A3, f.DeBaja, f.Suelto, f.DeOtraFamilia], huellasDeLosDemas);

        // Ya no es miembro: un cambio compartido sin alcance se comporta como el de cualquier artículo suelto
        // y los que quedaron en la familia no se enteran.
        var siguiente = ConCompartidos(EdicionIgualA(despues), ValoresConUnCampoCambiado(e, "costo_lista"));
        var respuestaSiguiente = await PutArticuloAsync(e.Admin, f.A2, siguiente);

        Assert.Equal(HttpStatusCode.OK, respuestaSiguiente.StatusCode);
        Assert.Equal(60m, (await apoyo.LeerAsync(f.A2)).CostoLista);
        Assert.Equal(50m, (await apoyo.LeerAsync(f.A1)).CostoLista);
        Assert.Equal(50m, (await apoyo.LeerAsync(f.A3)).CostoLista);
    }

    // =================================================================================================
    // Alcance inválido y precedencia del 404
    // =================================================================================================

    /// <summary>Un ordinal que no es el de ninguno de los dos valores llega al servidor (el conversor JSON
    /// acepta el ordinal además del nombre) y no se interpreta como ningún alcance ni se ignora: 400
    /// <c>alcance_invalido</c> y nada escrito. El <c>0</c> es el caso que importa: es lo que produce un entero
    /// sin inicializar y no elige ningún alcance.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task UnOrdinalDeAlcanceQueNoEsNingunValorDa400AlcanceInvalidoYNoEscribeNada(int ordinal)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnOrdinalDeAlcanceQueNoEsNingunValorDa400AlcanceInvalidoYNoEscribeNada));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var todos = new[] { f.A1, f.A2, f.A3 };
        var huellasAntes = await HuellasAsync(todos);

        var cuerpo = new Dictionary<string, object?>
        {
            ["nombre"] = "no se escribe", ["descripcion"] = antes.Descripcion, ["idArea"] = antes.IdArea,
            ["idCategoria"] = antes.IdCategoria, ["idMarca"] = antes.IdMarca, ["idGrupo"] = antes.IdGrupo,
            ["idProveedorHabitual"] = antes.IdProveedorHabitual, ["idAlicuotaIva"] = antes.IdAlicuotaIva,
            ["unidadVenta"] = "Unidad", ["unidadesPorBulto"] = antes.UnidadesPorBulto, ["esProducto"] = antes.EsProducto,
            ["costoLista"] = 60m, ["descuentoProveedor"] = antes.DescuentoProveedor,
            ["costoNominal"] = antes.CostoNominal, ["disponibleParaTodas"] = true, ["idsEmpresas"] = null,
            ["activo"] = antes.Activo, ["controlaLote"] = antes.ControlaLote,
            ["acumulaEnVenta"] = antes.AcumulaEnVenta, ["alcance"] = ordinal
        };

        var respuesta = await e.Admin.PutAsJsonAsync($"/api/articulos/{f.A2}", cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("alcance_invalido", (await ProblemaAsync(respuesta)).Codigo);
        await AfirmarSinCambiosAsync(todos, huellasAntes);
    }

    /// <summary>El 404 de un artículo inexistente precede a cualquier validación del pedido, incluida la del
    /// alcance: hasta esta edición era la primera sentencia del método y el contrato no cambia.</summary>
    [Fact]
    public async Task UnArticuloInexistenteDa404AunConUnAlcanceInvalido()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnArticuloInexistenteDa404AunConUnAlcanceInvalido));
        var antes = await apoyo.LeerAsync(await apoyo.SembrarArticuloAsync(e, "cualquiera", ValoresBase(e)));

        var cuerpo = new Dictionary<string, object?>
        {
            ["nombre"] = antes.Nombre, ["idArea"] = antes.IdArea, ["idAlicuotaIva"] = antes.IdAlicuotaIva,
            ["unidadVenta"] = "Unidad", ["esProducto"] = true, ["disponibleParaTodas"] = true, ["activo"] = true,
            ["controlaLote"] = false, ["alcance"] = 99
        };

        var respuesta = await e.Admin.PutAsJsonAsync("/api/articulos/999999999", cuerpo);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
    }

    // =================================================================================================
    // Todo o nada: ningún rechazo ni fallo deja un miembro a medias
    // =================================================================================================

    private (WaysDbContext Db, ServicioDeArticulos Servicio) CrearServicio(
        Entorno e, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptores)
    {
        var db = fixture.CrearContextoDeAplicacionConReintentos(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), interceptores);
        var reloj = new RelojReal();
        var contexto = new ContextoFijo(e.IdTenant, e.IdActorAdmin);

        return (db, new ServicioDeArticulos(
            db, reloj, contexto, new ServicioDeLotes(db, reloj, contexto), new GuardaDeReferencias(db, new InspectorDeUso(db))));
    }

    /// <summary>Un contexto y un servicio, dos escrituras seguidas: lo que hace cualquier llamador que atrapa
    /// un rechazo y sigue con otro artículo sobre el mismo contexto del request. La primera es una edición
    /// que cambia un campo compartido de un miembro sin alcance y se rechaza. Ninguna entidad puede quedar
    /// mutada en el contexto: si quedara, el guardado de la escritura siguiente —de otro artículo— la
    /// escribiría por detrás y la familia quedaría con un miembro distinto de los demás.</summary>
    [Fact]
    public async Task UnRechazoPorAlcanceRequeridoNoDejaMutacionesRastreadasYLaSiguienteEscrituraDelMismoContextoNoLasGuarda()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnRechazoPorAlcanceRequeridoNoDejaMutacionesRastreadasYLaSiguienteEscrituraDelMismoContextoNoLasGuarda));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antesDeA2 = await apoyo.LeerAsync(f.A2);
        var antesDelSuelto = await apoyo.LeerAsync(f.Suelto);
        var huellasDeLaFamilia = await HuellasAsync(f.A1, f.A2, f.A3);

        var (db, servicio) = CrearServicio(e);
        await using var _ = db;

        var rechazo = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(
            f.A2, ConCompartidos(EdicionIgualA(antesDeA2) with { Nombre = "no se escribe" }, ValoresConUnCampoCambiado(e, "costo_lista"))));
        Assert.Equal("alcance_requerido", rechazo.Codigo);

        Assert.DoesNotContain(
            db.ChangeTracker.Entries(),
            entrada => entrada.State is EntityState.Modified or EntityState.Added or EntityState.Deleted);

        await servicio.ActualizarAsync(f.Suelto, EdicionIgualA(antesDelSuelto) with { Nombre = "suelto editado" });

        Assert.Equal("suelto editado", (await apoyo.LeerAsync(f.Suelto)).Nombre);
        await AfirmarSinCambiosAsync([f.A1, f.A2, f.A3], huellasDeLaFamilia);
    }

    /// <summary>Una referencia que ya no existe (un área dada de baja) rechaza la edición ANTES de escribir:
    /// ningún miembro cambia, tampoco con alcance <c>Familia</c>.</summary>
    [Fact]
    public async Task UnaReferenciaDadaDeBajaEnUnaEdicionDeFamiliaDa400YNingunMiembroCambia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaDadaDeBajaEnUnaEdicionDeFamiliaDa400YNingunMiembroCambia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var todos = new[] { f.A1, f.A2, f.A3, f.Suelto };
        var huellasAntes = await HuellasAsync(todos);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            (await db.Areas.IgnoreQueryFilters().SingleAsync(a => a.Id == e.Areas[1])).DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var respuesta = await PutArticuloAsync(
            e.Admin, f.A2, ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "id_area")));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("referencia_invalida", (await ProblemaAsync(respuesta)).Codigo);
        await AfirmarSinCambiosAsync(todos, huellasAntes);
    }

    /// <summary>Si el guardado falla, la transacción entera se revierte —ningún miembro queda cambiado— y la
    /// edición no se reintenta: el contexto es el de la API, con <c>EnableRetryOnFailure</c>, y el
    /// interceptor rompe solo el primer <c>UPDATE</c> de <c>articulos</c>. Con reintento el segundo intento
    /// escribiría la familia y <see cref="InterceptorQueRompeLaPrimeraEscritura.Intentos"/> sería dos.</summary>
    [Fact]
    public async Task UnFalloAlGuardarLaEdicionDeLaFamiliaNoDejaNingunMiembroCambiadoNiSeReintenta()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnFalloAlGuardarLaEdicionDeLaFamiliaNoDejaNingunMiembroCambiadoNiSeReintenta));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var familia = new[] { f.A1, f.A2, f.A3 };
        var huellasAntes = await HuellasAsync(familia);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("articulos", "40001", ClaseDeSentencia.Update);
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var pedido = ConCompartidos(
            EdicionIgualA(antes, AlcanceDeFamilia.Familia) with { Nombre = "no se escribe" }, ValoresConUnCampoCambiado(e, "costo_lista"));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.ActualizarAsync(f.A2, pedido));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        await AfirmarSinCambiosAsync(familia, huellasAntes);
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
    // Reconciliación de lotes: una por cada miembro cuyo controla_lote pasó de false a true
    // =================================================================================================

    private async Task SembrarStockAsync(Entorno e, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        db.Stock.Add(new Stock
        {
            IdArticulo = idArticulo, IdPuntoVenta = e.IdPuntoVenta, IdTenant = e.IdTenant, Cantidad = cantidad
        });
        await db.SaveChangesAsync();
    }

    /// <summary>El flip de <c>controla_lote</c> de <c>false</c> a <c>true</c> alcanza a toda la familia, y la
    /// reconciliación de lotes corre por CADA miembro con stock —no solo por el editado—: cada uno queda con
    /// su par de movimientos de reclasificación y su stock en el lote sin identificar. El miembro sin stock no
    /// escribe nada, y un artículo suelto con stock tampoco: no flipeó.</summary>
    [Fact]
    public async Task UnFlipDeControlaLoteDeUnaFamiliaReconciliaCadaMiembroQueFlipeo()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnFlipDeControlaLoteDeUnaFamiliaReconciliaCadaMiembroQueFlipeo));
        var f = await SembrarFamiliaConVecinosAsync(e);

        var habilitado = await e.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={e.IdEmpresa}", new ParametroAlta("lotes_habilitado", "true", null));
        Assert.Equal(HttpStatusCode.OK, habilitado.StatusCode);

        await SembrarStockAsync(e, f.A1, 60m);
        await SembrarStockAsync(e, f.A2, 25m);
        await SembrarStockAsync(e, f.Suelto, 99m);

        var antes = await apoyo.LeerAsync(f.A2);
        var pedido = ConCompartidos(
            EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "controla_lote"));

        var respuesta = await PutArticuloAsync(e.Admin, f.A2, pedido);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));

        foreach (var (id, cantidad) in new[] { (f.A1, 60m), (f.A2, 25m) })
        {
            Assert.Equal(2, await db.MovimientosStock.CountAsync(
                m => m.IdArticulo == id && m.IdPuntoVenta == e.IdPuntoVenta && m.Motivo == MotivoStock.Reclasificacion));

            var loteSinIdentificar = await db.Lotes.SingleAsync(l => l.IdArticulo == id && l.EsSinIdentificar);
            var stockLote = await db.StockLotes.SingleAsync(
                sl => sl.IdArticulo == id && sl.IdPuntoVenta == e.IdPuntoVenta && sl.IdLote == loteSinIdentificar.Id);
            Assert.Equal(cantidad, stockLote.Cantidad);
        }

        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == f.A3));
        Assert.Equal(0, await db.MovimientosStock.CountAsync(
            m => m.IdArticulo == f.Suelto && m.Motivo == MotivoStock.Reclasificacion));
    }

    // =================================================================================================
    // El id de la familia en la lectura
    // =================================================================================================

    /// <summary>El detalle, el listado y la respuesta de la edición exponen la familia tal como está
    /// guardada. No se anula aunque la fila de la familia esté dada de baja: los escritores definen la
    /// pertenencia por <c>id_familia</c> y la baja del artículo, no por el estado de la familia, y un lector
    /// que la anulara discreparía con ellos sobre quién es miembro.</summary>
    [Fact]
    public async Task ElDetalleYElListadoExponenElIdDeLaFamiliaTalComoEstaGuardado()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElDetalleYElListadoExponenElIdDeLaFamiliaTalComoEstaGuardado));
        var f = await SembrarFamiliaConVecinosAsync(e);

        var detalleDelMiembro = (await e.Admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{f.A1}", OpcionesJson))!;
        var detalleDelSuelto = (await e.Admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{f.Suelto}", OpcionesJson))!;
        Assert.Equal(f.Familia, detalleDelMiembro.IdFamilia);
        Assert.Null(detalleDelSuelto.IdFamilia);

        var pagina = (await e.Admin.GetFromJsonAsync<PaginaDe<ArticuloListado>>("/api/articulos?tamanio=100", OpcionesJson))!;
        var porId = pagina.Items.ToDictionary(a => a.Id);
        Assert.Equal(f.Familia, porId[f.A1].IdFamilia);
        Assert.Equal(f.Familia, porId[f.A3].IdFamilia);
        Assert.Equal(f.OtraFamilia, porId[f.DeOtraFamilia].IdFamilia);
        Assert.Null(porId[f.Suelto].IdFamilia);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            (await db.Familias.IgnoreQueryFilters().SingleAsync(x => x.Id == f.Familia)).DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var conLaFamiliaDadaDeBaja = (await e.Admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{f.A1}", OpcionesJson))!;
        Assert.Equal(f.Familia, conLaFamiliaDadaDeBaja.IdFamilia);
    }

    // =================================================================================================
    // Locks: membresía, filas de articulos y catálogos, vistos desde pg_locks
    // =================================================================================================

    /// <summary>"Solo este" cambia la pertenencia y por eso pide el lock EXCLUSIVO: con un compartido ajeno
    /// sostenido (stand-in de una escritura de precios de otra familia), queda esperando —observado pidiendo
    /// <c>ExclusiveLock</c>— hasta que el otro lo libera. Espera ANTES de tomar ningún otro lock ni escribir:
    /// la membresía es la primera sentencia de la transacción.</summary>
    [Fact]
    public async Task SoloEstePideElLockExclusivoYEsperaAUnCompartidoAjeno()
    {
        using var e = await apoyo.PrepararAsync(nameof(SoloEstePideElLockExclusivoYEsperaAUnCompartidoAjeno));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var escritura = PutArticuloAsync(e.Admin, f.A2, EdicionIgualA(antes, AlcanceDeFamilia.SoloEste) with { Nombre = "solo este" });

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "El 'solo este' nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(escritura.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Null((await apoyo.LeerAsync(f.A2)).IdFamilia);
        Assert.Equal(f.Familia, (await apoyo.LeerAsync(f.A1)).IdFamilia);
    }

    /// <summary>El lock compartido no espera a otro compartido: una edición que no cambia la pertenencia
    /// —de un miembro sin alcance, de un miembro con alcance <c>Familia</c> y de un artículo suelto— termina
    /// SIN que el compartido ajeno se libere. Con el lock exclusivo, las tres quedarían esperando.</summary>
    [Fact]
    public async Task UnaEdicionQueNoCambiaLaMembresiaNoEsperaAUnLockCompartidoAjeno()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionQueNoCambiaLaMembresiaNoEsperaAUnLockCompartidoAjeno));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antesDeA1 = await apoyo.LeerAsync(f.A1);
        var antesDeA2 = await apoyo.LeerAsync(f.A2);
        var antesDelSuelto = await apoyo.LeerAsync(f.Suelto);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var espera = TimeSpan.FromSeconds(15);
        var sinAlcance = await PutArticuloAsync(e.Admin, f.A1, EdicionIgualA(antesDeA1) with { Nombre = "a1 editado" }).WaitAsync(espera);
        var deLaFamilia = await PutArticuloAsync(
            e.Admin, f.A2,
            ConCompartidos(EdicionIgualA(antesDeA2, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "costo_lista")))
            .WaitAsync(espera);
        var delSuelto = await PutArticuloAsync(e.Admin, f.Suelto, EdicionIgualA(antesDelSuelto) with { Nombre = "suelto editado" }).WaitAsync(espera);

        Assert.Equal(HttpStatusCode.OK, sinAlcance.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deLaFamilia.StatusCode);
        Assert.Equal(HttpStatusCode.OK, delSuelto.StatusCode);
    }

    /// <summary>Un escritor de membresía (stand-in de entrar a una familia) sostiene el lock EXCLUSIVO y
    /// mueve a un artículo suelto a la familia SIN commitear. La edición con alcance <c>Familia</c> queda
    /// esperando ESE lock —observado pidiendo el modo compartido y antes de haber escrito o bloqueado nada—,
    /// y cuando el escritor comitea lee la pertenencia nueva: el artículo que entró recibe el cambio
    /// compartido. Si el lock se tomara después de leer la familia, la edición ya tendría bloqueados a los
    /// miembros viejos y el que entró quedaría con valores distintos.</summary>
    [Fact]
    public async Task UnEscritorDeMembresiaConElLockExclusivoHaceEsperarALaEdicionYLaEdicionVeLaFamiliaNueva()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnEscritorDeMembresiaConElLockExclusivoHaceEsperarALaEdicionYLaEdicionVeLaFamiliaNueva));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A1);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EjecutarAsync(
            sostenedor, transaccion, "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2", f.Familia, f.Suelto);

        var escritura = PutArticuloAsync(
            e.Admin, f.A1,
            ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "costo_lista")));

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "La edición nunca se observó esperando el lock de membresía: la prueba no está probando la carrera.");

        Assert.Equal("ShareLock", esperando.Modo);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);

        foreach (var id in new[] { f.A1, f.A2, f.A3, f.Suelto })
        {
            Assert.Equal(60m, (await apoyo.LeerAsync(id)).CostoLista);
        }
    }

    /// <summary>Orden de locks, paso (2) después de la membresía: un tercero sostiene <c>FOR UPDATE</c> sobre
    /// la fila del miembro de id más alto. La edición con alcance <c>Familia</c> espera ESA fila, con el lock
    /// de membresía ya tomado —en modo compartido, el único lock advisory que tiene— y todavía sin los
    /// chequeos de catálogo: un <c>FOR UPDATE NOWAIT</c> sobre el área que el pedido referencia sale bien, o
    /// sea que la edición no le tomó todavía el <c>FOR KEY SHARE</c>.</summary>
    [Fact]
    public async Task LasFilasDeLosMiembrosSeBloqueanDespuesDeLaMembresia()
    {
        using var e = await apoyo.PrepararAsync(nameof(LasFilasDeLosMiembrosSeBloqueanDespuesDeLaMembresia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A1);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", f.A3);

        var escritura = PutArticuloAsync(
            e.Admin, f.A1,
            ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "costo_lista")));

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición nunca se observó esperando la fila de un miembro.");

        var unico = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        Assert.Equal(1, unico.ObjSubId);
        Assert.Equal((alto, bajo), (unico.ClassId, unico.ObjId));
        Assert.Equal("ShareLock", unico.Modo);

        await using (var comando = new NpgsqlCommand(
            "SELECT 1 FROM areas WHERE id_area = $1 FOR UPDATE NOWAIT", poll))
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = antes.IdArea });

            Assert.NotNull(await comando.ExecuteScalarAsync());
        }

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.OK, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Equal(60m, (await apoyo.LeerAsync(f.A3)).CostoLista);
    }

    /// <summary>Las filas de los miembros se bloquean <c>FOR NO KEY UPDATE</c> y no <c>FOR UPDATE</c>: el
    /// segundo chocaría con el <c>FOR KEY SHARE</c> que una venta toma sobre el artículo por las FK de sus
    /// renglones. Un tercero sostiene a mano un <c>FOR KEY SHARE</c> sobre un miembro y la edición de la
    /// familia tiene que terminar sin esperarlo.</summary>
    [Fact]
    public async Task UnForKeyShareAjenoSobreUnMiembroNoHaceEsperarALaEdicionDeLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnForKeyShareAjenoSobreUnMiembroNoHaceEsperarALaEdicionDeLaFamilia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A1);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR KEY SHARE", f.A3);

        var respuesta = await PutArticuloAsync(
            e.Admin, f.A1,
            ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "costo_lista")))
            .WaitAsync(EsperaMaxima);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Las filas se bloquean en orden ASCENDENTE de id. Un tercero sostiene <c>FOR UPDATE</c> sobre
    /// el miembro de id más BAJO: la edición espera esa fila sin tener ninguna otra. Un <c>FOR NO KEY UPDATE
    /// NOWAIT</c> desde otra conexión sobre los de id más alto tiene que salir bien; si la edición los
    /// bloqueara de mayor a menor ya los tendría y fallaría con <c>55P03</c>.</summary>
    [Fact]
    public async Task LasFilasDeLosMiembrosSeBloqueanEnOrdenAscendente()
    {
        using var e = await apoyo.PrepararAsync(nameof(LasFilasDeLosMiembrosSeBloqueanEnOrdenAscendente));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", f.A1);

        var escritura = PutArticuloAsync(
            e.Admin, f.A2,
            ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "costo_lista")));

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición nunca se observó esperando la fila del miembro de id más bajo.");

        foreach (var idMasAlto in new[] { f.A2, f.A3 })
        {
            await using var comando = new NpgsqlCommand(
                "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR NO KEY UPDATE NOWAIT", poll);
            comando.Parameters.Add(new NpgsqlParameter { Value = idMasAlto });

            Assert.NotNull(await comando.ExecuteScalarAsync());
        }

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.OK, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
    }

    /// <summary>El artículo sin familia conserva su lock de siempre —<c>FOR UPDATE</c> sobre la fila propia— y
    /// lo toma DESPUÉS de la membresía: un <c>FOR KEY SHARE</c> ajeno sobre su fila lo hace esperar (el
    /// <c>FOR NO KEY UPDATE</c> de los miembros no esperaría), con el lock de membresía compartido como único
    /// lock advisory concedido.</summary>
    [Fact]
    public async Task UnArticuloSinFamiliaSigueBloqueandoSuFilaConForUpdateDespuesDeLaMembresia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnArticuloSinFamiliaSigueBloqueandoSuFilaConForUpdateDespuesDeLaMembresia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.Suelto);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR KEY SHARE", f.Suelto);

        var escritura = PutArticuloAsync(e.Admin, f.Suelto, EdicionIgualA(antes) with { Nombre = "suelto editado" });

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición del artículo sin familia nunca se observó esperando su fila: el FOR UPDATE no está.");

        var unico = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        Assert.Equal((alto, bajo), (unico.ClassId, unico.ObjId));
        Assert.Equal("ShareLock", unico.Modo);

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.OK, (await escritura.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Equal("suelto editado", (await apoyo.LeerAsync(f.Suelto)).Nombre);
    }

    /// <summary>Los miembros se leen DESPUÉS de bloquearlos. Una transacción ajena —que no toma el lock de
    /// membresía— cambia <c>costo_lista</c> de toda la familia y la sostiene sin commitear: la edición espera
    /// la fila del primer miembro. El pedido lleva el valor NUEVO de <c>costo_lista</c> y un nombre distinto;
    /// al comitear la otra transacción, lo que la edición lee bajo los locks ya es ese valor, así que no hay
    /// cambio compartido, no se exige alcance y se escribe el nombre. Si la lectura se hiciera antes de
    /// bloquear, vería el valor viejo, el pedido parecería un cambio compartido y daría
    /// <c>alcance_requerido</c>.</summary>
    [Fact]
    public async Task LaEdicionDeUnMiembroDecideConLoQueLeeDespuesDeLosLocksYNoConLaFotoPrevia()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaEdicionDeUnMiembroDecideConLoQueLeeDespuesDeLosLocksYNoConLaFotoPrevia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "UPDATE articulos SET costo_lista = $1 WHERE id_familia = $2", 60m, f.Familia);

        var pedido = ConCompartidos(
            EdicionIgualA(antes) with { Nombre = "a2 editado" }, ValoresConUnCampoCambiado(e, "costo_lista"));
        var escritura = PutArticuloAsync(e.Admin, f.A2, pedido);

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición nunca se observó esperando la fila de un miembro.");

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("a2 editado", (await apoyo.LeerAsync(f.A2)).Nombre);
        Assert.Equal(60m, (await apoyo.LeerAsync(f.A2)).CostoLista);
        Assert.Equal(60m, (await apoyo.LeerAsync(f.A1)).CostoLista);
    }

    /// <summary>El valor que el pedido toma para <c>acumula_en_venta</c> cuando lo omite sale de la misma
    /// lectura, bajo los locks, que los valores con los que se lo compara. Una transacción ajena —que no toma
    /// el lock de membresía— pasa <c>acumula_en_venta</c> de toda la familia a <c>false</c> y la sostiene sin
    /// commitear: la edición espera la fila del primer miembro. El pedido omite el campo y lleva un nombre
    /// distinto; al comitear la otra transacción, lo que la edición lee bajo los locks ya es <c>false</c> y el
    /// pedido toma ese valor, así que no hay cambio compartido, no se exige alcance y se escribe el nombre. Con
    /// un valor leído antes de bloquear (<c>true</c>) el pedido parecería cambiar el campo y daría
    /// <c>alcance_requerido</c>.</summary>
    [Fact]
    public async Task UnPedidoQueOmiteAcumulaEnVentaTomaElValorLeidoBajoLosLocksYNoUnaFotoPrevia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnPedidoQueOmiteAcumulaEnVentaTomaElValorLeidoBajoLosLocksYNoUnaFotoPrevia));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "UPDATE articulos SET acumula_en_venta = $1 WHERE id_familia = $2", false, f.Familia);

        var escritura = PutArticuloAsync(e.Admin, f.A2, EdicionIgualA(antes) with { AcumulaEnVenta = null, Nombre = "a2 editado" });

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición nunca se observó esperando la fila de un miembro.");

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var a2 = await apoyo.LeerAsync(f.A2);
        Assert.Equal("a2 editado", a2.Nombre);
        Assert.False(a2.AcumulaEnVenta);
        Assert.False((await apoyo.LeerAsync(f.A1)).AcumulaEnVenta);
    }

    // =================================================================================================
    // Un escritor que NO respeta el protocolo: las guardas de defensa
    // =================================================================================================

    /// <summary>Una escritura que cambia la pertenencia sin tomar el lock de membresía (el protocolo lo
    /// exige, nada del esquema lo fuerza) saca al artículo editado de la familia mientras la edición espera la
    /// fila de ese miembro. Al retomar, el artículo ya no es miembro: 409 <c>familia_cambio</c> en vez de
    /// escribirle a la familia un pedido que no lo incluye.</summary>
    [Fact]
    public async Task SiElArticuloEditadoSaleDeLaFamiliaSinElLockLaEdicionDeLaFamiliaSeRechaza()
    {
        using var e = await apoyo.PrepararAsync(nameof(SiElArticuloEditadoSaleDeLaFamiliaSinElLockLaEdicionDeLaFamiliaSeRechaza));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);
        var huellasAntes = await HuellasAsync(f.A1, f.A3);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET id_familia = NULL WHERE id_articulo = $1", f.A2);

        var escritura = PutArticuloAsync(
            e.Admin, f.A2,
            ConCompartidos(EdicionIgualA(antes, AlcanceDeFamilia.Familia), ValoresConUnCampoCambiado(e, "costo_lista")));

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición nunca se observó esperando la fila del artículo que sale de la familia.");

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);
        await AfirmarSinCambiosAsync([f.A1, f.A3], huellasAntes);
        Assert.Null((await apoyo.LeerAsync(f.A2)).IdFamilia);
        Assert.Equal(50m, (await apoyo.LeerAsync(f.A2)).CostoLista);
    }

    /// <summary>Lo mismo para "solo este": la fila del artículo se bloquea con la familia que se leyó bajo el
    /// lock de membresía como condición del <c>WHERE</c>. Otro escritor cambia esa familia sin respetar el lock
    /// y la edición espera su fila; al retomar, PostgreSQL reevalúa el <c>WHERE</c> sobre la versión nueva y no
    /// devuelve la fila: 409 <c>familia_cambio</c> y la pertenencia nueva queda como estaba.</summary>
    [Fact]
    public async Task SiLaFamiliaCambioSinElLockElSoloEsteDeLaEdicionSeRechazaYNoPisaLaPertenenciaNueva()
    {
        using var e = await apoyo.PrepararAsync(nameof(SiLaFamiliaCambioSinElLockElSoloEsteDeLaEdicionSeRechazaYNoPisaLaPertenenciaNueva));
        var f = await SembrarFamiliaConVecinosAsync(e);
        var antes = await apoyo.LeerAsync(f.A2);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2", f.OtraFamilia, f.A2);

        var escritura = PutArticuloAsync(
            e.Admin, f.A2, EdicionIgualA(antes, AlcanceDeFamilia.SoloEste) with { Nombre = "no se escribe" });

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "El 'solo este' nunca se observó esperando la fila del artículo.");

        await transaccion.CommitAsync();

        var respuesta = await escritura.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);

        var despues = await apoyo.LeerAsync(f.A2);
        Assert.Equal(f.OtraFamilia, despues.IdFamilia);
        Assert.Equal(antes.Nombre, despues.Nombre);
    }
}
