using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

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
/// Es todo o nada: un artículo inexistente, en otra familia o con un precio que no se puede alinear rechaza el pedido
/// entero y no se escribe nada. La previsualización es solo lectura, sin locks y sin transacción: una foto.</para>
///
/// <para>Los pedidos que escriben cambian la pertenencia, así que siguen el protocolo de locks de las familias
/// (<see cref="LockDeMembresiaDeFamilias"/>): el lock de membresía EXCLUSIVO como primera sentencia; la fila de la
/// familia, si ya existe; las filas de los artículos ascendentes; y los locks de par artículo-lista por clave
/// ascendente. "Ahora" se lee una sola vez, después de todos los locks.</para>
/// </summary>
public class ServicioDeAgrupacionDeFamilias(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDePrecios servicioDePrecios,
    ServicioDeLotes servicioDeLotes)
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

        var listas = await servicioDePrecios.ListasFijasAsync(ct);
        var plan = await servicioDePrecios.PlanificarAlineacionAsync(
            referencia.Id, alineables, [.. listas.Select(l => l.Id)], idTenant, ahora, ct);

        var valoresDeLaReferencia = ValoresCompartidosDeFamilia.De(referencia);

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
                    articulos[id], actuales, valoresDeLaReferencia, valoresDeLaReferencia.CamposDistintos(actuales), plan);
            })
            .ToList();

        return new PrevisualizacionDeAgrupacion(referencia.Id, referencia.IdFamilia, cambios, problemas);
    }

    /// <summary>
    /// Crea la familia <see cref="AltaDeFamilia.Nombre"/> y la agrupa: el artículo de referencia y los pedidos quedan como
    /// sus miembros, y los pedidos, alineados con la referencia (<see cref="ResultadoDeAgrupacion"/>). Rechaza, sin
    /// escribir nada: <c>400</c> por el nombre, la referencia o el tope; <c>409 familia_nombre_duplicado</c>;
    /// <c>400 referencia_invalida</c> por un artículo que no existe o está dado de baja; <c>409
    /// articulo_en_otra_familia</c> por uno que ya es miembro de una familia —la referencia incluida: agrupar no mueve a
    /// nadie—; y <c>422 familia_precio_inalineable</c> por un par que no se puede alinear.
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
    /// está vacía; <c>404</c> si la familia no existe o está dada de baja; <c>409 familia_inactiva</c>; <c>409
    /// familia_sin_articulos</c> si no tiene ningún miembro vivo, y por lo tanto referencia; y, para los artículos, lo
    /// mismo que <see cref="CrearAsync"/>: <c>400 referencia_invalida</c>, <c>409 articulo_en_otra_familia</c> y <c>422
    /// familia_precio_inalineable</c>. Un artículo que ya es miembro de esta familia no es un rechazo: se alinea igual.
    /// El detalle del orden y de los locks está en <see cref="AgruparAsync"/>.
    /// </summary>
    public async Task<ResultadoDeAgrupacion> AgregarArticulosAsync(
        int idFamilia, AgregadoDeArticulos datos, CancellationToken ct = default)
    {
        var destinos = ReglaDeAgrupacion.Destinos(datos.IdsArticulos, idArticuloReferencia: null);

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
    /// <item>las validaciones, que no escriben: la familia (al agregar), los artículos que no existen, los que ya están
    /// en otra familia;</item>
    /// <item>los locks de par artículo-lista de cada destino y cada lista fija, por clave ascendente
    /// (<see cref="ServicioDePrecios.TomarLocksDeParesAsync"/>): el superconjunto de lo que se puede escribir, tomado
    /// antes de leer ningún precio;</item>
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

                var listas = await servicioDePrecios.ListasFijasAsync(ct);
                var idsListas = listas.Select(l => l.Id).ToList();

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

                    cambios.Add(CambiosDe(destino, actuales, valores, campos, plan));
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
    /// llama toma ANTES de alinearlo), los de la referencia, las columnas que difieren y los pares de la lista que se
    /// alinean.</summary>
    private static CambiosDeUnArticulo CambiosDe(
        Articulo articulo, ValoresCompartidosDeFamilia actuales, ValoresCompartidosDeFamilia valoresDeLaReferencia,
        IReadOnlyList<string> campos, PlanDeAlineacionDePrecios plan) =>
        new(
            articulo.Id,
            campos,
            actuales,
            valoresDeLaReferencia,
            [
                .. plan.Pares
                    .Where(par => par.IdArticulo == articulo.Id && par.Resolucion == ResolucionDeAlineacionDePrecios.Alinear)
                    .Select(par => new CambioDePreciosDeLista(par.IdListaPrecio, par.Actual, par.Referencia))
            ]);

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
