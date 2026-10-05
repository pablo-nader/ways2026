using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;

namespace Ways.Application.Familias;

/// <summary>
/// Agrupar artículos en una familia (doc 10 §3, "Familias de artículos"): previsualizar lo que cambiaría, crear una
/// familia con sus artículos y agregar artículos a una familia que ya existe. Autorización:
/// <c>Politicas.GestionDeCatalogo</c> aplicada en la capa de API, la misma puerta que el alta y la edición de
/// artículos.
///
/// <para>Agrupar ALINEA: los artículos que entran quedan idénticos al de referencia en los trece campos compartidos
/// (<see cref="ValoresCompartidosDeFamilia"/>) y en el estado de precios de cada lista fija
/// (<see cref="EstadoDePrecios"/>, reglas en <see cref="ReglaDeAlineacionDePrecios"/>); los campos propios no se tocan.
/// Es todo o nada: un artículo inexistente o en otra familia, un catálogo del artículo de referencia que no existe, un
/// pedido de más pares artículo-lista de los que admite <see cref="ReglaDeAgrupacion.MaximoDeParesPorPedido"/> o un
/// precio que no se puede alinear rechaza el pedido entero y no se escribe nada. La previsualización es solo lectura,
/// sin locks y sin transacción: una foto.</para>
///
/// <para>Los pedidos que escriben cambian la pertenencia, así que siguen el protocolo de locks de las familias
/// (<see cref="LockDeMembresiaDeFamilias"/>): el lock de membresía EXCLUSIVO como primera sentencia; la fila de la
/// familia, si ya existe; las filas de los artículos ascendentes; los chequeos de catálogo de la referencia (el área, la
/// categoría, el grupo y el proveedor habitual <c>FOR KEY SHARE</c>, después de las filas, como en la edición de
/// artículos); y los locks de par artículo-lista por clave ascendente. "Ahora" se lee una sola vez, después de todos los
/// locks.</para>
/// </summary>
public class ServicioDeAgrupacionDeFamilias(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDePrecios servicioDePrecios,
    ServicioDeLotes servicioDeLotes, GuardaDeReferencias guarda)
{
    /// <summary>
    /// Qué cambiaría si se agruparan los artículos pedidos con la referencia (<see cref="SolicitudDePrevisualizacion"/>):
    /// por cada artículo que se puede alinear, las columnas compartidas que cambian (actual → nuevo) y el cambio de precios
    /// de cada lista fija; y todos los problemas que impedirían agrupar (<see cref="ProblemaDeAgrupacion"/>), con el
    /// código y el mensaje del error que el pedido real daría. Si la referencia no existe no hay nada contra qué
    /// comparar y la respuesta trae solo los artículos inexistentes.
    ///
    /// <para>Solo lee: no abre transacción, no toma ningún lock y no rastrea ninguna entidad. "Ahora" se lee una sola
    /// vez, para clasificar los precios de todos los artículos igual.</para>
    /// </summary>
    public async Task<PrevisualizacionDeAgrupacion> PrevisualizarAsync(
        SolicitudDePrevisualizacion datos, CancellationToken ct = default)
    {
        ReglaDeAgrupacion.ExigirReferencia(datos.IdArticuloReferencia);
        var destinosPedidos = ReglaDeAgrupacion.Destinos(datos.IdsArticulos, datos.IdArticuloReferencia);
        var idTenant = ExigirTenantDeLaSesion();
        var ahora = reloj.Ahora;

        var idsPedidos = new List<int>(destinosPedidos.Count + 1) { datos.IdArticuloReferencia };
        idsPedidos.AddRange(destinosPedidos);
        var idsAConsultar = idsPedidos.ToArray();

        var articulos = (await db.Articulos.AsNoTracking().Where(a => idsAConsultar.Contains(a.Id)).ToListAsync(ct))
            .ToDictionary(a => a.Id);

        var inexistentes = idsPedidos
            .Order()
            .Where(id => !articulos.ContainsKey(id))
            .Select(id => new ProblemaDeAgrupacion("referencia_invalida", MensajeDeArticuloInexistente(id), id, null))
            .ToList();

        if (!articulos.TryGetValue(datos.IdArticuloReferencia, out var referencia))
        {
            return new PrevisualizacionDeAgrupacion(datos.IdArticuloReferencia, IdFamilia: null, [], inexistentes);
        }

        var problemas = new List<ProblemaDeAgrupacion>();

        if (referencia.IdFamilia is { } idFamilia)
        {
            var familia = await db.Familias
                .AsNoTracking()
                .Where(f => f.Id == idFamilia)
                .Select(f => new { f.Nombre, f.Activo })
                .FirstOrDefaultAsync(ct);

            if (familia is null)
            {
                problemas.Add(new ProblemaDeAgrupacion("no_encontrado", MensajeDeFamiliaInexistente(idFamilia), null, null));
            }
            else if (ReglaDeFamilias.ResolverAgregado(familia.Activo, tieneMiembrosVivos: true) == ResolucionDeIngresoAFamilia.FamiliaInactiva)
            {
                problemas.Add(new ProblemaDeAgrupacion(
                    "familia_inactiva", MensajeDeFamiliaInactiva(familia.Nombre), null, null));
            }
        }

        problemas.AddRange(inexistentes);

        var enOtraFamilia = destinosPedidos
            .Where(id => articulos.TryGetValue(id, out var a) && a.IdFamilia is not null && a.IdFamilia != referencia.IdFamilia)
            .ToList();
        var nombresDeFamilias = await NombresDeFamiliasAsync(
            [.. enOtraFamilia.Select(id => articulos[id].IdFamilia!.Value)], ct);

        problemas.AddRange(enOtraFamilia.Select(id => new ProblemaDeAgrupacion(
            "articulo_en_otra_familia",
            MensajeDeArticuloEnOtraFamilia(articulos[id], nombresDeFamilias),
            id,
            null)));

        var alineables = destinosPedidos
            .Where(id => articulos.TryGetValue(id, out var a) && (a.IdFamilia is null || a.IdFamilia == referencia.IdFamilia))
            .ToList();

        var valoresDeLaReferencia = ValoresCompartidosDeFamilia.De(referencia);
        var visibles = await CatalogosVisibles.LeerAsync(
            db, [valoresDeLaReferencia, .. alineables.Select(id => ValoresCompartidosDeFamilia.De(articulos[id]))], ct);

        problemas.AddRange(await CatalogosInexistentesDeLaReferenciaAsync(referencia, visibles, ct));

        var listas = await servicioDePrecios.ListasFijasAsync(ct);

        if (ReglaDeAgrupacion.MensajeSiExcedeLosPares(destinosPedidos.Count, listas.Count) is { } mensajeDePares)
        {
            problemas.Add(new ProblemaDeAgrupacion("demasiados_articulos", mensajeDePares, null, null));
        }

        var plan = await servicioDePrecios.PlanificarAlineacionAsync(
            referencia.Id, alineables, [.. listas.Select(l => l.Id)], idTenant, ahora, ct);

        problemas.AddRange(plan.Pares.Where(par => par.EsInalineable).Select(par => new ProblemaDeAgrupacion(
            "familia_precio_inalineable",
            MensajeDePrecioInalineable(articulos[par.IdArticulo], listas.Single(l => l.Id == par.IdListaPrecio), par),
            par.IdArticulo,
            par.IdListaPrecio)));

        var cambios = alineables
            .Select(id =>
            {
                var actuales = ValoresCompartidosDeFamilia.De(articulos[id]);

                return CambiosDe(
                    articulos[id], actuales, valoresDeLaReferencia, valoresDeLaReferencia.CamposDistintos(actuales), plan,
                    visibles);
            })
            .ToList();

        return new PrevisualizacionDeAgrupacion(referencia.Id, referencia.IdFamilia, cambios, problemas);
    }

    /// <summary>
    /// Crea la familia <see cref="AltaDeFamilia.Nombre"/> y la agrupa: el artículo de referencia y los pedidos quedan como
    /// sus miembros, y los pedidos, alineados con la referencia (<see cref="ResultadoDeAgrupacion"/>). Rechaza, sin
    /// escribir nada: <c>400</c> por el nombre, la referencia o el tope de artículos; <c>409 familia_nombre_duplicado</c>;
    /// <c>400 referencia_invalida</c> por un artículo que no existe o está dado de baja; <c>409
    /// articulo_en_otra_familia</c> por uno que ya es miembro de una familia —la referencia incluida: agrupar no mueve a
    /// nadie—; <c>400 referencia_invalida</c> por un catálogo del artículo de referencia (el área, la categoría, el
    /// grupo, el proveedor habitual o la alícuota de IVA) que no existe o está dado de baja, con el código y el mensaje
    /// de la edición de artículos; <c>400 demasiados_articulos</c> por más pares artículo-lista fija que
    /// <see cref="ReglaDeAgrupacion.MaximoDeParesPorPedido"/>; y <c>422 familia_precio_inalineable</c> por un par que no se
    /// puede alinear.
    ///
    /// <para>El chequeo del nombre es best-effort y corre antes de abrir la transacción: dos pedidos al mismo nombre pasan
    /// los dos, se serializan en el lock de membresía y el segundo recibe, al insertar, el <c>409</c> del respaldo
    /// <c>ux_familias_nombre</c>. La familia se inserta DESPUÉS de leer "ahora" y de tomar todos los locks, en la fase de
    /// escrituras: una fila nueva no la puede bloquear nadie, y así su <c>created_at</c> es el mismo "ahora" que el de
    /// todo lo demás. El detalle del orden y de los locks está en <see cref="AgruparAsync"/>.</para>
    /// </summary>
    public async Task<ResultadoDeAgrupacion> CrearAsync(AltaDeFamilia datos, CancellationToken ct = default)
    {
        var nombre = NombreDeFamilia.Normalizar(datos.Nombre);
        ReglaDeAgrupacion.ExigirReferencia(datos.IdArticuloReferencia);
        var destinos = ReglaDeAgrupacion.Destinos(datos.IdsArticulos, datos.IdArticuloReferencia);

        await NombreDeFamilia.ExigirDisponibleAsync(db, nombre, excluirId: null, ct);

        return await AgruparAsync(
            new Pedido(IdFamiliaExistente: null, NombreNuevo: nombre, datos.IdArticuloReferencia, destinos), ct);
    }

    /// <summary>
    /// Suma artículos a una familia que ya existe, alineados con su artículo de referencia —el miembro vivo de menor
    /// id— (<see cref="ResultadoDeAgrupacion"/>). Rechaza, sin escribir nada: <c>400 articulos_requeridos</c> si la lista
    /// está vacía; <c>400 demasiados_articulos</c> si trae más ids que el tope más uno, antes de buscar la familia;
    /// <c>404</c> si la familia no existe o está dada de baja; <c>409 familia_inactiva</c>; <c>409
    /// familia_sin_articulos</c> si no tiene ningún miembro vivo, y por lo tanto referencia; y, para los artículos, lo
    /// mismo que <see cref="CrearAsync"/>: <c>400 referencia_invalida</c>, <c>409 articulo_en_otra_familia</c>, <c>400
    /// demasiados_articulos</c> por los pares y <c>422 familia_precio_inalineable</c>. Un artículo que ya es miembro de
    /// esta familia no es un rechazo: se alinea igual. La referencia de la familia puede figurar en la lista y no cuenta
    /// para el tope: el tope exacto, sobre los destinos que quedan sin ella, se exige bajo el lock, cuando se la conoce.
    /// El detalle del orden y de los locks está en <see cref="AgruparAsync"/>.
    /// </summary>
    public async Task<ResultadoDeAgrupacion> AgregarArticulosAsync(
        int idFamilia, AgregadoDeArticulos datos, CancellationToken ct = default)
    {
        var destinos = ReglaDeAgrupacion.DestinosAlAgregar(datos.IdsArticulos);

        if (destinos.Count == 0)
        {
            throw new ErrorDominio("articulos_requeridos", "El campo idsArticulos tiene que traer al menos un artículo.", 400);
        }

        return await AgruparAsync(
            new Pedido(IdFamiliaExistente: idFamilia, NombreNuevo: null, IdArticuloReferencia: null, destinos), ct);
    }

    /// <summary>Lo que un pedido de agrupar pide, ya normalizado. Con <paramref name="IdFamiliaExistente"/> es el agregado a
    /// esa familia, cuya referencia sale de la base; sin él es el alta de <paramref name="NombreNuevo"/> con
    /// <paramref name="IdArticuloReferencia"/> como modelo. <paramref name="Destinos"/> son los artículos a alinear,
    /// ascendentes y sin repetir.</summary>
    private sealed record Pedido(
        int? IdFamiliaExistente, string? NombreNuevo, int? IdArticuloReferencia, IReadOnlyList<int> Destinos);

    /// <summary>
    /// El camino común de <see cref="CrearAsync"/> y <see cref="AgregarArticulosAsync"/>: UNA transacción, sin reintento
    /// (<c>ef-retry-safe-writes</c>, forma (b): ni las filas de precio ni la de auditoría ni la familia son idempotentes
    /// y un reintento no tiene clave de idempotencia), con este orden:
    ///
    /// <list type="number">
    /// <item>el lock de membresía EXCLUSIVO, primera sentencia: entrar a una familia cambia la pertenencia y ningún otro
    /// escritor de familias puede estar a mitad de camino hasta el commit;</item>
    /// <item>solo al agregar, la familia —viva— leída y bloqueada <c>FOR SHARE</c> (la misma lectura del alta de un
    /// artículo dentro de una familia): serializa con una baja o un cambio de <c>activo</c> en curso;</item>
    /// <item>las filas de <c>articulos</c>, ascendentes por id y <c>FOR NO KEY UPDATE</c> en un solo statement: la
    /// referencia y los destinos al crear; los miembros de la familia y los destinos al agregar (la referencia es uno de
    /// los miembros). Después, la ÚNICA lectura de las entidades, bajo esos locks;</item>
    /// <item>las validaciones, que no escriben: la familia (al agregar), el tope exacto de destinos sin la referencia de la
    /// familia (al agregar: <see cref="ReglaDeAgrupacion.ExigirTope"/>), los artículos que no existen, los que ya están
    /// en otra familia;</item>
    /// <item>los chequeos de catálogo del artículo de referencia, ya con las filas de los artículos bloqueadas, en el
    /// orden de la edición de artículos (<c>ServicioDeArticulos.ActualizarAsync</c>): la alícuota de IVA, que es global y
    /// no se bloquea, y el área, la categoría, el grupo y el proveedor habitual, cada uno <c>FOR KEY SHARE</c> sobre la
    /// fila viva (<see cref="GuardaDeReferencias.BloquearSiEstaVivaAsync{T}"/>): los trece valores de la referencia son
    /// los que se copian a los demás, y un id de catálogo que no existe no se copia (<c>400 referencia_invalida</c>);</item>
    /// <item>los locks de par artículo-lista de cada destino y cada lista fija, por clave ascendente
    /// (<see cref="ServicioDePrecios.TomarLocksDeParesAsync"/>): el superconjunto de lo que se puede escribir, tomado
    /// antes de leer ningún precio y después de exigir que los pares no pasen de
    /// <see cref="ReglaDeAgrupacion.MaximoDeParesPorPedido"/> (<see cref="ReglaDeAgrupacion.ExigirParesAcotados"/>, con
    /// las listas fijas ya leídas);</item>
    /// <item>"ahora", UNA vez; FASE 1, solo lecturas: se planifica la alineación de los precios y se rechaza el pedido
    /// si algún par no se puede alinear;</item>
    /// <item>FASE 2, solo escrituras: la familia nueva (primer <c>SaveChangesAsync</c>, que da su id), la pertenencia y los
    /// trece campos de cada artículo, las filas de precio y su auditoría (<see cref="ServicioDePrecios"/>, único escritor
    /// de <c>precios</c>), el segundo <c>SaveChangesAsync</c> y el commit.</item>
    /// </list>
    ///
    /// Si algo falla, la transacción se revierte entera y lo que la operación dejó rastreado se suelta del contexto
    /// (<see cref="RastreoDeEntidades"/>): el contexto vive todo el request y un guardado posterior lo escribiría por
    /// detrás. Después del commit, y fuera del lock, cada artículo cuyo <c>controla_lote</c> pasó de <c>false</c> a
    /// <c>true</c> reconcilia sus lotes, en orden ascendente de id; mantiene el contrato de fallo parcial de la edición de
    /// artículos —el cambio ya está comiteado—, y se recupera con <c>POST /api/stock/lotes/reconciliacion</c>.
    /// </summary>
    private async Task<ResultadoDeAgrupacion> AgruparAsync(Pedido pedido, CancellationToken ct)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        // Antes de la primera lectura: lo que esta operación deje rastreado se suelta si falla.
        var yaRastreadas = RastreoDeEntidades.Instantanea(db);

        List<int> idsConControlDeLoteActivado = [];
        ResultadoDeAgrupacion? resultado = null;

        try
        {
            await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await db.Database.BeginTransactionAsync(ct);

                var conexion = await ObtenerConexionAbiertaAsync(ct);
                var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

                await LockDeMembresiaDeFamilias.TomarExclusivoAsync(conexion, transaccionCruda, idTenant, ct);

                MembresiaDeFamilias.FamiliaParaIngresar? familiaExistente = null;
                List<int> bloqueados;

                if (pedido.IdFamiliaExistente is { } idFamiliaExistente)
                {
                    familiaExistente = await MembresiaDeFamilias.LeerFamiliaParaIngresarAsync(
                        conexion, transaccionCruda, idFamiliaExistente, idTenant, ct)
                        ?? throw ErrorDominio.NoEncontrado(MensajeDeFamiliaInexistente(idFamiliaExistente));

                    bloqueados = await MembresiaDeFamilias.BloquearMiembrosYArticulosAsync(
                        conexion, transaccionCruda, idFamiliaExistente, pedido.Destinos, idTenant, ct);
                }
                else
                {
                    bloqueados = await MembresiaDeFamilias.BloquearArticulosAsync(
                        conexion, transaccionCruda, [pedido.IdArticuloReferencia!.Value, .. pedido.Destinos], idTenant, ct);
                }

                // La ÚNICA lectura de las entidades, nacida bajo los locks de arriba y ascendente por id.
                var leidos = await db.Articulos.Where(a => bloqueados.Contains(a.Id)).OrderBy(a => a.Id).ToListAsync(ct);
                var articulos = leidos.ToDictionary(a => a.Id);

                int idReferencia;
                List<int> destinos;
                List<int> idsAExigir;

                if (familiaExistente is not null)
                {
                    var miembros = leidos.Where(a => a.IdFamilia == pedido.IdFamiliaExistente).ToList();

                    switch (ReglaDeFamilias.ResolverAgregado(familiaExistente.Activa, tieneMiembrosVivos: miembros.Count > 0))
                    {
                        case ResolucionDeIngresoAFamilia.FamiliaInactiva:
                            throw ErrorDominio.Conflicto("familia_inactiva", MensajeDeFamiliaInactiva(familiaExistente.Nombre));

                        case ResolucionDeIngresoAFamilia.FamiliaSinArticulos:
                            throw ErrorDominio.Conflicto(
                                "familia_sin_articulos",
                                $"La familia \"{familiaExistente.Nombre}\" no tiene artículos vivos: no hay un artículo de referencia al que alinear.");
                    }

                    idReferencia = miembros[0].Id;
                    destinos = [.. pedido.Destinos.Where(id => id != idReferencia)];

                    // El pedido pudo traer UNO más que el tope por si la referencia de la familia, que recién acá se
                    // conoce, era uno de los ids: el tope exacto es sobre los destinos que quedan sin ella.
                    ReglaDeAgrupacion.ExigirTope(destinos);

                    idsAExigir = destinos;
                }
                else
                {
                    idReferencia = pedido.IdArticuloReferencia!.Value;
                    destinos = [.. pedido.Destinos];
                    idsAExigir = [.. destinos.Prepend(idReferencia).Order()];
                }

                foreach (var id in idsAExigir)
                {
                    if (!articulos.ContainsKey(id))
                    {
                        throw new ErrorDominio("referencia_invalida", MensajeDeArticuloInexistente(id), 400);
                    }
                }

                var referencia = articulos[idReferencia];

                var enOtraFamilia = idsAExigir
                    .Where(id => articulos[id].IdFamilia is not null && articulos[id].IdFamilia != pedido.IdFamiliaExistente)
                    .ToList();

                if (enOtraFamilia.Count > 0)
                {
                    var nombres = await NombresDeFamiliasAsync([.. enOtraFamilia.Select(id => articulos[id].IdFamilia!.Value)], ct);

                    throw ErrorDominio.Conflicto(
                        "articulo_en_otra_familia", MensajeDeArticuloEnOtraFamilia(articulos[enOtraFamilia[0]], nombres));
                }

                await ExigirCatalogosDeLaReferenciaAsync(referencia, ct);

                var listas = await servicioDePrecios.ListasFijasAsync(ct);
                var idsListas = listas.Select(l => l.Id).ToList();

                ReglaDeAgrupacion.ExigirParesAcotados(destinos.Count, idsListas.Count);

                await servicioDePrecios.TomarLocksDeParesAsync(idTenant, destinos, idsListas, ct);

                var ahora = reloj.Ahora;

                var plan = await servicioDePrecios.PlanificarAlineacionAsync(
                    referencia.Id, destinos, idsListas, idTenant, ahora, ct);

                if (plan.PrimerRechazo is { } rechazo)
                {
                    throw new ErrorDominio(
                        "familia_precio_inalineable",
                        MensajeDePrecioInalineable(articulos[rechazo.IdArticulo], listas.Single(l => l.Id == rechazo.IdListaPrecio), rechazo),
                        422);
                }

                var visibles = await CatalogosVisibles.LeerAsync(
                    db, [ValoresCompartidosDeFamilia.De(referencia), .. destinos.Select(id => ValoresCompartidosDeFamilia.De(articulos[id]))], ct);

                int idFamilia;
                string nombreDeLaFamilia;

                if (familiaExistente is null)
                {
                    var familia = new Familia { Nombre = pedido.NombreNuevo!, Activo = true, CreatedAt = ahora, UpdatedAt = ahora };
                    db.Familias.Add(familia);
                    await db.SaveChangesAsync(ct);

                    idFamilia = familia.Id;
                    nombreDeLaFamilia = familia.Nombre;

                    referencia.IdFamilia = idFamilia;
                    referencia.UpdatedAt = ahora;
                }
                else
                {
                    idFamilia = pedido.IdFamiliaExistente!.Value;
                    nombreDeLaFamilia = familiaExistente.Nombre;
                }

                var valores = ValoresCompartidosDeFamilia.De(referencia);
                var cambios = new List<CambiosDeUnArticulo>(destinos.Count);

                foreach (var id in destinos)
                {
                    var destino = articulos[id];
                    var actuales = ValoresCompartidosDeFamilia.De(destino);
                    var campos = valores.CamposDistintos(actuales);
                    var entra = destino.IdFamilia != idFamilia;

                    if (!destino.ControlaLote && valores.ControlaLote)
                    {
                        idsConControlDeLoteActivado.Add(destino.Id);
                    }

                    valores.AplicarA(destino);

                    if (entra)
                    {
                        destino.IdFamilia = idFamilia;
                    }

                    if (entra || campos.Count > 0)
                    {
                        destino.UpdatedAt = ahora;
                    }

                    cambios.Add(CambiosDe(destino, actuales, valores, campos, plan, visibles));
                }

                await servicioDePrecios.EscribirAlineacionAsync(plan, idTenant, ahora, ct);
                await db.SaveChangesAsync(ct);
                await transaccion.CommitAsync(ct);

                resultado = new ResultadoDeAgrupacion(idFamilia, nombreDeLaFamilia, referencia.Id, cambios);
            });
        }
        catch
        {
            RastreoDeEntidades.SoltarLoAgregadoDesde(db, yaRastreadas);
            throw;
        }

        foreach (var idConControlDeLote in idsConControlDeLoteActivado)
        {
            await servicioDeLotes.ReconciliarAsync(idConControlDeLote, idPuntoVenta: null, ct);
        }

        return resultado!;
    }

    /// <summary>Lo que cambia en un artículo al alinearlo: los valores que tenía (<paramref name="actuales"/>, que quien
    /// llama toma ANTES de alinearlo), los de la referencia —los dos presentados como los lee un cliente: un id de
    /// catálogo que no es visible viaja como <c>null</c> (<see cref="CatalogosVisibles.Presentar"/>)—, las columnas que
    /// difieren, comparadas por el valor guardado, y los pares de la lista que se alinean.</summary>
    private static CambiosDeUnArticulo CambiosDe(
        Articulo articulo, ValoresCompartidosDeFamilia actuales, ValoresCompartidosDeFamilia valoresDeLaReferencia,
        IReadOnlyList<string> campos, PlanDeAlineacionDePrecios plan, CatalogosVisibles visibles) =>
        new(
            articulo.Id,
            campos,
            visibles.Presentar(actuales),
            visibles.Presentar(valoresDeLaReferencia),
            [
                .. plan.Pares
                    .Where(par => par.IdArticulo == articulo.Id && par.Resolucion == ResolucionDeAlineacionDePrecios.Alinear)
                    .Select(par => new CambioDePreciosDeLista(par.IdListaPrecio, par.Actual, par.Referencia))
            ]);

    private enum Catalogo
    {
        AlicuotaDeIva,
        Area,
        Categoria,
        Grupo,
        ProveedorHabitual
    }

    /// <summary>Los catálogos que el artículo de referencia comparte con los demás —los que se copian—, en el orden de la
    /// edición de artículos (<c>ServicioDeArticulos.ActualizarAsync</c>, que además valida la marca, que no es
    /// compartida): la alícuota de IVA, el área, la categoría, el grupo y el proveedor habitual. Los opcionales ausentes no
    /// figuran. Es la única definición del orden y de lo que se chequea: la comparten el pedido real y la
    /// previsualización.</summary>
    private static IEnumerable<(Catalogo Catalogo, int Id)> CatalogosDeLaReferencia(Articulo referencia)
    {
        yield return (Catalogo.AlicuotaDeIva, referencia.IdAlicuotaIva);
        yield return (Catalogo.Area, referencia.IdArea);

        if (referencia.IdCategoria is { } idCategoria)
        {
            yield return (Catalogo.Categoria, idCategoria);
        }

        if (referencia.IdGrupo is { } idGrupo)
        {
            yield return (Catalogo.Grupo, idGrupo);
        }

        if (referencia.IdProveedorHabitual is { } idProveedor)
        {
            yield return (Catalogo.ProveedorHabitual, idProveedor);
        }
    }

    /// <summary>Los chequeos de catálogo del pedido real: el de la edición de artículos aplicado a la referencia, con el
    /// mismo código y el mismo mensaje. El área, la categoría, el grupo y el proveedor habitual se toman
    /// <c>FOR KEY SHARE</c> sobre la fila VIVA (<see cref="GuardaDeReferencias.BloquearSiEstaVivaAsync{T}"/>), dentro de la
    /// transacción y con las filas de los artículos ya bloqueadas: una baja que gana la carrera hace que el chequeo
    /// falle en vez de copiar un id colgante a los demás. La alícuota de IVA es global y se chequea sin lock, como en la
    /// edición.</summary>
    private async Task ExigirCatalogosDeLaReferenciaAsync(Articulo referencia, CancellationToken ct)
    {
        foreach (var (catalogo, id) in CatalogosDeLaReferencia(referencia))
        {
            var existe = catalogo switch
            {
                Catalogo.AlicuotaDeIva => await db.AlicuotasIva.AnyAsync(a => a.Id == id, ct),
                Catalogo.Area => await guarda.BloquearSiEstaVivaAsync<Area>(id, ct),
                Catalogo.Categoria => await guarda.BloquearSiEstaVivaAsync<Categoria>(id, ct),
                Catalogo.Grupo => await guarda.BloquearSiEstaVivaAsync<Grupo>(id, ct),
                _ => await guarda.BloquearSiEstaVivaAsync<Proveedor>(id, ct)
            };

            if (!existe)
            {
                throw new ErrorDominio("referencia_invalida", MensajeDeCatalogoInexistente(catalogo, id), 400);
            }
        }
    }

    /// <summary>Los catálogos del artículo de referencia que no existen, uno por problema y en el orden en que el pedido
    /// real los rechaza. Solo lee, sin locks: el área, la categoría, el grupo y el proveedor habitual salen de
    /// <paramref name="visibles"/>.</summary>
    private async Task<List<ProblemaDeAgrupacion>> CatalogosInexistentesDeLaReferenciaAsync(
        Articulo referencia, CatalogosVisibles visibles, CancellationToken ct)
    {
        var problemas = new List<ProblemaDeAgrupacion>();

        foreach (var (catalogo, id) in CatalogosDeLaReferencia(referencia))
        {
            var existe = catalogo switch
            {
                Catalogo.AlicuotaDeIva => await db.AlicuotasIva.AsNoTracking().AnyAsync(a => a.Id == id, ct),
                Catalogo.Area => visibles.AreaEsVisible(id),
                Catalogo.Categoria => visibles.CategoriaEsVisible(id),
                Catalogo.Grupo => visibles.GrupoEsVisible(id),
                _ => visibles.ProveedorEsVisible(id)
            };

            if (!existe)
            {
                problemas.Add(new ProblemaDeAgrupacion(
                    "referencia_invalida", MensajeDeCatalogoInexistente(catalogo, id), referencia.Id, null));
            }
        }

        return problemas;
    }

    /// <summary>Los nombres de las familias dadas, también las dadas de baja: el mensaje de un artículo que ya
    /// pertenece a una familia la nombra aunque la fila de la familia ya no esté viva. Solo el filtro de baja lógica se
    /// ignora; el de tenant sigue puesto.</summary>
    private async Task<Dictionary<int, string>> NombresDeFamiliasAsync(IReadOnlyList<int> idsDeFamilia, CancellationToken ct)
    {
        if (idsDeFamilia.Count == 0)
        {
            return [];
        }

        var ids = idsDeFamilia.Distinct().ToArray();

        return await db.Familias
            .IgnoreQueryFilters(["BajaLogica"])
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Nombre, ct);
    }

    // =================================================================================================
    // Los mensajes de los rechazos: los mismos en la previsualización y en el pedido real
    // =================================================================================================

    private static string MensajeDeArticuloInexistente(int idArticulo) => $"No existe el artículo {idArticulo}.";

    private static string MensajeDeFamiliaInexistente(int idFamilia) => $"No existe la familia {idFamilia}.";

    /// <summary>Los mismos mensajes que la edición de artículos (<c>ServicioDeArticulos</c>) da por cada uno de estos
    /// catálogos; <c>FamiliasAgruparTests</c> los compara contra las respuestas reales de la edición.</summary>
    private static string MensajeDeCatalogoInexistente(Catalogo catalogo, int id) => catalogo switch
    {
        Catalogo.AlicuotaDeIva => $"No existe la alícuota de IVA {id}.",
        Catalogo.Area => $"No existe el área {id}.",
        Catalogo.Categoria => $"No existe la categoría {id}.",
        Catalogo.Grupo => $"No existe el grupo {id}.",
        _ => $"No existe el proveedor {id}."
    };

    private static string MensajeDeFamiliaInactiva(string nombre) =>
        $"La familia \"{nombre}\" está inactiva: no se le pueden agregar artículos.";

    private static string MensajeDeArticuloEnOtraFamilia(Articulo articulo, IReadOnlyDictionary<int, string> nombresDeFamilias) =>
        $"El artículo {articulo.CodigoInterno} ya pertenece a la familia " +
        $"\"{(nombresDeFamilias.TryGetValue(articulo.IdFamilia!.Value, out var nombre) ? nombre : $"{articulo.IdFamilia}")}\": " +
        "hay que sacarlo de ella antes de agruparlo.";

    private static string MensajeDePrecioInalineable(Articulo articulo, ListaFija lista, AlineacionDeUnPar par) =>
        $"No se puede alinear el artículo {articulo.CodigoInterno} en la lista \"{lista.Nombre}\": " + par.Resolucion switch
        {
            ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSinPrecios =>
                "el artículo de referencia no tiene ningún precio en esa lista y este sí, y un precio no se puede quitar.",
            ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSoloProgramada =>
                "el artículo de referencia solo tiene un precio programado y este ya tiene un precio vigente, que no se puede quitar.",
            ResolucionDeAlineacionDePrecios.InalineablePorPrecioPredecesorPosterior =>
                "su precio programado reemplaza a uno que empieza después de este momento, y ese no se puede cerrar.",
            _ => throw new InvalidOperationException($"La resolución {par.Resolucion} no es un rechazo.")
        };

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant: un actor de plataforma nunca llega hasta
            // acá. Defensa en profundidad, no un camino alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeAgrupacionDeFamilias requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }
}
