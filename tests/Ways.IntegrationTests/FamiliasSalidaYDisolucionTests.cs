using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>DELETE /api/familias/{id}/articulos/{idArticulo}</c> (sacar un artículo de su familia) y
/// <c>DELETE /api/familias/{id}</c> (disolverla), doc 10 §3, contra Postgres real. Las dos solo SACAN miembros: el
/// artículo que sale conserva todos sus valores —los compartidos, los propios y sus precios— y los que se quedan no
/// cambian; lo que cambia es la pertenencia (<c>id_familia</c>) y, con ella, qué escritores lo alcanzan.
///
/// <para>Las dos cambian la pertenencia: toman el lock de membresía EXCLUSIVO como primera sentencia. Las pruebas de
/// locks son rendezvous determinísticos: una conexión cruda sostiene un lock o una escritura sin comitear y la
/// operación queda observada esperando en <c>pg_locks</c>. Al mutante que toma la membresía en modo compartido o que
/// no la toma lo mata la espera observada y su modo; al que no bloquea una fila de artículo lo mata el estado final
/// de esa fila, porque el <c>UPDATE</c> esperaría igual a una fila bloqueada por otro.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasSalidaYDisolucionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static Task<HttpResponseMessage> SacarAsync(HttpClient cliente, int idFamilia, int idArticulo) =>
        cliente.DeleteAsync($"/api/familias/{idFamilia}/articulos/{idArticulo}");

    private static Task<HttpResponseMessage> DisolverAsync(HttpClient cliente, int idFamilia) =>
        cliente.DeleteAsync($"/api/familias/{idFamilia}");

    /// <summary>Todo lo que el artículo guarda, incluida su pertenencia y sus sellos.</summary>
    private static object Huella(Articulo a) => new
    {
        a.Id, a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.DisponibleParaTodas, a.IdFamilia,
        Compartidos = ValoresCompartidosDeFamilia.De(a), a.CreatedAt, a.UpdatedAt, a.DeletedAt
    };

    /// <summary>La huella del artículo SIN su pertenencia ni su <c>updated_at</c>: lo que no tiene que cambiar cuando
    /// el artículo sale de su familia.</summary>
    private static object Valores(Articulo a) => new
    {
        a.Id, a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.DisponibleParaTodas,
        Compartidos = ValoresCompartidosDeFamilia.De(a), a.CreatedAt, a.DeletedAt
    };

    private static object Huella(Familia f) => new { f.Id, f.Nombre, f.Activo, f.CreatedAt, f.UpdatedAt, f.DeletedAt };

    private async Task<List<Articulo>> LeerAsync(params int[] ids)
    {
        var articulos = new List<Articulo>(ids.Length);
        foreach (var id in ids)
        {
            articulos.Add(await apoyo.LeerAsync(id));
        }

        return articulos;
    }

    private async Task<List<(decimal Monto, DateTimeOffset Desde, DateTimeOffset? Hasta, DateTimeOffset UpdatedAt)>> PreciosAsync(
        int idArticulo, int idLista) =>
        [.. (await apoyo.FilasDePrecioAsync(idArticulo, idLista)).Select(p => (p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt))];

    private sealed record Sembrada(Entorno E, int Familia, int M1, int M2, int M3, int DeBaja, int Otra, int Otro);

    /// <summary>La familia "Gaseosas": tres miembros vivos con campos propios distintos entre sí y el mismo estado de
    /// precios (lista general: vigente 100 y pendiente 130 dentro de tres días; mayorista: vigente 80), y un miembro
    /// dado de baja. Más una familia ajena, "Otra", con su propio miembro vivo.</summary>
    private async Task<Sembrada> SembrarAsync(string nombre)
    {
        var e = await apoyo.PrepararAsync(nombre);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var otraFamilia = await apoyo.SembrarFamiliaAsync(e, "Otra");
        var @base = ValoresBase(e);

        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", @base, familia, idMarca: e.Marcas[0], descripcion: "primero");
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", @base, familia, idMarca: e.Marcas[1], descripcion: "segundo", activo: false);
        var m3 = await apoyo.SembrarArticuloAsync(e, "m3", @base, familia, idMarca: null, descripcion: null, disponibleParaTodas: false);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", @base, familia, dadoDeBaja: true);
        var otro = await apoyo.SembrarArticuloAsync(e, "otro", @base with { CostoLista = 999m }, otraFamilia);

        var desdeDelPendiente = DateTimeOffset.UtcNow.AddDays(3);
        foreach (var miembro in new[] { m1, m2, m3 })
        {
            await apoyo.SembrarPrecioPendienteAsync(e, miembro, e.IdListaGeneral, 100m, 130m, desdeDelPendiente);
            await apoyo.SembrarPrecioVigenteAsync(e, miembro, e.IdListaMayorista, 80m);
        }

        return new Sembrada(e, familia, m1, m2, m3, deBaja, otraFamilia, otro);
    }

    private static async Task<HttpResponseMessage> PostPrecioDeFamiliaAsync(Entorno e, int idArticulo, decimal precio, string? alcance)
    {
        var cuerpo = new Dictionary<string, object?> { ["idListaPrecio"] = e.IdListaMayorista, ["precio"] = precio };
        if (alcance is not null)
        {
            cuerpo["alcance"] = alcance;
        }

        return await e.Admin.PostAsJsonAsync($"/api/articulos/{idArticulo}/precios", cuerpo);
    }

    // =================================================================================================
    // Sacar un artículo
    // =================================================================================================

    /// <summary>El artículo sale: <c>id_familia</c> en <c>NULL</c>, <c>updated_at</c> que avanza, y NADA más —ningún
    /// campo compartido ni propio, ni su estado ni sus filas de precio—. Los demás miembros, el dado de baja, la
    /// familia y la familia ajena quedan idénticos, <c>updated_at</c> incluido: las huellas completas, antes y
    /// después. Y el efecto sobre los escritores es el esperado: un cambio de precio con alcance <c>Familia</c> sobre
    /// otro miembro ya no lo alcanza.</summary>
    [Fact]
    public async Task UnArticuloQueSaleConservaTodosSusValoresYLosDemasNoCambian()
    {
        var s = await SembrarAsync(nameof(UnArticuloQueSaleConservaTodosSusValoresYLosDemasNoCambian));
        using var e = s.E;

        var m2Antes = await apoyo.LeerAsync(s.M2);
        var intactosAntes = (await LeerAsync(s.M1, s.M3, s.DeBaja, s.Otro)).Select(Huella).ToList();
        var familiaAntes = Huella(await apoyo.LeerFamiliaAsync(s.Familia));
        var otraFamiliaAntes = Huella(await apoyo.LeerFamiliaAsync(s.Otra));
        var preciosGeneralAntes = await PreciosAsync(s.M2, e.IdListaGeneral);
        var preciosMayoristaAntes = await PreciosAsync(s.M2, e.IdListaMayorista);
        Assert.Equal(s.Familia, m2Antes.IdFamilia);

        var respuesta = await SacarAsync(e.Admin, s.Familia, s.M2);

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        var m2 = await apoyo.LeerAsync(s.M2);
        Assert.Null(m2.IdFamilia);
        Assert.True(m2.UpdatedAt > m2Antes.UpdatedAt);
        Assert.Equal(Valores(m2Antes), Valores(m2));
        Assert.Equal(preciosGeneralAntes, await PreciosAsync(s.M2, e.IdListaGeneral));
        Assert.Equal(preciosMayoristaAntes, await PreciosAsync(s.M2, e.IdListaMayorista));

        var intactosDespues = (await LeerAsync(s.M1, s.M3, s.DeBaja, s.Otro)).Select(Huella).ToList();
        Assert.Equal(intactosAntes, intactosDespues);
        Assert.Equal(familiaAntes, Huella(await apoyo.LeerFamiliaAsync(s.Familia)));
        Assert.Equal(otraFamiliaAntes, Huella(await apoyo.LeerFamiliaAsync(s.Otra)));

        // Los escritores ven el cambio: la familia ahora son m1 y m3, y m2 no recibe el precio nuevo.
        Assert.Equal(HttpStatusCode.Created, (await PostPrecioDeFamiliaAsync(e, s.M1, 95m, "Familia")).StatusCode);
        foreach (var miembro in new[] { s.M1, s.M3 })
        {
            Assert.Equal(95m, (await apoyo.FilasDePrecioAsync(miembro, e.IdListaMayorista)).Single(p => p.VigenteHasta is null).Monto);
        }

        Assert.Equal(preciosMayoristaAntes, await PreciosAsync(s.M2, e.IdListaMayorista));

        var listado = await e.Admin.GetFromJsonAsync<List<FamiliaListado>>("/api/familias", OpcionesJson);
        Assert.Equal(2, listado!.Single(f => f.Id == s.Familia).CantidadArticulos);
    }

    /// <summary>Sacar al último miembro vivo no disuelve la familia: sigue existiendo, vacía, con su nombre y su estado,
    /// y el detalle no tiene referencia (ni valores ni precios).</summary>
    [Fact]
    public async Task LaFamiliaQueSeQuedaSinMiembrosVivosSigueExistiendoVacia()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaFamiliaQueSeQuedaSinMiembrosVivosSigueExistiendoVacia));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Único miembro");
        var miembro = await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);

        Assert.Equal(HttpStatusCode.NoContent, (await SacarAsync(e.Admin, familia, miembro)).StatusCode);

        var listado = await e.Admin.GetFromJsonAsync<List<FamiliaListado>>("/api/familias", OpcionesJson);
        Assert.Equal([new FamiliaListado(familia, "Único miembro", true, 0)], listado);

        var detalle = (await e.Admin.GetFromJsonAsync<FamiliaDetalle>($"/api/familias/{familia}", OpcionesJson))!;
        Assert.Empty(detalle.Articulos);
        Assert.Null(detalle.Valores);
        Assert.Empty(detalle.Precios);
        Assert.Null((await apoyo.LeerFamiliaAsync(familia)).DeletedAt);
    }

    /// <summary>La familia y el artículo se buscan en ese orden: una familia que no existe, está dada de baja o es de
    /// otro tenant da 404 aunque el artículo exista y no pertenezca a ella (si el artículo se mirara primero sería un
    /// 409). Un artículo que no existe, está dado de baja o es de otro tenant también da 404. Nada se escribe.</summary>
    [Fact]
    public async Task UnaFamiliaOUnArticuloInexistentesDadosDeBajaODeOtroTenantDan404YNoEscribenNada()
    {
        var s = await SembrarAsync(nameof(UnaFamiliaOUnArticuloInexistentesDadosDeBajaODeOtroTenantDan404YNoEscribenNada));
        using var e = s.E;
        using var otro = await apoyo.PrepararAsync(nameof(UnaFamiliaOUnArticuloInexistentesDadosDeBajaODeOtroTenantDan404YNoEscribenNada) + "-ajeno");
        var familiaAjena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var articuloAjeno = await apoyo.SembrarArticuloAsync(otro, "ajeno", ValoresBase(otro), familiaAjena);
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        var miembroDeLaDadaDeBaja = await apoyo.SembrarArticuloAsync(e, "de-la-dada-de-baja", ValoresBase(e), dadaDeBaja);
        var suelto = await apoyo.SembrarArticuloAsync(e, "suelto", ValoresBase(e));

        var antes = (await LeerAsync(s.M1, s.M2, s.M3, s.DeBaja, suelto, miembroDeLaDadaDeBaja, articuloAjeno)).Select(Huella).ToList();

        var casos = new (string Descripcion, int Familia, int Articulo)[]
        {
            ("familia inexistente con un artículo suelto", 999_999_999, suelto),
            ("familia dada de baja con un miembro vivo", dadaDeBaja, miembroDeLaDadaDeBaja),
            ("familia de otro tenant", familiaAjena, articuloAjeno),
            ("artículo inexistente", s.Familia, 999_999_999),
            ("artículo dado de baja", s.Familia, s.DeBaja),
            ("artículo de otro tenant", s.Familia, articuloAjeno)
        };

        foreach (var (descripcion, familia, articulo) in casos)
        {
            var respuesta = await SacarAsync(e.Admin, familia, articulo);

            Assert.True(respuesta.StatusCode == HttpStatusCode.NotFound, $"{descripcion}: esperaba 404 y recibió {(int)respuesta.StatusCode}.");
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }

        var despues = (await LeerAsync(s.M1, s.M2, s.M3, s.DeBaja, suelto, miembroDeLaDadaDeBaja, articuloAjeno)).Select(Huella).ToList();
        Assert.Equal(antes, despues);
    }

    /// <summary>Un artículo que existe pero no es miembro de ESA familia —uno sin familia, o miembro de otra— da 409
    /// <c>familia_cambio</c>: el cliente creía otra cosa y tiene que recargar. Nada se escribe: ni el artículo suelto ni la
    /// otra familia pierden nada.</summary>
    [Fact]
    public async Task UnArticuloQueNoEsMiembroDeEsaFamiliaDa409FamiliaCambioYNoEscribeNada()
    {
        var s = await SembrarAsync(nameof(UnArticuloQueNoEsMiembroDeEsaFamiliaDa409FamiliaCambioYNoEscribeNada));
        using var e = s.E;
        var suelto = await apoyo.SembrarArticuloAsync(e, "suelto", ValoresBase(e));
        var antes = (await LeerAsync(suelto, s.Otro, s.M1)).Select(Huella).ToList();

        foreach (var (familia, articulo) in new[] { (s.Familia, suelto), (s.Familia, s.Otro), (s.Otra, s.M1) })
        {
            var respuesta = await SacarAsync(e.Admin, familia, articulo);

            Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
            Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Equal(antes, (await LeerAsync(suelto, s.Otro, s.M1)).Select(Huella).ToList());
    }

    /// <summary>Sacar cambia la pertenencia y pide el lock de membresía EXCLUSIVO: con un compartido ajeno sostenido
    /// (stand-in de una escritura de precios de la familia en curso) queda esperando —observado pidiendo
    /// <c>ExclusiveLock</c>— ANTES de tomar ningún otro lock ni de escribir nada. Cuando el otro lo libera, termina y
    /// el artículo salió.</summary>
    [Fact]
    public async Task SacarPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtro()
    {
        var s = await SembrarAsync(nameof(SacarPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtro));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var salida = SacarAsync(e.Admin, s.Familia, s.M2);

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "Sacar un artículo nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(salida.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(s.Familia, (await apoyo.LeerAsync(s.M2)).IdFamilia);

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await salida.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Null((await apoyo.LeerAsync(s.M2)).IdFamilia);
    }

    /// <summary>La fila del artículo se bloquea <c>FOR NO KEY UPDATE</c> guardada por la baja lógica: otra escritura
    /// sostiene una baja del artículo sin comitear. Sacar queda esperando esa fila con la membresía como único lock
    /// concedido y, al comitear la baja, la fila ya no cumple el guardado: 409 <c>familia_cambio</c> y el artículo
    /// dado de baja conserva su <c>id_familia</c>. Sin ese bloqueo, el <c>UPDATE</c> esperaría igual, pero al retomar
    /// escribiría sobre la fila ya dada de baja y respondería 204.</summary>
    [Fact]
    public async Task UnArticuloQueLoDanDeBajaMientrasSeEsperaSuFilaDa409YNoSeEscribeSobreLaFilaDadaDeBaja()
    {
        var s = await SembrarAsync(nameof(UnArticuloQueLoDanDeBajaMientrasSeEsperaSuFilaDa409YNoSeEscribeSobreLaFilaDadaDeBaja));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET deleted_at = now() WHERE id_articulo = $1", s.M2);

        var salida = SacarAsync(e.Admin, s.Familia, s.M2);

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "Sacar nunca se observó esperando la fila del artículo.");

        var concedidos = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        Assert.Equal((1, "ExclusiveLock"), (concedidos.ObjSubId, concedidos.Modo));

        await transaccion.CommitAsync();

        var respuesta = await salida.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_cambio", (await ProblemaAsync(respuesta)).Codigo);

        var m2 = await apoyo.LeerAsync(s.M2);
        Assert.Equal((s.Familia, true), (m2.IdFamilia, m2.DeletedAt is not null));
    }

    // =================================================================================================
    // Disolver
    // =================================================================================================

    /// <summary>Disolver deja a los miembros vivos sin familia con TODOS sus valores y da de baja la familia, con un
    /// solo "ahora": el <c>updated_at</c> de cada miembro, el <c>updated_at</c> y el <c>deleted_at</c> de la familia son
    /// el mismo instante. El miembro dado de baja conserva su <c>id_familia</c> y no cambia; la otra familia y su
    /// miembro, tampoco. La familia desaparece del listado y del detalle, su nombre se puede reutilizar, y los
    /// escritores ven artículos sueltos: un precio sin alcance es válido y solo escribe a quien se le pidió.</summary>
    [Fact]
    public async Task DisolverDejaALosMiembrosVivosSueltosConTodosSusValoresYDaDeBajaLaFamiliaConUnSoloAhora()
    {
        var s = await SembrarAsync(nameof(DisolverDejaALosMiembrosVivosSueltosConTodosSusValoresYDaDeBajaLaFamiliaConUnSoloAhora));
        using var e = s.E;

        var miembrosAntes = (await LeerAsync(s.M1, s.M2, s.M3)).ToList();
        var intactosAntes = (await LeerAsync(s.DeBaja, s.Otro)).Select(Huella).ToList();
        var familiaAntes = await apoyo.LeerFamiliaAsync(s.Familia);
        var otraFamiliaAntes = Huella(await apoyo.LeerFamiliaAsync(s.Otra));
        var preciosAntes = new List<object>();
        foreach (var miembro in new[] { s.M1, s.M2, s.M3 })
        {
            preciosAntes.Add(await PreciosAsync(miembro, e.IdListaGeneral));
            preciosAntes.Add(await PreciosAsync(miembro, e.IdListaMayorista));
        }

        var respuesta = await DisolverAsync(e.Admin, s.Familia);

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        var familia = await apoyo.LeerFamiliaAsync(s.Familia);
        var ahora = familia.DeletedAt!.Value;
        Assert.Equal(ahora, familia.UpdatedAt);
        Assert.Equal((familiaAntes.Nombre, familiaAntes.Activo, familiaAntes.CreatedAt), (familia.Nombre, familia.Activo, familia.CreatedAt));
        Assert.True(ahora > familiaAntes.UpdatedAt);

        var miembrosDespues = (await LeerAsync(s.M1, s.M2, s.M3)).ToList();
        Assert.All(miembrosDespues, m =>
        {
            Assert.Null(m.IdFamilia);
            Assert.Equal(ahora, m.UpdatedAt);
        });
        Assert.Equal(miembrosAntes.Select(Valores), miembrosDespues.Select(Valores));

        var preciosDespues = new List<object>();
        foreach (var miembro in new[] { s.M1, s.M2, s.M3 })
        {
            preciosDespues.Add(await PreciosAsync(miembro, e.IdListaGeneral));
            preciosDespues.Add(await PreciosAsync(miembro, e.IdListaMayorista));
        }

        Assert.Equal(preciosAntes, preciosDespues);

        Assert.Equal(intactosAntes, (await LeerAsync(s.DeBaja, s.Otro)).Select(Huella).ToList());
        Assert.Equal(s.Familia, (await apoyo.LeerAsync(s.DeBaja)).IdFamilia);
        Assert.Equal(otraFamiliaAntes, Huella(await apoyo.LeerFamiliaAsync(s.Otra)));

        var listado = await e.Admin.GetFromJsonAsync<List<FamiliaListado>>("/api/familias", OpcionesJson);
        Assert.Equal([s.Otra], listado!.Select(f => f.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await e.Admin.GetAsync($"/api/familias/{s.Familia}")).StatusCode);

        // El nombre de la familia disuelta se puede reutilizar.
        Assert.Equal(
            HttpStatusCode.OK,
            (await e.Admin.PutAsJsonAsync($"/api/familias/{s.Otra}", new EdicionFamilia("Gaseosas", true), OpcionesJson)).StatusCode);

        // Artículos sueltos: un precio sin alcance es válido y solo escribe al artículo pedido.
        Assert.Equal(HttpStatusCode.Created, (await PostPrecioDeFamiliaAsync(e, s.M1, 95m, alcance: null)).StatusCode);
        Assert.Equal(95m, (await apoyo.FilasDePrecioAsync(s.M1, e.IdListaMayorista)).Single(p => p.VigenteHasta is null).Monto);
        Assert.Equal(80m, (await apoyo.FilasDePrecioAsync(s.M2, e.IdListaMayorista)).Single(p => p.VigenteHasta is null).Monto);
    }

    /// <summary>Una familia sin miembros vivos o inactiva también se disuelve: se da de baja y no se toca ningún
    /// artículo (el dado de baja que le queda conserva su <c>id_familia</c>).</summary>
    [Fact]
    public async Task UnaFamiliaSinMiembrosVivosOInactivaSeDisuelveIgual()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaSinMiembrosVivosOInactivaSeDisuelveIgual));
        var vacia = await apoyo.SembrarFamiliaAsync(e, "Vacía");
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), vacia, dadoDeBaja: true);
        var inactiva = await apoyo.SembrarFamiliaAsync(e, "Inactiva", activa: false);
        var miembro = await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), inactiva);
        var deBajaAntes = Huella(await apoyo.LeerAsync(deBaja));

        Assert.Equal(HttpStatusCode.NoContent, (await DisolverAsync(e.Admin, vacia)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DisolverAsync(e.Admin, inactiva)).StatusCode);

        Assert.NotNull((await apoyo.LeerFamiliaAsync(vacia)).DeletedAt);
        Assert.NotNull((await apoyo.LeerFamiliaAsync(inactiva)).DeletedAt);
        Assert.Equal(deBajaAntes, Huella(await apoyo.LeerAsync(deBaja)));
        Assert.Null((await apoyo.LeerAsync(miembro)).IdFamilia);
    }

    [Fact]
    public async Task UnaFamiliaInexistenteDadaDeBajaODeOtroTenantNoSeDisuelveYDa404()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDadaDeBajaODeOtroTenantNoSeDisuelveYDa404));
        using var otro = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDadaDeBajaODeOtroTenantNoSeDisuelveYDa404) + "-ajeno");
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        var familiaAjena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var miembroAjeno = await apoyo.SembrarArticuloAsync(otro, "ajeno", ValoresBase(otro), familiaAjena);
        var dadaDeBajaAntes = Huella(await apoyo.LeerFamiliaAsync(dadaDeBaja));
        var ajenaAntes = Huella(await apoyo.LeerFamiliaAsync(familiaAjena));
        var miembroAjenoAntes = Huella(await apoyo.LeerAsync(miembroAjeno));

        foreach (var id in new[] { 999_999_999, dadaDeBaja, familiaAjena })
        {
            var respuesta = await DisolverAsync(e.Admin, id);

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Equal(dadaDeBajaAntes, Huella(await apoyo.LeerFamiliaAsync(dadaDeBaja)));
        Assert.Equal(ajenaAntes, Huella(await apoyo.LeerFamiliaAsync(familiaAjena)));
        Assert.Equal(miembroAjenoAntes, Huella(await apoyo.LeerAsync(miembroAjeno)));
    }

    /// <summary>Disolver pide el lock de membresía EXCLUSIVO como primera sentencia: con un compartido ajeno
    /// sostenido queda esperando —observado pidiendo <c>ExclusiveLock</c>— sin ningún otro lock concedido y sin haber
    /// escrito. Cuando el otro lo libera, la familia se disuelve.</summary>
    [Fact]
    public async Task DisolverPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtro()
    {
        var s = await SembrarAsync(nameof(DisolverPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtro));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var disolucion = DisolverAsync(e.Admin, s.Familia);

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "Disolver nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(disolucion.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));
        Assert.Null((await apoyo.LeerFamiliaAsync(s.Familia)).DeletedAt);

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await disolucion.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.NotNull((await apoyo.LeerFamiliaAsync(s.Familia)).DeletedAt);
    }

    /// <summary>La fila de la familia se bloquea después de la membresía y ANTES de las filas de los miembros: una
    /// escritura de la familia en curso (sostiene el lock de fila de su <c>UPDATE</c>) hace esperar a la disolución
    /// con la membresía como único lock concedido, y todavía no bloqueó a ningún miembro: una tercera conexión toma
    /// sin esperar <c>FOR NO KEY UPDATE NOWAIT</c> de cada uno. Cuando la escritura comitea, la disolución sigue y la
    /// familia queda dada de baja con los miembros sueltos.</summary>
    [Fact]
    public async Task DisolverEsperaLaFilaDeLaFamiliaAntesDeBloquearALosMiembros()
    {
        var s = await SembrarAsync(nameof(DisolverEsperaLaFilaDeLaFamiliaAntesDeBloquearALosMiembros));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE familias SET activo = false WHERE id_familia = $1", s.Familia);

        var disolucion = DisolverAsync(e.Admin, s.Familia);

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "Disolver nunca se observó esperando la fila de la familia.");

        var concedidos = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        Assert.Equal((1, "ExclusiveLock"), (concedidos.ObjSubId, concedidos.Modo));

        foreach (var miembro in new[] { s.M1, s.M2, s.M3 })
        {
            await using var tercero = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
            await using var terceraTransaccion = await tercero.BeginTransactionAsync();
            await EjecutarAsync(
                tercero, terceraTransaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR NO KEY UPDATE NOWAIT", miembro);
        }

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await disolucion.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.NotNull((await apoyo.LeerFamiliaAsync(s.Familia)).DeletedAt);
        Assert.All(await LeerAsync(s.M1, s.M2, s.M3), m => Assert.Null(m.IdFamilia));
    }

    /// <summary>Las filas de los miembros se bloquean y la baja lógica se reevalúa bajo el lock de cada una: otra
    /// escritura sostiene la baja del miembro <c>m2</c> sin comitear; la disolución queda esperando esa fila y, al
    /// comitear la baja, <c>m2</c> ya no es un miembro vivo. Se dan por disueltos los otros dos y <c>m2</c>, dado de
    /// baja, conserva su <c>id_familia</c> y su <c>updated_at</c>: una escritura que alcanzara a todas las filas de la
    /// familia, y no solo a las bloqueadas, lo cambiaría.</summary>
    [Fact]
    public async Task UnMiembroQueLoDanDeBajaMientrasSeEsperaSuFilaNoSeDisuelveConLosDemas()
    {
        var s = await SembrarAsync(nameof(UnMiembroQueLoDanDeBajaMientrasSeEsperaSuFilaNoSeDisuelveConLosDemas));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET deleted_at = now() WHERE id_articulo = $1", s.M2);

        var disolucion = DisolverAsync(e.Admin, s.Familia);

        await EsperarAsync(() => EsperandoUnaFilaAsync(poll), "Disolver nunca se observó esperando la fila del miembro.");

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await disolucion.WaitAsync(EsperaMaxima)).StatusCode);

        Assert.Null((await apoyo.LeerAsync(s.M1)).IdFamilia);
        Assert.Null((await apoyo.LeerAsync(s.M3)).IdFamilia);

        var m2 = await apoyo.LeerAsync(s.M2);
        Assert.Equal(s.Familia, m2.IdFamilia);
        Assert.NotNull(m2.DeletedAt);
        Assert.NotEqual((await apoyo.LeerFamiliaAsync(s.Familia)).DeletedAt, m2.UpdatedAt);
    }

    /// <summary>Dos disoluciones simultáneas de la misma familia terminan una con 204 y la otra, que ya no encuentra la
    /// familia, con 404. La familia queda dada de baja.</summary>
    [Fact]
    public async Task DosDisolucionesSimultaneasDanUn204YUn404()
    {
        var s = await SembrarAsync(nameof(DosDisolucionesSimultaneasDanUn204YUn404));
        using var e = s.E;

        var respuestas = await Task.WhenAll(DisolverAsync(e.Admin, s.Familia), DisolverAsync(e.Admin, s.Familia)).WaitAsync(EsperaMaxima);

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NotFound], respuestas.Select(r => r.StatusCode).Order());
        Assert.NotNull((await apoyo.LeerFamiliaAsync(s.Familia)).DeletedAt);
    }

    // =================================================================================================
    // Sin reintento y todo o nada
    // =================================================================================================

    private (WaysDbContext Db, ServicioDeFamilias Servicio) CrearServicio(
        Entorno e, params IInterceptor[] interceptores)
    {
        var db = fixture.CrearContextoDeAplicacionConReintentos(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), interceptores);

        return (db, ServicioDe(db, e));
    }

    /// <summary>Sacar no se reintenta ante un fallo transitorio. La escritura es SQL crudo, que ningún interceptor de
    /// EF ve: el <c>40001</c> se inyecta en la lectura de la familia que corre DENTRO de la transacción, la única
    /// sentencia de EF del camino. El error llega tal cual y esa lectura se intentó UNA vez (con la estrategia
    /// reintentable la segunda vuelta comitearía y serían dos): el artículo sigue en su familia. Sin el interceptor la
    /// misma llamada sobre el MISMO contexto lo saca.</summary>
    [Fact]
    public async Task UnFalloTransitorioAlSacarNoSeReintentaYElArticuloSigueEnSuFamilia()
    {
        var s = await SembrarAsync(nameof(UnFalloTransitorioAlSacarNoSeReintentaYElArticuloSigueEnSuFamilia));
        using var e = s.E;
        var antes = Huella(await apoyo.LeerAsync(s.M2));

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("familias", "40001", ClaseDeSentencia.Select);
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.SacarArticuloAsync(s.Familia, s.M2));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, Huella(await apoyo.LeerAsync(s.M2)));

        await servicio.SacarArticuloAsync(s.Familia, s.M2);

        Assert.Null((await apoyo.LeerAsync(s.M2)).IdFamilia);
    }

    /// <summary>Disolver es todo o nada y no se reintenta. El <c>40001</c> se inyecta en el <c>UPDATE familias</c> que
    /// da de baja la familia, DESPUÉS de que el <c>UPDATE</c> crudo ya dejó sueltos a los miembros dentro de la
    /// transacción: al fallar, la transacción entera se revierte y los miembros siguen en su familia y la familia
    /// viva, el <c>UPDATE</c> se intentó UNA vez, y la familia que la operación leyó no queda rastreada en el contexto.
    /// Sin el interceptor la misma llamada sobre el MISMO contexto disuelve.</summary>
    [Fact]
    public async Task UnFalloTransitorioAlDisolverRevierteTodoNoSeReintentaYNoDejaLaFamiliaRastreada()
    {
        var s = await SembrarAsync(nameof(UnFalloTransitorioAlDisolverRevierteTodoNoSeReintentaYNoDejaLaFamiliaRastreada));
        using var e = s.E;
        var familiaAntes = Huella(await apoyo.LeerFamiliaAsync(s.Familia));
        var miembrosAntes = (await LeerAsync(s.M1, s.M2, s.M3)).Select(Huella).ToList();

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("familias", "40001", ClaseDeSentencia.Update);
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.DisolverAsync(s.Familia));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(familiaAntes, Huella(await apoyo.LeerFamiliaAsync(s.Familia)));
        Assert.Equal(miembrosAntes, (await LeerAsync(s.M1, s.M2, s.M3)).Select(Huella).ToList());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entrada => entrada.Entity is Familia);

        await servicio.DisolverAsync(s.Familia);

        Assert.NotNull((await apoyo.LeerFamiliaAsync(s.Familia)).DeletedAt);
        Assert.All(await LeerAsync(s.M1, s.M2, s.M3), m => Assert.Null(m.IdFamilia));
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
}
