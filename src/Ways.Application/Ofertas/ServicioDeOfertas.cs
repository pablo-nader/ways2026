using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
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
/// así que hacen falta dos <c>SaveChangesAsync</c> atómicos entre sí. La edición NO necesita
/// transacción explícita: el replace-set de <c>ofertas_listas</c> (delete-all + insert, ids
/// <c>.Distinct()</c>ed) entra en UN solo <c>SaveChangesAsync</c> — EF ya lo envuelve en una
/// transacción implícita (design: Protection Rules, "Lista set replacement is atomic") — mismo
/// motivo por el que <c>ServicioDeArticulos.ActualizarAsync</c> tampoco abre una explícita, y lo
/// que permite cubrir <see cref="ActualizarAsync"/> completo contra el proveedor InMemory (a
/// diferencia de <see cref="CrearAsync"/>, mismo "transaction-blocked-provider caveat" que
/// <c>ServicioDeArticulosTests</c>).
/// </summary>
public class ServicioDeOfertas(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
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

        // Reemplaza el subconjunto entero (INSERT/DELETE físico, sin historial que preservar —
        // OfertaLista es PK-only, Slice 1): design: Protection Rules, "Lista set replacement is
        // atomic" — UN solo SaveChangesAsync más abajo hace que el delete-all + insert sea
        // atómico entre sí (misma transacción implícita de EF), que es la superficie que
        // pk_ofertas_listas protege bajo dos PUT concurrentes sobre la misma oferta.
        var filasActuales = await db.OfertasListas.Where(ol => ol.IdOferta == id).ToListAsync(ct);
        db.OfertasListas.RemoveRange(filasActuales);

        if (idsListas is { Count: > 0 })
        {
            AgregarFilasDeListas(id, idTenant, idsListas);
        }

        await db.SaveChangesAsync(ct);

        return Proyectar(oferta, (IReadOnlyList<int>?)idsListas ?? Array.Empty<int>());
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila. Las filas de
    /// <see cref="OfertaLista"/> asociadas quedan como están — sin cascada, mismo criterio que
    /// <see cref="Articulos.ServicioDeArticulos.EliminarAsync"/> con
    /// <see cref="Domain.Articulos.ArticuloEmpresa"/>.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var oferta = await BuscarAsync(id, ct);

        var ahora = reloj.Ahora;
        oferta.DeletedAt = ahora;
        oferta.UpdatedAt = ahora;

        await db.SaveChangesAsync(ct);
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
    /// de otro tenant" con el mismo 400, backstop real <c>fk_ofertas_listas_lista_precio</c>.</summary>
    private async Task ExigirListasValidasAsync(IReadOnlyList<int> idsListas, CancellationToken ct)
    {
        foreach (var idLista in idsListas)
        {
            if (!await db.ListasPrecio.AnyAsync(l => l.Id == idLista, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe la lista de precios {idLista}.", 400);
            }
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
