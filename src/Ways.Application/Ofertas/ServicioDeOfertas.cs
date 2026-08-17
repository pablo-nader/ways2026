using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;

namespace Ways.Application.Ofertas;

/// <summary>
/// ABM de ofertas + targeting de listas vía <c>ofertas_listas</c> (design decision 6: entidad/
/// servicio dedicados, <c>Oferta</c> NO extiende <c>ServicioDeCatalogo&lt;T,TListado,TAlta&gt;</c>
/// — <c>nombre</c> es una etiqueta de ticket deliberadamente no única, y las dos CHECKs de
/// exclusividad más la junction son la misma clase de divergencia que ya tomaron
/// <see cref="Articulos.ServicioDeArticulos"/>/<see cref="Precios.ServicioDePrecios"/>).
/// Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la capa de API.
///
/// Todo camino de escritura pasa por las cinco guardas de <see cref="ReglaDeOfertas"/> (pura,
/// sin DB) ANTES de tocar la fila (design: Protection Rules) — las cuatro CHECKs de esquema
/// quedan como defensa en profundidad, alcanzable solo por una escritura cruda/fuera de banda.
///
/// El alta abre transacción explícita (mismo patrón que
/// <see cref="Articulos.ServicioDeArticulos.CrearAsync"/>): necesita el <c>Id</c> autogenerado
/// de la oferta antes de poder insertar las filas de <see cref="OfertaLista"/> que la referencian,
/// así que hacen falta dos <c>SaveChangesAsync</c> atómicos entre sí.
///
/// (judgment-day, item 1) <see cref="ActualizarAsync"/> TAMBIÉN abre transacción explícita —
/// contrario a lo que decía este comentario antes del fix: el "un solo SaveChangesAsync ya es
/// atómico" solo protegía el replace-set CONTRA SÍ MISMO, no contra otro PUT concurrente
/// leyendo <c>filasActuales</c> ANTES de que el primero comiteara. Dos PUT concurrentes con
/// targets DISTINTOS (p.ej. A → [1], B → [2]) pasaban las dos por un <c>filasActuales</c> vacío,
/// y el orden de commit determinaba cuál sobrevivía — sin lock, ninguna elegía "la última en
/// escribir gana" de forma confiable, y en el peor caso (B comitea, A comitea después con un
/// DELETE que ya no afecta ninguna fila) el DELETE de A no fallaba pero tampoco revertía el
/// INSERT de B, dejando la UNIÓN de ambos targets persistida — lost update silencioso. Mismo
/// mecanismo que <see cref="Precios.ServicioDePrecios.AbrirNuevoPrecioAsync"/> lo resuelve acá:
/// <see cref="TomarLockDeOfertaAsync"/> toma un <c>pg_advisory_xact_lock</c> determinístico por
/// <c>(idTenant, idOferta)</c> ANTES de releer <c>filasActuales</c> — el segundo llamador espera
/// a que el primero comitee, y al retomar el lock ve el estado YA COMITEADO, así que hace un
/// reemplazo limpio en vez de competir a ciegas ("último committer gana", nunca una unión, nunca
/// un DELETE de 0 filas). Esto mueve <see cref="ActualizarAsync"/> completo fuera del proveedor
/// InMemory (mismo "transaction-blocked-provider caveat" que <see cref="CrearAsync"/>/
/// <c>ServicioDeArticulosTests</c>) — las pruebas de <c>ServicioDeOfertasTests</c> que cubrían el
/// replace-set persistido se movieron a <c>OfertasEndpointsTests</c> (Postgres real).
///
/// (judgment-day ronda 2, item 1 — CRITICAL) El lock de <see cref="ActualizarAsync"/> serializaba
/// PUT contra PUT, pero no PUT contra <see cref="EliminarAsync"/>: ese último ni abría transacción
/// ni tomaba el lock, así que un PUT podía leer la oferta viva, un DELETE concurrente comiteaba
/// primero (fuera de cualquier lock), y el PUT — que nunca volvía a chequear <c>DeletedAt</c> —
/// terminaba pisando los campos editables sobre una fila YA ELIMINADA (ghost edit: 200 del PUT +
/// <c>deleted_at</c> seteado, sin que ninguno de los dos escritores fallara). Ahora
/// <see cref="EliminarAsync"/> abre la MISMA transacción explícita y toma el MISMO
/// <see cref="TomarLockDeOfertaAsync"/> ANTES de leer la fila, y <see cref="ActualizarAsync"/>
/// re-chequea existencia con un <c>EXISTS</c> plano DESPUÉS de tomar el lock (nunca reusa la
/// entidad trackeada desde antes de la transacción, que el identity map de EF no refresca sola)
/// — cualquiera de los dos que pierda la carrera del lock ve el estado YA COMITEADO por el otro.</summary>
public class ServicioDeOfertas(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDePrecios servicioDePrecios)
{
    /// <summary>Sin filtro de <c>Activo</c> (a diferencia de <c>ServicioDeCatalogo</c>) —
    /// mismo criterio que <c>ArticuloListado</c>: el filtro global de baja lógica ya deja
    /// afuera las eliminadas, y una oferta inactiva sigue siendo administrable desde el
    /// listado. <see cref="OfertaListado.IdsListas"/> vacío por fila (evita el N+1 de una
    /// query por oferta listada, mismo criterio que <c>ArticuloListado.IdsEmpresas</c>).</summary>
    public async Task<IReadOnlyList<OfertaListado>> ListarAsync(
        bool incluirEliminados = false, CancellationToken ct = default)
    {
        var query = db.Ofertas.AsQueryable();

        if (incluirEliminados)
        {
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        var items = await query.OrderBy(o => o.Nombre).ToListAsync(ct);

        return items.Select(o => Proyectar(o, Array.Empty<int>())).ToList();
    }

    public async Task<OfertaListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var oferta = await BuscarAsync(id, ct);
        var idsListas = await IdsListasDeAsync(oferta.Id, ct);

        return Proyectar(oferta, idsListas);
    }

    public async Task<OfertaListado> CrearAsync(AltaOferta datos, CancellationToken ct = default)
    {
        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var diasSemana = ConvertirDiasSemana(datos.DiasSemana);
        var idsListas = datos.IdsListas?.Distinct().ToList();

        var oferta = new Oferta
        {
            Nombre = nombre,
            IdEmpresa = datos.IdEmpresa,
            IdArticulo = datos.IdArticulo,
            IdGrupo = datos.IdGrupo,
            IdCategoria = datos.IdCategoria,
            FechaDesde = datos.FechaDesde,
            FechaHasta = datos.FechaHasta,
            HoraDesde = datos.HoraDesde,
            HoraHasta = datos.HoraHasta,
            DiasSemana = diasSemana,
            CantidadMinima = datos.CantidadMinima,
            PrecioUnitario = datos.PrecioUnitario,
            Porcentaje = datos.Porcentaje,
            ImporteFijo = datos.ImporteFijo,
            Prioridad = datos.Prioridad,
            Acumulable = datos.Acumulable,
            Activo = datos.Activo
        };

        var alcance = ValidarInvariantes(oferta);

        await ExigirAlcanceValidoAsync(alcance, ct);
        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        if (idsListas is { Count: > 0 })
        {
            await ExigirListasValidasAsync(idsListas, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        var ahora = reloj.Ahora;
        oferta.CreatedAt = ahora;
        oferta.UpdatedAt = ahora;

        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            db.Ofertas.Add(oferta);
            await db.SaveChangesAsync(ct);

            if (idsListas is { Count: > 0 })
            {
                AgregarFilasDeListas(oferta.Id, idTenant, idsListas);
                await db.SaveChangesAsync(ct);
            }

            await transaccion.CommitAsync(ct);

            return Proyectar(oferta, (IReadOnlyList<int>?)idsListas ?? Array.Empty<int>());
        });
    }

    public async Task<OfertaListado> ActualizarAsync(int id, EdicionOferta datos, CancellationToken ct = default)
    {
        var oferta = await BuscarAsync(id, ct);

        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var diasSemana = ConvertirDiasSemana(datos.DiasSemana);
        var idsListas = datos.IdsListas?.Distinct().ToList();

        // Validar sobre un candidato transitorio (nunca agregado al DbSet) ANTES de tocar la
        // fila trackeada: si ReglaDeOfertas rechaza el shape, `oferta` queda intacta en memoria
        // (mismo espíritu de seguridad que ServicioDeArticulos.ActualizarAsync, que valida los
        // datos crudos antes de asignar ningún campo).
        var candidato = new Oferta
        {
            Nombre = nombre,
            IdEmpresa = datos.IdEmpresa,
            IdArticulo = datos.IdArticulo,
            IdGrupo = datos.IdGrupo,
            IdCategoria = datos.IdCategoria,
            FechaDesde = datos.FechaDesde,
            FechaHasta = datos.FechaHasta,
            HoraDesde = datos.HoraDesde,
            HoraHasta = datos.HoraHasta,
            DiasSemana = diasSemana,
            CantidadMinima = datos.CantidadMinima,
            PrecioUnitario = datos.PrecioUnitario,
            Porcentaje = datos.Porcentaje,
            ImporteFijo = datos.ImporteFijo,
            Prioridad = datos.Prioridad,
            Acumulable = datos.Acumulable,
            Activo = datos.Activo
        };

        var alcance = ValidarInvariantes(candidato);

        await ExigirAlcanceValidoAsync(alcance, ct);
        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        if (idsListas is { Count: > 0 })
        {
            await ExigirListasValidasAsync(idsListas, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // (judgment-day, item 1) Lock ANTES de tocar nada de ofertas_listas — serializa
            // cualquier otro PUT concurrente sobre la MISMA oferta, exista o no una fila de
            // targeting todavía. Recién con el lock tomado es seguro releer filasActuales:
            // ningún otro escritor puede estar reemplazando el subconjunto en simultáneo.
            await TomarLockDeOfertaAsync(idTenant, id, ct);

            // (judgment-day ronda 2, item 1 — CRITICAL) Re-chequeo de existencia DESPUÉS del
            // lock, vía un EXISTS plano (nunca materializa entidad, así que nunca pasa por el
            // identity map) en vez de reusar `oferta` (ya trackeada desde ANTES de la
            // transacción, con su `DeletedAt` de esa foto vieja). Sin esto: un DELETE
            // concurrente que gana la carrera del lock, comitea PRIMERO y sale de la
            // transacción deja la fila con `deleted_at` seteado en la DB — pero `oferta` en
            // memoria seguía "viva" (el identity map de EF NO refresca una entidad ya
            // trackeada con los valores de una query posterior), así que este PUT hubiera
            // seguido de largo, pisado los campos editables y comiteado un 200 sobre una
            // oferta YA ELIMINADA (ghost edit — ni el DELETE lo revertía, porque nunca tocó
            // `DeletedAt`). El filtro `BajaLogica` ya excluye la fila del EXISTS si está
            // borrada, así que el mismo 404 uniforme (ADR-8) que `BuscarAsync` cubre acá el
            // caso "borrada por otro escritor mientras esperaba el lock".
            if (!await db.Ofertas.AnyAsync(o => o.Id == id, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe la oferta {id}.");
            }

            oferta.Nombre = candidato.Nombre;
            oferta.IdEmpresa = candidato.IdEmpresa;
            oferta.IdArticulo = candidato.IdArticulo;
            oferta.IdGrupo = candidato.IdGrupo;
            oferta.IdCategoria = candidato.IdCategoria;
            oferta.FechaDesde = candidato.FechaDesde;
            oferta.FechaHasta = candidato.FechaHasta;
            oferta.HoraDesde = candidato.HoraDesde;
            oferta.HoraHasta = candidato.HoraHasta;
            oferta.DiasSemana = candidato.DiasSemana;
            oferta.CantidadMinima = candidato.CantidadMinima;
            oferta.PrecioUnitario = candidato.PrecioUnitario;
            oferta.Porcentaje = candidato.Porcentaje;
            oferta.ImporteFijo = candidato.ImporteFijo;
            oferta.Prioridad = candidato.Prioridad;
            oferta.Acumulable = candidato.Acumulable;
            oferta.Activo = candidato.Activo;
            oferta.UpdatedAt = reloj.Ahora;

            // Reemplaza el subconjunto entero (INSERT/DELETE físico, sin historial que preservar
            // — OfertaLista es PK-only, Slice 1): design: Protection Rules, "Lista set
            // replacement is atomic". Releído DESPUÉS del lock (judgment-day, item 1) — un
            // segundo llamador que esperó el lock ve acá el estado YA COMITEADO por el primero,
            // nunca la foto vieja que produciría la unión de ambos targets o un DELETE de 0
            // filas.
            var filasActuales = await db.OfertasListas.Where(ol => ol.IdOferta == id).ToListAsync(ct);
            db.OfertasListas.RemoveRange(filasActuales);

            if (idsListas is { Count: > 0 })
            {
                AgregarFilasDeListas(id, idTenant, idsListas);
            }

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return Proyectar(oferta, (IReadOnlyList<int>?)idsListas ?? Array.Empty<int>());
        });
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila. Las filas de
    /// <see cref="OfertaLista"/> asociadas quedan como están — sin cascada, mismo criterio que
    /// <see cref="Articulos.ServicioDeArticulos.EliminarAsync"/> con
    /// <see cref="Domain.Articulos.ArticuloEmpresa"/>.
    ///
    /// (judgment-day ronda 2, item 1 — CRITICAL) Mismo <c>CreateExecutionStrategy</c> + transacción
    /// explícita que <see cref="ActualizarAsync"/>/<see cref="CrearAsync"/>, con el
    /// <c>pg_advisory_xact_lock</c> de <see cref="TomarLockDeOfertaAsync"/> tomado ANTES de leer
    /// la fila: antes de este fix, el DELETE no abría transacción propia ni tomaba ningún lock, así
    /// que un PUT concurrente podía leer la oferta ANTES de que este DELETE comiteara, pisar
    /// campos editables con su propio <c>SaveChangesAsync</c> DESPUÉS, y dejar la fila con
    /// <c>deleted_at</c> seteado (por este DELETE) a la vez que los campos frescos del PUT
    /// (ghost edit — ninguno de los dos escritores fallaba). Con el lock, el DELETE solo lee la
    /// fila DESPUÉS de tomarlo — si un PUT lo tiene tomado, este DELETE espera hasta que
    /// comitee y recién ahí lee y marca el estado YA COMITEADO, nunca una foto vieja.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();

        var estrategia = db.Database.CreateExecutionStrategy();

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDeOfertaAsync(idTenant, id, ct);

            var oferta = await BuscarAsync(id, ct);

            var ahora = reloj.Ahora;
            oferta.DeletedAt = ahora;
            oferta.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Resolución batch, query-only (task 3.5; design: Technical Approach — "7 constant queries
    /// per resolution call, independent of N articles × M listas"; spec: resolucion-de-ofertas /
    /// Batch Input Shape). NUNCA escribe: ni acá ni en <see cref="ResolvedorDeOfertas"/> hay un
    /// <c>SaveChangesAsync</c> — el resultado se reporta, nunca se persiste (spec: Applied
    /// Ofertas Are Reported, Never Persisted).
    ///
    /// <para>Presupuesto: 1 <c>articulos</c> + 1 <c>categorias</c> (mapa de ancestros completo del
    /// tenant) + 1 <c>ofertas</c> (filtro grueso: <c>activo</c>, alcance por ids, <c>id_empresa</c>)
    /// + 1 <c>ofertas_listas</c> + hasta 3 de <see cref="ServicioDePrecios.PreciosVigentesEnLoteAsync"/>
    /// = 7, sin loop de una consulta por línea. El resto del matching (ventana de vigencia,
    /// <c>cantidad_minima</c>, lista objetivo, día de semana, alcance jerárquico de categoría) lo
    /// hace <see cref="ResolvedorDeOfertas.Coincide"/> en memoria, por línea, sobre las mismas
    /// <see cref="OfertaCandidata"/> ya materializadas — el único filtro por línea que corre ACÁ
    /// (no en el resolver puro, porque <see cref="LineaAResolver"/> no lleva <c>id_empresa</c>,
    /// design: Resolution Contract) es <see cref="ReglaDeOfertas.CoincideEmpresa"/>.</para>
    ///
    /// <para>Hora local (design: Open Questions, "server-configured local time" v1): el único
    /// <paramref name="momento"/> del lote se descompone UNA vez con <see cref="TimeZoneInfo.Local"/>
    /// — no hay huso horario de tenant modelado todavía.</para>
    /// </summary>
    public async Task<IReadOnlyList<ResultadoDeResolucion>> ResolverAsync(
        IReadOnlyList<LineaDeResolucion>? lineas, DateTimeOffset? momento, CancellationToken ct = default)
    {
        // La clave "lineas" ausente o explícitamente `null` en el body deserializa igual acá
        // (STJ no valida `required` con constructores `SetsRequiredMembers`), así que el chequeo
        // vive en el servicio: distingue un body malformado (400) de un lote vacío legítimo
        // (`[]` ⇒ resultado vacío, sin error).
        if (lineas is null)
        {
            throw new ErrorDominio("lineas_requeridas", "El campo 'lineas' es obligatorio.", 400);
        }

        if (lineas.Count == 0)
        {
            return [];
        }

        var idsArticulo = lineas.Select(l => l.IdArticulo).Distinct().ToList();
        var idsListaPrecio = lineas.Select(l => l.IdListaPrecio).Distinct().ToList();

        // 1 consulta: alcance de los artículos pedidos (categoría/grupo propios).
        var articulos = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .Select(a => new { a.Id, a.IdCategoria, a.IdGrupo })
            .ToListAsync(ct);

        var articuloPorId = articulos.ToDictionary(a => a.Id);

        var idsArticuloFaltantes = idsArticulo.Except(articuloPorId.Keys).ToList();
        if (idsArticuloFaltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idsArticuloFaltantes[0]}.", 400);
        }

        // 1 consulta: mapa de ancestros de TODA la jerarquía de categorías del tenant — se arma
        // en memoria por artículo, sin una consulta jerárquica por artículo (design: Batch
        // Boundary — Categoria scope matching).
        var padrePorCategoria = await db.Categorias
            .Select(c => new { c.Id, c.IdCategoriaPadre })
            .ToDictionaryAsync(c => c.Id, c => c.IdCategoriaPadre, ct);

        var idsCategoriasPorArticulo = new Dictionary<int, IReadOnlySet<int>>(articulos.Count);
        var todasLasCategoriasAlcanzables = new HashSet<int>();
        var idsGrupo = new HashSet<int>();

        foreach (var articulo in articulos)
        {
            var ancestros = articulo.IdCategoria is { } idCategoria
                ? CadenaDeCategorias.ConstruirAncestros(idCategoria, padrePorCategoria)
                : (IReadOnlySet<int>)new HashSet<int>();

            idsCategoriasPorArticulo[articulo.Id] = ancestros;
            todasLasCategoriasAlcanzables.UnionWith(ancestros);

            if (articulo.IdGrupo is { } idGrupo)
            {
                idsGrupo.Add(idGrupo);
            }
        }

        var idsEmpresa = lineas
            .Where(l => l.IdEmpresa is not null)
            .Select(l => l.IdEmpresa!.Value)
            .Distinct()
            .ToList();

        // 1 consulta: ofertas activas cuyo alcance (por id, superconjunto de las líneas pedidas)
        // e id_empresa podrían aplicar a ALGUNA línea del lote — el matching fino por línea
        // (incl. id_empresa exacto) corre después, en memoria.
        var ofertas = await db.Ofertas
            .Where(o => o.Activo &&
                ((o.IdArticulo != null && idsArticulo.Contains(o.IdArticulo.Value)) ||
                 (o.IdGrupo != null && idsGrupo.Contains(o.IdGrupo.Value)) ||
                 (o.IdCategoria != null && todasLasCategoriasAlcanzables.Contains(o.IdCategoria.Value))) &&
                (o.IdEmpresa == null || idsEmpresa.Contains(o.IdEmpresa.Value)))
            .ToListAsync(ct);

        var idsOferta = ofertas.Select(o => o.Id).ToList();

        // 1 consulta: targeting de listas de las ofertas candidatas.
        var listasPorOferta = idsOferta.Count == 0
            ? new Dictionary<int, IReadOnlySet<int>>()
            : (await db.OfertasListas
                .Where(ol => idsOferta.Contains(ol.IdOferta))
                .Select(ol => new { ol.IdOferta, ol.IdListaPrecio })
                .ToListAsync(ct))
                .GroupBy(ol => ol.IdOferta)
                .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(x => x.IdListaPrecio).ToHashSet());

        // Candidatas materializadas UNA vez, compartidas entre todas las líneas — ReglaDeOfertas
        // ya validó estas cinco guardas al escribir (Slice 2), así que proyectarlas acá nunca
        // debería lanzar; si lo hace, es una fila corrupta fuera de banda (defensa en profundidad,
        // no un camino alcanzable en operación normal).
        var candidatasPorOferta = ofertas.Select(o => new
        {
            Oferta = o,
            Candidata = new OfertaCandidata(
                o.Id, o.Nombre, o.Prioridad, o.Acumulable,
                ReglaDeOfertas.LeerAlcance(o), ReglaDeOfertas.LeerBeneficio(o), o.CantidadMinima,
                o.FechaDesde, o.FechaHasta, o.HoraDesde, o.HoraHasta,
                ReglaDeOfertas.LeerDiasSemana(o.DiasSemana),
                listasPorOferta.TryGetValue(o.Id, out var listas) ? listas : new HashSet<int>())
        }).ToList();

        // Hasta 3 consultas: precios vigentes en lote para el producto cartesiano de artículos ×
        // listas pedidas (design decision 5 — ServicioDePrecios.PreciosVigentesAsync/
        // PrecioVigenteAsync quedan sin tocar).
        var momentoEfectivo = momento ?? reloj.Ahora;
        var precios = await servicioDePrecios.PreciosVigentesEnLoteAsync(idsArticulo, idsListaPrecio, momentoEfectivo, ct);

        var (fechaLocal, horaLocal, diaSemanaLocal) = DescomponerHoraLocal(momentoEfectivo);

        var resultado = new List<ResultadoDeResolucion>(lineas.Count);

        foreach (var linea in lineas)
        {
            var precioOriginal = precios.TryGetValue((linea.IdArticulo, linea.IdListaPrecio), out var monto)
                ? monto
                : null;

            if (precioOriginal is null)
            {
                resultado.Add(new ResultadoDeResolucion(
                    linea.IdArticulo, linea.IdListaPrecio, null, null, 0m, []));
                continue;
            }

            var candidatasDeLaLinea = candidatasPorOferta
                .Where(c => ReglaDeOfertas.CoincideEmpresa(c.Oferta.IdEmpresa, linea.IdEmpresa))
                .Select(c => c.Candidata)
                .ToList();

            var lineaAResolver = new LineaAResolver(
                linea.IdArticulo, articuloPorId[linea.IdArticulo].IdGrupo,
                idsCategoriasPorArticulo[linea.IdArticulo].ToList(),
                linea.IdListaPrecio, linea.Cantidad, precioOriginal.Value,
                fechaLocal, horaLocal, diaSemanaLocal);

            var resuelto = ResolvedorDeOfertas.Resolver(lineaAResolver, candidatasDeLaLinea);

            resultado.Add(new ResultadoDeResolucion(
                linea.IdArticulo, linea.IdListaPrecio, resuelto.PrecioOriginal, resuelto.PrecioFinal,
                resuelto.DescuentoUnitario,
                resuelto.Aplicadas.Select(a => new OfertaAplicadaDto(a.IdOferta, a.Nombre, a.DescuentoUnitario)).ToList()));
        }

        return resultado;
    }

    /// <summary>Design: Open Questions, "Time zone for hora_desde/hasta and dias_semana
    /// matching" — v1 usa <see cref="TimeZoneInfo.Local"/> (huso del servidor, no hay huso de
    /// tenant modelado todavía). Día de semana ISO-8601 (1 = lunes … 7 = domingo) — .NET expone
    /// <see cref="DayOfWeek"/> con domingo = 0, así que se remapea acá.</summary>
    private static (DateOnly Fecha, TimeOnly Hora, int DiaSemana) DescomponerHoraLocal(DateTimeOffset momento)
    {
        var local = TimeZoneInfo.ConvertTime(momento, TimeZoneInfo.Local);
        var diaSemanaDotNet = (int)local.DayOfWeek;
        var diaSemanaIso = diaSemanaDotNet == 0 ? 7 : diaSemanaDotNet;

        return (DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime), diaSemanaIso);
    }

    /// <summary>Las cinco guardas de <see cref="ReglaDeOfertas"/> (design: Protection Rules) —
    /// alcance y beneficio exclusivos, rango de <c>cantidad_minima</c>, ventana de vigencia
    /// válida, <c>dias_semana</c> subset sin duplicados. Devuelve el <see cref="AlcanceDeOferta"/>
    /// ya proyectado para que el llamador sepa qué referencia tenant-scoped validar a
    /// continuación, sin tener que releer las tres columnas nullable crudas.</summary>
    private static AlcanceDeOferta ValidarInvariantes(Oferta candidato)
    {
        var alcance = ReglaDeOfertas.LeerAlcance(candidato);
        ReglaDeOfertas.LeerBeneficio(candidato);
        ReglaDeOfertas.ValidarCantidadMinima(candidato.CantidadMinima);
        ReglaDeOfertas.ValidarVentana(
            candidato.FechaDesde, candidato.FechaHasta, candidato.HoraDesde, candidato.HoraHasta);
        ReglaDeOfertas.LeerDiasSemana(candidato.DiasSemana);

        return alcance;
    }

    /// <summary>db-error-backstops: pre-chequeo de existencia tenant-scoped antes del INSERT/
    /// UPDATE — el backstop real sigue siendo la FK compuesta correspondiente
    /// (<c>fk_ofertas_articulo</c>/<c>fk_ofertas_grupo</c>/<c>fk_ofertas_categoria</c>, 23503 →
    /// 400 <c>referencia_invalida</c>, genérico desde la Slice 1). <paramref name="alcance"/> ya
    /// garantiza que exactamente uno de los tres está seteado (<see cref="ValidarInvariantes"/>
    /// corrió antes), así que este método solo despacha a la tabla correcta.</summary>
    private async Task ExigirAlcanceValidoAsync(AlcanceDeOferta alcance, CancellationToken ct)
    {
        if (alcance.IdArticulo is { } idArticulo)
        {
            if (!await db.Articulos.AnyAsync(a => a.Id == idArticulo, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idArticulo}.", 400);
            }

            return;
        }

        if (alcance.IdGrupo is { } idGrupo)
        {
            if (!await db.Grupos.AnyAsync(g => g.Id == idGrupo, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe el grupo {idGrupo}.", 400);
            }

            return;
        }

        var idCategoria = alcance.IdCategoria!.Value;
        if (!await db.Categorias.AnyAsync(c => c.Id == idCategoria, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la categoría {idCategoria}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAlcanceValidoAsync"/>, para
    /// <c>fk_ofertas_empresa</c> — <see cref="Oferta.IdEmpresa"/> es nullable (<c>NULL</c> =
    /// todo el tenant, design decision 5).</summary>
    private async Task ExigirEmpresaValidaAsync(int? idEmpresa, CancellationToken ct)
    {
        if (idEmpresa is null)
        {
            return;
        }

        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la empresa {idEmpresa}.", 400);
        }
    }

    /// <summary>Spec: "Junction row references must belong to the same tenant" — pre-chequeo
    /// tenant-scoped (filtro de EF, sin <c>IgnoreQueryFilters</c>, per db-error-backstops) antes
    /// de escribir cualquier fila de <see cref="OfertaLista"/>: cubre a la vez "no existe" y "es
    /// de otro tenant" con el mismo 400, backstop real <c>fk_ofertas_listas_lista_precio</c>.
    ///
    /// (judgment-day, item 4) Una sola consulta por conjunto en vez de un <c>AnyAsync</c> por id
    /// — mismo resultado (400 <c>referencia_invalida</c> ante la primera referencia inválida, en
    /// el orden de <paramref name="idsListas"/>), sin el round trip N+1 del <c>foreach</c>
    /// anterior.</summary>
    private async Task ExigirListasValidasAsync(IReadOnlyList<int> idsListas, CancellationToken ct)
    {
        var idsExistentes = await db.ListasPrecio
            .Where(l => idsListas.Contains(l.Id))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var idsInvalidos = idsListas.Except(idsExistentes).ToList();

        if (idsInvalidos.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la lista de precios {idsInvalidos[0]}.", 400);
        }
    }

    private void AgregarFilasDeListas(int idOferta, int idTenant, IReadOnlyList<int> idsListas)
    {
        foreach (var idLista in idsListas)
        {
            db.OfertasListas.Add(new OfertaLista
            {
                IdOferta = idOferta,
                IdListaPrecio = idLista,
                IdTenant = idTenant
            });
        }
    }

    private async Task<IReadOnlyList<int>> IdsListasDeAsync(int idOferta, CancellationToken ct) =>
        await db.OfertasListas
            .Where(ol => ol.IdOferta == idOferta)
            .OrderBy(ol => ol.IdListaPrecio)
            .Select(ol => ol.IdListaPrecio)
            .ToListAsync(ct);

    /// <summary>(judgment-day, item 1) <c>pg_advisory_xact_lock</c> con alcance de TRANSACCIÓN
    /// determinístico por <c>(idTenant, idOferta)</c> — a diferencia de
    /// <see cref="Precios.ServicioDePrecios.ClaveDeLockDePar"/>, acá no hace falta combinar dos
    /// ids en uno: el segundo argumento del lock ya ES <paramref name="idOferta"/> directo, sin
    /// riesgo de colisión entre ofertas distintas del mismo tenant. Mismo criterio DELIBERADO
    /// que Precios de NO usar <c>HashCode.Combine</c> (semilla aleatoria por proceso) — acá
    /// directamente no aplica, no hay nada que combinar. Tomado ANTES de releer
    /// <c>ofertas_listas</c> (ver <see cref="ActualizarAsync"/>): serializa cualquier otro PUT
    /// concurrente sobre la MISMA oferta, exista o no una fila de targeting todavía.</summary>
    private async Task TomarLockDeOfertaAsync(int idTenant, int idOferta, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idOferta);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    /// <summary>Normaliza a UTC cualquier <see cref="DateTimeOffset"/> antes de escribirlo como
    /// parámetro raw-ADO — la convención de EF no alcanza este camino (ver el doc-comment de
    /// <c>ServicioDePrecios.AgregarParametro</c>, judgment-day juez A).</summary>
    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor is DateTimeOffset dto ? dto.ToUniversalTime() : valor;
        comando.Parameters.Add(parametro);
    }

    private async Task<Oferta> BuscarAsync(int id, CancellationToken ct) =>
        await db.Ofertas.FirstOrDefaultAsync(o => o.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant —
            // esto solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe la oferta {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino
            // alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeOfertas requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    /// <summary>Convierte el <c>int[]</c> del contrato HTTP al <c>short[]</c> nativo de
    /// <see cref="Oferta.DiasSemana"/> (Npgsql <c>smallint[]</c>). El chequeo de rango de
    /// <c>short</c> corre ACÁ, antes del cast, para que un valor fuera de rango nunca dé la
    /// vuelta silenciosamente a un 1..7 que <see cref="ReglaDeOfertas.LeerDiasSemana"/> aceptaría
    /// por error — esa función valida el subset 1..7 DESPUÉS del cast, no el rango del tipo.</summary>
    private static short[]? ConvertirDiasSemana(IReadOnlyList<int>? dias)
    {
        if (dias is null || dias.Count == 0)
        {
            return null;
        }

        var convertidos = new short[dias.Count];
        for (var i = 0; i < dias.Count; i++)
        {
            var valor = dias[i];
            if (valor is < short.MinValue or > short.MaxValue)
            {
                throw new ErrorDominio(
                    "dias_semana_invalidos",
                    "Los días de semana de la oferta tienen que ser valores de 1 a 7 sin repetir.",
                    400);
            }

            convertidos[i] = (short)valor;
        }

        return convertidos;
    }

    private static string NormalizarRequerido(string? valor, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    private static OfertaListado Proyectar(Oferta o, IReadOnlyList<int> idsListas) => new(
        o.Id, o.Nombre, o.IdEmpresa, o.IdArticulo, o.IdGrupo, o.IdCategoria,
        o.FechaDesde, o.FechaHasta, o.HoraDesde, o.HoraHasta,
        o.DiasSemana is null ? Array.Empty<int>() : o.DiasSemana.Select(d => (int)d).ToArray(),
        o.CantidadMinima, o.PrecioUnitario, o.Porcentaje, o.ImporteFijo,
        o.Prioridad, o.Acumulable, o.Activo, idsListas);
}
