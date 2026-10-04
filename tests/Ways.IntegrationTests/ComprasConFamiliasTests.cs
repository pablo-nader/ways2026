using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Articulos;
using Ways.Application.Compras;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// Compras frente a las familias de artículos (doc 10 §3) contra Postgres real: confirmar una compra replica el
/// costo (<c>costo_nominal</c>) de las líneas que lo actualizan a TODOS los miembros vivos de la familia de cada
/// una —de las líneas de una misma familia gana la de mayor <c>orden</c>— y aplicar el precio sugerido aplica,
/// por familia, solo el de la línea de mayor <c>orden</c>. La confirmación toma el lock de membresía compartido
/// como primera sentencia y bloquea las filas de los artículos a los que escribe el costo en orden ascendente.
///
/// <para>Compra con factura A (<c>C-FA</c>, discrimina IVA 21%) de 10 unidades por línea: el costo efectivo de
/// una línea de costo unitario <c>c</c> es <c>c × 1,21</c>, y el precio sugerido —con el margen del proveedor
/// habitual en 50%— es <c>c × 1,21 × 1,5</c>.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasConFamiliasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static int contadorDeFacturas;

    private sealed record Contexto(Entorno E, int IdTipoCFA, int IdAlicuotaIva21) : IDisposable
    {
        public void Dispose() => E.Dispose();
    }

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var e = await apoyo.PrepararAsync(nombre);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idAlicuota21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        (await db.Proveedores.IgnoreQueryFilters().SingleAsync(p => p.Id == e.Proveedores[0])).Margen = 50m;
        await db.SaveChangesAsync();

        return new Contexto(e, idTipo, idAlicuota21);
    }

    /// <summary>El costo efectivo de una línea de 10 unidades en una factura A: el costo unitario más el IVA.</summary>
    private static decimal Costo(decimal costoUnitario) => costoUnitario * 1.21m;

    private static decimal Sugerido(decimal costoUnitario) => Math.Round(Costo(costoUnitario) * 1.5m, 2, MidpointRounding.AwayFromZero);

    private static LineaDeCompraSolicitada Linea(
        Contexto c, int idArticulo, decimal costoUnitario, bool? actualizaCosto = null) =>
        new(idArticulo, "Línea de prueba", 10m, null, null, costoUnitario, 0m, c.IdAlicuotaIva21, actualizaCosto);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto c, params LineaDeCompraSolicitada[] lineas)
    {
        var numero = $"0001-{Interlocked.Increment(ref contadorDeFacturas):D8}";
        var solicitud = new SolicitudDeCompra(
            c.E.Proveedores[0], c.IdTipoCFA, c.E.IdPuntoVenta, numero, DateOnly.FromDateTime(DateTime.UtcNow), null, lineas);

        var respuesta = await c.E.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return System.Text.Json.JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> ConfirmarAsync(Contexto c, int idCompra) =>
        c.E.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null);

    private static async Task<CompraDetalle> ConfirmarYLeerAsync(Contexto c, int idCompra)
    {
        var respuesta = await ConfirmarAsync(c, idCompra);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return System.Text.Json.JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static object Huella(Articulo a) => new
    {
        a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.DisponibleParaTodas, a.IdFamilia,
        Compartidos = ValoresCompartidosDeFamilia.De(a), a.UpdatedAt, a.DeletedAt
    };

    // =================================================================================================
    // Siembra: una familia de tres miembros, uno dado de baja, dos sueltos y otra familia
    // =================================================================================================

    private sealed record Familiares(int Familia, int M1, int M2, int M3, int DeBaja, int Suelto, int OtroSuelto, int OtraFamilia, int G1, int G2);

    private async Task<Familiares> SembrarAsync(Entorno e)
    {
        var @base = ValoresBase(e);

        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", @base, familia, idMarca: e.Marcas[0], descripcion: "primero");
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", @base, familia, descripcion: "segundo");
        var m3 = await apoyo.SembrarArticuloAsync(e, "m3", @base, familia, activo: false);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", @base, familia, dadoDeBaja: true);
        var suelto = await apoyo.SembrarArticuloAsync(e, "suelto", @base);
        var otroSuelto = await apoyo.SembrarArticuloAsync(e, "otro-suelto", @base);
        var otraFamilia = await apoyo.SembrarFamiliaAsync(e, "Otra familia");
        var g1 = await apoyo.SembrarArticuloAsync(e, "g1", @base, otraFamilia);
        var g2 = await apoyo.SembrarArticuloAsync(e, "g2", @base, otraFamilia);

        return new Familiares(familia, m1, m2, m3, deBaja, suelto, otroSuelto, otraFamilia, g1, g2);
    }

    private async Task<decimal?> CostoNominalAsync(int idArticulo) => (await apoyo.LeerAsync(idArticulo)).CostoNominal;

    // =================================================================================================
    // Costo: una línea de la familia llega a todos sus miembros vivos
    // =================================================================================================

    /// <summary>De las líneas de una familia gana la de mayor orden —aunque tenga el costo menor— y su costo es el
    /// de TODOS los miembros vivos, también de los que no tienen línea. El artículo suelto conserva el dedupe por
    /// artículo (gana su línea de mayor orden). El miembro dado de baja, el otro suelto y la otra familia no
    /// cambian, ni su <c>updated_at</c>.</summary>
    [Fact]
    public async Task ConfirmarReplicaElCostoDeLaLineaDeMayorOrdenDeLaFamiliaATodosSusMiembrosVivos()
    {
        using var c = await PrepararAsync(nameof(ConfirmarReplicaElCostoDeLaLineaDeMayorOrdenDeLaFamiliaATodosSusMiembrosVivos));
        var f = await SembrarAsync(c.E);
        var antes = new Dictionary<int, Articulo>();
        foreach (var id in new[] { f.M1, f.M2, f.M3, f.DeBaja, f.Suelto, f.OtroSuelto, f.G1, f.G2 })
        {
            antes[id] = await apoyo.LeerAsync(id);
        }

        var borrador = await CrearBorradorAsync(
            c, Linea(c, f.M1, 300m), Linea(c, f.M2, 200m), Linea(c, f.Suelto, 50m), Linea(c, f.Suelto, 70m));

        await ConfirmarYLeerAsync(c, borrador.Id);

        // La familia: el costo de la línea 2 (orden más alto de la familia), 242, en los tres vivos.
        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            var miembro = await apoyo.LeerAsync(id);

            Assert.Equal(Costo(200m), miembro.CostoNominal);
            Assert.True(miembro.UpdatedAt > antes[id].UpdatedAt);

            // Solo cambió el costo: los demás campos compartidos y los propios siguen.
            Assert.Equal(
                ValoresCompartidosDeFamilia.De(antes[id]) with { CostoNominal = Costo(200m) },
                ValoresCompartidosDeFamilia.De(miembro));
            Assert.Equal(antes[id].Nombre, miembro.Nombre);
            Assert.Equal(antes[id].Descripcion, miembro.Descripcion);
            Assert.Equal(antes[id].IdMarca, miembro.IdMarca);
            Assert.Equal(antes[id].Activo, miembro.Activo);
            Assert.Equal(f.Familia, miembro.IdFamilia);
        }

        // El suelto: su línea de mayor orden (la 4, 70 × 1,21).
        Assert.Equal(Costo(70m), await CostoNominalAsync(f.Suelto));

        // El resto no cambia.
        foreach (var id in new[] { f.DeBaja, f.OtroSuelto, f.G1, f.G2 })
        {
            Assert.Equal(Huella(antes[id]), Huella(await apoyo.LeerAsync(id)));
        }
    }

    /// <summary>Una sola línea de la familia alcanza: el costo llega también a los miembros que no figuran en la
    /// compra.</summary>
    [Fact]
    public async Task UnaSolaLineaDeLaFamiliaDejaElMismoCostoEnTodosSusMiembros()
    {
        using var c = await PrepararAsync(nameof(UnaSolaLineaDeLaFamiliaDejaElMismoCostoEnTodosSusMiembros));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 100m));
        await ConfirmarYLeerAsync(c, borrador.Id);

        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            Assert.Equal(Costo(100m), await CostoNominalAsync(id));
        }

        Assert.Equal(40m, await CostoNominalAsync(f.DeBaja));
        Assert.Equal(40m, await CostoNominalAsync(f.G1));
    }

    /// <summary>Las líneas que no actualizan el costo, o con costo unitario cero (el guard anti-bonificación),
    /// se descartan ANTES de elegir la ganadora de la familia: una línea de mayor orden que no cuenta no le gana
    /// a una de menor orden que sí.</summary>
    [Fact]
    public async Task LasLineasQueNoActualizanElCostoNoLeGananALaDeMenorOrdenQueSiLoActualiza()
    {
        using var c = await PrepararAsync(nameof(LasLineasQueNoActualizanElCostoNoLeGananALaDeMenorOrdenQueSiLoActualiza));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(
            c, Linea(c, f.M1, 100m), Linea(c, f.M2, 500m, actualizaCosto: false), Linea(c, f.M3, 0m));

        await ConfirmarYLeerAsync(c, borrador.Id);

        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            Assert.Equal(Costo(100m), await CostoNominalAsync(id));
        }
    }

    [Fact]
    public async Task UnaCompraSinLineasQueActualicenElCostoNoTocaLaFamilia()
    {
        using var c = await PrepararAsync(nameof(UnaCompraSinLineasQueActualicenElCostoNoTocaLaFamilia));
        var f = await SembrarAsync(c.E);
        var huellas = new List<object>();
        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            huellas.Add(Huella(await apoyo.LeerAsync(id)));
        }

        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 100m, actualizaCosto: false));
        await ConfirmarYLeerAsync(c, borrador.Id);

        var despues = new List<object>();
        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            despues.Add(Huella(await apoyo.LeerAsync(id)));
        }

        Assert.Equal(huellas, despues);
    }

    /// <summary>Dos familias y un suelto en la misma compra: cada uno resuelve por su cuenta, y sus miembros se
    /// escriben todos.</summary>
    [Fact]
    public async Task DosFamiliasYUnSueltoEnLaMismaCompraResuelvenCadaUnoPorSuCuenta()
    {
        using var c = await PrepararAsync(nameof(DosFamiliasYUnSueltoEnLaMismaCompraResuelvenCadaUnoPorSuCuenta));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(
            c, Linea(c, f.M3, 100m), Linea(c, f.G1, 300m), Linea(c, f.Suelto, 50m), Linea(c, f.G2, 200m), Linea(c, f.M1, 400m));

        await ConfirmarYLeerAsync(c, borrador.Id);

        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            Assert.Equal(Costo(400m), await CostoNominalAsync(id));
        }

        foreach (var id in new[] { f.G1, f.G2 })
        {
            Assert.Equal(Costo(200m), await CostoNominalAsync(id));
        }

        Assert.Equal(Costo(50m), await CostoNominalAsync(f.Suelto));
        Assert.Equal(40m, await CostoNominalAsync(f.OtroSuelto));
    }

    /// <summary>Anular una compra nunca revierte el costo, tampoco el de una familia.</summary>
    [Fact]
    public async Task AnularLaCompraNoRevierteElCostoDeLaFamilia()
    {
        using var c = await PrepararAsync(nameof(AnularLaCompraNoRevierteElCostoDeLaFamilia));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 200m));
        await ConfirmarYLeerAsync(c, borrador.Id);

        var anulacion = await c.E.Admin.PostAsync($"/api/compras/{borrador.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            Assert.Equal(Costo(200m), await CostoNominalAsync(id));
        }
    }

    /// <summary>Un artículo que se dio de baja después de armarse el borrador ya no es miembro de su familia —el
    /// artículo dado de baja conserva su <c>id_familia</c>, pero no cuenta—: su línea se escribe sola, como la de
    /// un artículo suelto, y su costo no llega a los miembros vivos. La línea de un miembro vivo de la misma
    /// familia sí llega a todos los vivos, y no pisa el costo de la línea del dado de baja, aunque la fila de este
    /// apunte a esa misma familia.</summary>
    [Fact]
    public async Task UnArticuloDadoDeBajaDespuesDeArmarElBorradorEscribeSoloSuPropiaLineaYNoReplicaSuCosto()
    {
        using var c = await PrepararAsync(nameof(UnArticuloDadoDeBajaDespuesDeArmarElBorradorEscribeSoloSuPropiaLineaYNoReplicaSuCosto));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(c, Linea(c, f.M1, 100m), Linea(c, f.M3, 200m));

        var baja = await c.E.Admin.DeleteAsync($"/api/articulos/{f.M3}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        await ConfirmarYLeerAsync(c, borrador.Id);

        var dadoDeBaja = await apoyo.LeerAsync(f.M3);
        Assert.Equal(Costo(200m), dadoDeBaja.CostoNominal);
        Assert.Equal(f.Familia, dadoDeBaja.IdFamilia);

        Assert.Equal(Costo(100m), await CostoNominalAsync(f.M1));
        Assert.Equal(Costo(100m), await CostoNominalAsync(f.M2));
    }

    // =================================================================================================
    // Locks: membresía compartida primero, filas de los artículos ascendentes
    // =================================================================================================

    /// <summary>El lock de membresía es la PRIMERA sentencia de la transacción de la confirmación: con el
    /// exclusivo sostenido por otro, la confirmación espera pidiendo el modo compartido (<c>ShareLock</c>)
    /// SIN haber escrito nada —ni siquiera el encabezado, que le habría asignado una transacción— ni tomado
    /// ningún otro lock advisory. Al liberarlo termina y la familia queda con el costo.</summary>
    [Fact]
    public async Task ConfirmarEsperaElLockDeMembresiaExclusivoAjenoAntesDeEscribirNada()
    {
        using var c = await PrepararAsync(nameof(ConfirmarEsperaElLockDeMembresiaExclusivoAjenoAntesDeEscribirNada));
        var f = await SembrarAsync(c.E);
        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 100m));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(c.E.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(c.E.IdTenant));

        var confirmacion = ConfirmarAsync(c, borrador.Id);

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, c.E.IdTenant),
            "La confirmación nunca se observó esperando el lock de membresía.");

        Assert.Equal("ShareLock", esperando.Modo);
        Assert.False(confirmacion.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await confirmacion.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Equal(Costo(100m), await CostoNominalAsync(f.M1));
    }

    /// <summary>La confirmación no cambia la pertenencia: el lock que toma es COMPARTIDO y convive con el de
    /// otra escritura de la familia. Con el compartido sostenido por otro termina sin esperarlo; con el
    /// exclusivo se quedaría esperando.</summary>
    [Fact]
    public async Task ConfirmarNoEsperaAUnLockDeMembresiaCompartidoAjeno()
    {
        using var c = await PrepararAsync(nameof(ConfirmarNoEsperaAUnLockDeMembresiaCompartidoAjeno));
        var f = await SembrarAsync(c.E);
        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 100m));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(c.E.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(
            sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(c.E.IdTenant));

        var respuesta = await ConfirmarAsync(c, borrador.Id).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>Las filas de los artículos a los que se escribe el costo se bloquean en orden ASCENDENTE de id: un
    /// tercero sostiene <c>FOR UPDATE</c> sobre el miembro de id más ALTO y la confirmación espera esa fila
    /// teniendo ya bloqueado el de id más bajo (un <c>FOR NO KEY UPDATE NOWAIT</c> desde otra conexión falla con
    /// <c>55P03</c>). Con el orden invertido esperaría el más alto sin tener ninguno.</summary>
    [Fact]
    public async Task LasFilasDeLosMiembrosSeBloqueanEnOrdenAscendenteAlEscribirElCosto()
    {
        using var c = await PrepararAsync(nameof(LasFilasDeLosMiembrosSeBloqueanEnOrdenAscendenteAlEscribirElCosto));
        var f = await SembrarAsync(c.E);
        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 100m));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(c.E.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", f.M3);

        var confirmacion = ConfirmarAsync(c, borrador.Id);

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La confirmación nunca se observó esperando la fila del miembro de id más alto.");

        await using (var comando = new NpgsqlCommand(
            "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR NO KEY UPDATE NOWAIT", poll))
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = f.M1 });

            var error = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteScalarAsync());
            Assert.Equal("55P03", error.SqlState);
        }

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.OK, (await confirmacion.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.Equal(Costo(100m), await CostoNominalAsync(f.M3));
    }

    /// <summary>Las filas se bloquean <c>FOR NO KEY UPDATE</c> y no <c>FOR UPDATE</c>: el segundo chocaría con el
    /// <c>FOR KEY SHARE</c> que una venta toma sobre el artículo por las FK de sus renglones. Un tercero
    /// sostiene a mano un <c>FOR KEY SHARE</c> sobre un miembro y la confirmación tiene que terminar sin
    /// esperarlo.</summary>
    [Fact]
    public async Task UnForKeyShareAjenoSobreUnMiembroNoHaceEsperarALaConfirmacion()
    {
        using var c = await PrepararAsync(nameof(UnForKeyShareAjenoSobreUnMiembroNoHaceEsperarALaConfirmacion));
        var f = await SembrarAsync(c.E);
        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 100m));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(c.E.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR KEY SHARE", f.M3);

        var respuesta = await ConfirmarAsync(c, borrador.Id).WaitAsync(EsperaMaxima);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>La razón del lock compartido de la confirmación: una confirmación en curso —ya escribió el costo de
    /// la familia y está por comitear— hace esperar a un alta dentro de esa familia, que pide el lock exclusivo.
    /// Cuando la confirmación comitea, el alta lee la referencia con el costo NUEVO y entra con él. Sin el lock
    /// de la confirmación el alta no esperaría, leería el costo viejo y la familia quedaría con un miembro de
    /// costo distinto; el pedido lleva el costo nuevo, así que sin la espera el alta ni siquiera entraría
    /// (<c>familia_valores_distintos</c>).</summary>
    [Fact]
    public async Task UnaConfirmacionEnCursoHaceEsperarAlAltaEnLaFamiliaYElAltaEntraConElCostoNuevo()
    {
        using var c = await PrepararAsync(nameof(UnaConfirmacionEnCursoHaceEsperarAlAltaEnLaFamiliaYElAltaEntraConElCostoNuevo));
        var f = await SembrarAsync(c.E);
        var borrador = await CrearBorradorAsync(c, Linea(c, f.M2, 200m));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(c.E.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        var alPuntoDeComitear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeComitear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage> confirmacion;
        Task<HttpResponseMessage> alta;

        using (fixture.ConInterceptorEnElHost(new InterceptorDePausaAntesDelPrimerCommit(alPuntoDeComitear, puedeComitear)))
        {
            confirmacion = ConfirmarAsync(c, borrador.Id);
            await alPuntoDeComitear.Task.WaitAsync(EsperaMaxima);

            // La confirmación escribió el costo y sostiene sus locks. El alta lleva el costo nuevo en su pedido.
            var pedido = new AltaArticulo(
                CodigoInterno: null, Nombre: "nuevo miembro", Descripcion: null, IdArea: ValoresBase(c.E).IdArea,
                IdCategoria: ValoresBase(c.E).IdCategoria, IdMarca: null, IdGrupo: ValoresBase(c.E).IdGrupo,
                IdProveedorHabitual: ValoresBase(c.E).IdProveedorHabitual, IdAlicuotaIva: ValoresBase(c.E).IdAlicuotaIva,
                UnidadVenta: ValoresBase(c.E).UnidadVenta, UnidadesPorBulto: ValoresBase(c.E).UnidadesPorBulto,
                EsProducto: ValoresBase(c.E).EsProducto, CostoLista: ValoresBase(c.E).CostoLista,
                DescuentoProveedor: ValoresBase(c.E).DescuentoProveedor, CostoNominal: Costo(200m),
                ControlaLote: ValoresBase(c.E).ControlaLote, AcumulaEnVenta: ValoresBase(c.E).AcumulaEnVenta,
                IdFamilia: f.Familia);
            alta = c.E.Admin.PostAsJsonAsync("/api/articulos", pedido, OpcionesJson);

            var esperando = await EsperarAsync(
                () => EsperandoLaMembresiaAsync(poll, c.E.IdTenant),
                "El alta nunca se observó esperando el lock de membresía mientras la confirmación estaba en curso.");
            Assert.Equal("ExclusiveLock", esperando.Modo);

            puedeComitear.SetResult();
        }

        Assert.Equal(HttpStatusCode.OK, (await confirmacion.WaitAsync(EsperaMaxima)).StatusCode);

        var respuesta = await alta.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

        Assert.Equal(Costo(200m), await CostoNominalAsync(creado.Id));
        foreach (var id in new[] { f.M1, f.M2, f.M3, creado.Id })
        {
            Assert.Equal(Costo(200m), await CostoNominalAsync(id));
        }
    }

    // =================================================================================================
    // Precio sugerido: por familia solo se aplica la línea de mayor orden
    // =================================================================================================

    private static Task<HttpResponseMessage> AplicarPreciosAsync(Contexto c, int idCompra, int idLista, bool confirmarReemplazo = false) =>
        c.E.Admin.PostAsJsonAsync(
            $"/api/compras/{idCompra}/precios", new SolicitudDeAplicarPrecios(idLista, confirmarReemplazo));

    private static async Task<List<ResultadoAplicarPrecio>> ResultadosAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return System.Text.Json.JsonSerializer.Deserialize<List<ResultadoAplicarPrecio>>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Tres líneas de la familia y una de un artículo suelto: solo se aplica la de mayor orden de la
    /// familia, a TODOS sus miembros, con su precio sugerido; las otras dos se informan como no aplicadas,
    /// nombrando la línea que las supera; la del suelto se aplica. Cada artículo escrito recibe una auditoría
    /// <c>precio.cambio</c>.</summary>
    [Fact]
    public async Task AplicarElPrecioSugeridoSoloAplicaLaLineaDeMayorOrdenDeCadaFamiliaATodosSusMiembros()
    {
        using var c = await PrepararAsync(nameof(AplicarElPrecioSugeridoSoloAplicaLaLineaDeMayorOrdenDeCadaFamiliaATodosSusMiembros));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(
            c, Linea(c, f.M1, 300m), Linea(c, f.M3, 100m), Linea(c, f.M2, 200m), Linea(c, f.Suelto, 100m));
        await ConfirmarYLeerAsync(c, borrador.Id);

        var resultados = await ResultadosAsync(await AplicarPreciosAsync(c, borrador.Id, c.E.IdListaGeneral));

        // Una entrada por línea, en orden de línea.
        Assert.Equal([f.M1, f.M3, f.M2, f.Suelto], resultados.Select(r => r.IdArticulo));

        // Las líneas 1 y 2 las supera la 3.
        foreach (var superada in new[] { resultados[0], resultados[1] })
        {
            Assert.False(superada.Aplicado);
            Assert.Null(superada.Precio);
            Assert.Contains("línea 3", superada.Error, StringComparison.Ordinal);
        }

        Assert.True(resultados[2].Aplicado);
        Assert.Equal(Sugerido(200m), resultados[2].Precio);
        Assert.Null(resultados[2].Error);

        Assert.True(resultados[3].Aplicado);
        Assert.Equal(Sugerido(100m), resultados[3].Precio);

        // Los tres miembros vivos quedan con el precio de la línea 3; el suelto, con el suyo.
        foreach (var id in new[] { f.M1, f.M2, f.M3 })
        {
            var abierta = (await apoyo.FilasDePrecioAsync(id, c.E.IdListaGeneral)).Single(p => p.VigenteHasta is null);
            Assert.Equal(Sugerido(200m), abierta.Monto);
        }

        Assert.Equal(Sugerido(100m), (await apoyo.FilasDePrecioAsync(f.Suelto, c.E.IdListaGeneral)).Single(p => p.VigenteHasta is null).Monto);
        Assert.Empty(await apoyo.FilasDePrecioAsync(f.DeBaja, c.E.IdListaGeneral));
        Assert.Empty(await apoyo.FilasDePrecioAsync(f.OtroSuelto, c.E.IdListaGeneral));

        var auditoria = await apoyo.AuditoriaDePreciosAsync(c.E.IdTenant);
        Assert.Equal([f.M1, f.M2, f.M3, f.Suelto], auditoria.Select(a => a.IdEntidad).Order());
    }

    /// <summary>Si la línea que define el precio de la familia se rechaza (un miembro tiene un precio pendiente
    /// sin confirmar el reemplazo), la familia no cambia: las líneas superadas tampoco se escriben, y lo dicen —
    /// están superadas por esa línea, cuyo resultado es el rechazo—. La línea del suelto se aplica igual:
    /// partial success.</summary>
    [Fact]
    public async Task SiLaLineaGanadoraDeLaFamiliaSeRechazaLaFamiliaNoCambiaYLasSuperadasNoSeEscriben()
    {
        using var c = await PrepararAsync(nameof(SiLaLineaGanadoraDeLaFamiliaSeRechazaLaFamiliaNoCambiaYLasSuperadasNoSeEscriben));
        var f = await SembrarAsync(c.E);
        await apoyo.SembrarPrecioPendienteAsync(c.E, f.M3, c.E.IdListaGeneral, montoVigente: 120m, montoPendiente: 130m);

        var borrador = await CrearBorradorAsync(
            c, Linea(c, f.M1, 300m), Linea(c, f.M2, 200m), Linea(c, f.Suelto, 100m));
        await ConfirmarYLeerAsync(c, borrador.Id);

        var resultados = await ResultadosAsync(await AplicarPreciosAsync(c, borrador.Id, c.E.IdListaGeneral));

        Assert.False(resultados[0].Aplicado);
        Assert.Contains("línea 2", resultados[0].Error, StringComparison.Ordinal);

        Assert.False(resultados[1].Aplicado);
        Assert.NotNull(resultados[1].Error);
        Assert.DoesNotContain("línea", resultados[1].Error!, StringComparison.Ordinal);

        Assert.True(resultados[2].Aplicado);

        Assert.Empty(await apoyo.FilasDePrecioAsync(f.M1, c.E.IdListaGeneral));
        Assert.Empty(await apoyo.FilasDePrecioAsync(f.M2, c.E.IdListaGeneral));
        Assert.Equal([120m, 130m], (await apoyo.FilasDePrecioAsync(f.M3, c.E.IdListaGeneral)).Select(p => p.Monto));
        Assert.Equal([f.Suelto], (await apoyo.AuditoriaDePreciosAsync(c.E.IdTenant)).Select(a => a.IdEntidad));
    }

    /// <summary>Un artículo suelto no se deduplica: dos líneas del mismo artículo se aplican las dos, en orden, y
    /// el último precio es el de la línea de mayor orden.</summary>
    [Fact]
    public async Task DosLineasDelMismoArticuloSueltoSeAplicanLasDosEnOrden()
    {
        using var c = await PrepararAsync(nameof(DosLineasDelMismoArticuloSueltoSeAplicanLasDosEnOrden));
        var f = await SembrarAsync(c.E);

        var borrador = await CrearBorradorAsync(c, Linea(c, f.Suelto, 100m), Linea(c, f.Suelto, 200m));
        await ConfirmarYLeerAsync(c, borrador.Id);

        var resultados = await ResultadosAsync(await AplicarPreciosAsync(c, borrador.Id, c.E.IdListaGeneral));

        Assert.All(resultados, r => Assert.True(r.Aplicado));
        Assert.Equal([Sugerido(100m), Sugerido(200m)], (await apoyo.FilasDePrecioAsync(f.Suelto, c.E.IdListaGeneral)).Select(p => p.Monto));
    }
}
