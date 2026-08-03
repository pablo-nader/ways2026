using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Application.Articulos;

/// <summary>
/// ABM de artículos + códigos de barra (design decision 1: entidad/servicio dedicados, no
/// <c>ServicioDeCatalogo&lt;T&gt;</c>). Autorización: <c>Politicas.GestionDeCatalogo</c>
/// aplicada en la capa de API — mismo criterio que
/// <see cref="Clientes.ServicioDeClientes"/>/<see cref="Proveedores.ServicioDeProveedores"/>.
///
/// Un <c>ServicioDeCodigosBarra</c> separado no existe a propósito (task 2.3, "implementer's
/// call, document whichever is chosen"): los códigos de barra son una sub-colección del
/// artículo sin autorización/ciclo de vida propio — separarlos en otra clase solo agregaría un
/// segundo servicio inyectado en <c>ArticulosEndpoints</c> sin ningún beneficio de cohesión.
///
/// El alta abre transacción (mismo patrón que <see cref="Clientes.ServicioDeClientes.CrearAsync"/>)
/// porque puede necesitar <see cref="AsignadorDeCodigoInternoArticulo"/> — a diferencia de
/// <see cref="Proveedores.ServicioDeProveedores"/>, que nunca la necesita. La edición NO abre
/// transacción (el <c>codigo_interno</c> no es editable, ver <see cref="EdicionArticulo"/>):
/// mismo motivo por el que <c>ServicioDeArticulosTests</c> sí puede cubrir
/// <see cref="ActualizarAsync"/> completo contra el proveedor InMemory, a diferencia de
/// <see cref="CrearAsync"/> (mismo "transaction-blocked-provider caveat" que
/// <c>ServicioDeClientesTests</c>).
/// </summary>
public class ServicioDeArticulos(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    public async Task<PaginaDe<ArticuloListado>> ListarAsync(
        string? busqueda = null,
        int? idEmpresa = null,
        bool incluirEliminados = false,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Articulos.AsQueryable();

        if (incluirEliminados)
        {
            // Solo la baja lógica: ignorar todos los filtros de un tirón también saltearía
            // el de tenant (ADR-6) — mismo criterio que ServicioDeClientes.ListarAsync.
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        if (idEmpresa is { } idEmp)
        {
            query = query.DisponibleEnEmpresa(db, idEmp);
        }

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            // Columnas citext: el Contains ya es case-insensitive sin ILIKE explícito. El
            // término también busca por codigo_interno y por cualquiera de los codigos_barra
            // del artículo (subquery correlacionada, mismo shape que el EXISTS de
            // DisponibleEnEmpresa).
            var termino = busqueda.Trim();
            query = query.Where(a =>
                a.Nombre.Contains(termino) ||
                a.CodigoInterno.Contains(termino) ||
                db.CodigosBarra.Any(c => c.IdArticulo == a.Id && c.Codigo.Contains(termino)));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(a => a.Nombre)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(a => new ArticuloListado(
                a.Id, a.CodigoInterno, a.Nombre, a.Descripcion, a.IdArea, a.IdCategoria, a.IdMarca,
                a.IdGrupo, a.IdProveedorHabitual, a.IdAlicuotaIva, a.UnidadVenta, a.UnidadesPorBulto,
                a.EsProducto, a.CostoLista, a.DescuentoProveedor, a.CostoNominal, a.DisponibleParaTodas,
                Array.Empty<int>(), a.Activo))
            .ToListAsync(ct);

        return new PaginaDe<ArticuloListado>(items, total, pagina, tamanio);
    }

    public async Task<ArticuloListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(id, ct);

        // judgment-day ronda 1 (item 2): el detalle expone el subset actual — un cliente HTTP
        // necesita esto para armar un PUT de no-op sin perder las filas de articulos_empresas.
        IReadOnlyList<int> idsEmpresas = articulo.DisponibleParaTodas
            ? Array.Empty<int>()
            : await db.ArticulosEmpresas
                .Where(ae => ae.IdArticulo == articulo.Id)
                .Select(ae => ae.IdEmpresa)
                .ToListAsync(ct);

        return Proyectar(articulo, idsEmpresas);
    }

    /// <summary>Asigna <c>codigo_interno</c> de forma atómica (design decision 6) cuando se
    /// omite, dentro de la misma transacción que el INSERT — igual criterio que
    /// <see cref="Clientes.ServicioDeClientes.CrearAsync"/>. Cuando se provee, se valida único
    /// por tenant antes de abrir la transacción (pre-chequeo best-effort, db-error-backstops:
    /// el backstop real es <c>ux_articulos_codigo_interno</c>).</summary>
    public async Task<ArticuloListado> CrearAsync(AltaArticulo datos, CancellationToken ct = default)
    {
        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var descripcion = NormalizarOpcional(datos.Descripcion, "descripcion", null);
        var codigoInterno = NormalizarCodigoInternoOpcional(datos.CodigoInterno);

        ExigirIdRequerido(datos.IdArea, "id_area");
        ExigirIdRequerido(datos.IdAlicuotaIva, "id_alicuota_iva");
        ExigirUnidadesPorBultoValida(datos.UnidadesPorBulto);
        ExigirCostoValido(datos.CostoLista, "costo_lista");
        ExigirCostoValido(datos.CostoNominal, "costo_nominal");
        ExigirDescuentoProveedorValido(datos.DescuentoProveedor);

        await ExigirAreaValidaAsync(datos.IdArea, ct);
        await ExigirCategoriaValidaAsync(datos.IdCategoria, ct);
        await ExigirMarcaValidaAsync(datos.IdMarca, ct);
        await ExigirGrupoValidoAsync(datos.IdGrupo, ct);
        await ExigirProveedorHabitualValidoAsync(datos.IdProveedorHabitual, ct);
        await ExigirAlicuotaIvaValidaAsync(datos.IdAlicuotaIva, ct);

        // judgment-day ronda 1 (item 3): .Distinct() ANTES de validar/insertar — un duplicado
        // en el payload no debe inflar el conteo de "subset presente" ni generar dos INSERT
        // que choquen contra la PK compuesta (defensa en profundidad, ver el mapeo de
        // PK_articulos_empresas en ManejadorDeErrores).
        var idsEmpresas = datos.IdsEmpresas?.Distinct().ToList();

        // El artículo todavía no existe: crear directamente con disponible_para_todas=false
        // sin subconjunto cae bajo la misma regla que restringirlo después de creado (spec:
        // "Restricting availability requires at least one subset row") — la regla valida el
        // ESTADO RESULTANTE, no una transición (ver ReglaDeArticulos).
        ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(datos.DisponibleParaTodas, idsEmpresas?.Count ?? 0);

        if (!datos.DisponibleParaTodas)
        {
            await ExigirEmpresasValidasAsync(idsEmpresas!, ct);
        }

        if (codigoInterno is not null)
        {
            await ExigirCodigoInternoDisponibleAsync(codigoInterno, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var codigoFinal = codigoInterno;
            if (codigoFinal is null)
            {
                await AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync(db, idTenant, ct);
                var numero = await AsignadorDeCodigoInternoArticulo.AsignarSiguienteAsync(db, idTenant, ct);
                codigoFinal = numero.ToString(CultureInfo.InvariantCulture);
            }

            var ahora = reloj.Ahora;
            var articulo = new Articulo
            {
                CodigoInterno = codigoFinal,
                Nombre = nombre,
                Descripcion = descripcion,
                IdArea = datos.IdArea,
                IdCategoria = datos.IdCategoria,
                IdMarca = datos.IdMarca,
                IdGrupo = datos.IdGrupo,
                IdProveedorHabitual = datos.IdProveedorHabitual,
                IdAlicuotaIva = datos.IdAlicuotaIva,
                UnidadVenta = datos.UnidadVenta,
                UnidadesPorBulto = datos.UnidadesPorBulto,
                EsProducto = datos.EsProducto,
                CostoLista = datos.CostoLista,
                DescuentoProveedor = datos.DescuentoProveedor,
                CostoNominal = datos.CostoNominal,
                DisponibleParaTodas = datos.DisponibleParaTodas,
                Activo = datos.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };

            db.Articulos.Add(articulo);
            await db.SaveChangesAsync(ct);

            if (!datos.DisponibleParaTodas && idsEmpresas is { Count: > 0 })
            {
                AgregarFilasDeSubset(articulo.Id, idTenant, idsEmpresas);
                await db.SaveChangesAsync(ct);
            }

            await transaccion.CommitAsync(ct);

            return Proyectar(articulo, datos.DisponibleParaTodas ? Array.Empty<int>() : (IReadOnlyList<int>?)idsEmpresas ?? Array.Empty<int>());
        });
    }

    public async Task<ArticuloListado> ActualizarAsync(int id, EdicionArticulo datos, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(id, ct);

        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var descripcion = NormalizarOpcional(datos.Descripcion, "descripcion", null);

        ExigirIdRequerido(datos.IdArea, "id_area");
        ExigirIdRequerido(datos.IdAlicuotaIva, "id_alicuota_iva");
        ExigirUnidadesPorBultoValida(datos.UnidadesPorBulto);
        ExigirCostoValido(datos.CostoLista, "costo_lista");
        ExigirCostoValido(datos.CostoNominal, "costo_nominal");
        ExigirDescuentoProveedorValido(datos.DescuentoProveedor);

        await ExigirAreaValidaAsync(datos.IdArea, ct);
        await ExigirCategoriaValidaAsync(datos.IdCategoria, ct);
        await ExigirMarcaValidaAsync(datos.IdMarca, ct);
        await ExigirGrupoValidoAsync(datos.IdGrupo, ct);
        await ExigirProveedorHabitualValidoAsync(datos.IdProveedorHabitual, ct);
        await ExigirAlicuotaIvaValidaAsync(datos.IdAlicuotaIva, ct);

        // judgment-day ronda 1 (item 3): mismo dedup que CrearAsync, antes de validar/insertar.
        var idsEmpresas = datos.IdsEmpresas?.Distinct().ToList();

        // judgment-day ronda 1 (root cause de los dos CRITICAL): la regla valida el ESTADO
        // RESULTANTE, no la transición — dispara igual si el artículo YA estaba restringido y
        // se vuelve a guardar sin ninguna fila de subset (false -> false), no solo en el pasaje
        // true -> false. Antes de este fix, un PUT así esquivaba el guard y reventaba con NRE
        // en ExigirEmpresasValidasAsync al iterar datos.IdsEmpresas nulo.
        ReglaDeArticulos.ValidarRestriccionDeDisponibilidad(datos.DisponibleParaTodas, idsEmpresas?.Count ?? 0);

        if (!datos.DisponibleParaTodas)
        {
            await ExigirEmpresasValidasAsync(idsEmpresas!, ct);
        }

        var idTenant = ExigirTenantDeLaSesion();

        articulo.Nombre = nombre;
        articulo.Descripcion = descripcion;
        articulo.IdArea = datos.IdArea;
        articulo.IdCategoria = datos.IdCategoria;
        articulo.IdMarca = datos.IdMarca;
        articulo.IdGrupo = datos.IdGrupo;
        articulo.IdProveedorHabitual = datos.IdProveedorHabitual;
        articulo.IdAlicuotaIva = datos.IdAlicuotaIva;
        articulo.UnidadVenta = datos.UnidadVenta;
        articulo.UnidadesPorBulto = datos.UnidadesPorBulto;
        articulo.EsProducto = datos.EsProducto;
        articulo.CostoLista = datos.CostoLista;
        articulo.DescuentoProveedor = datos.DescuentoProveedor;
        articulo.CostoNominal = datos.CostoNominal;
        articulo.DisponibleParaTodas = datos.DisponibleParaTodas;
        articulo.Activo = datos.Activo;
        articulo.UpdatedAt = reloj.Ahora;

        // Reemplaza el subconjunto entero (INSERT/DELETE físico, sin historial que preservar —
        // ArticuloEmpresa es PK-only, task 1.4): más simple que calcular un delta, y el
        // volumen esperado por artículo es bajo (subconjunto de empresas de UN tenant).
        var filasActuales = await db.ArticulosEmpresas.Where(ae => ae.IdArticulo == id).ToListAsync(ct);
        db.ArticulosEmpresas.RemoveRange(filasActuales);

        if (!datos.DisponibleParaTodas && idsEmpresas is { Count: > 0 })
        {
            AgregarFilasDeSubset(id, idTenant, idsEmpresas);
        }

        await db.SaveChangesAsync(ct);

        return Proyectar(articulo, datos.DisponibleParaTodas ? Array.Empty<int>() : (IReadOnlyList<int>?)idsEmpresas ?? Array.Empty<int>());
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila. Los
    /// <see cref="CodigoBarra"/>/<see cref="ArticuloEmpresa"/> asociados quedan como están —
    /// sin cascada, mismo criterio que <see cref="Proveedores.ServicioDeProveedores.EliminarAsync"/>
    /// (sin guard de fila protegida a diferencia de clientes: artículos no tiene un equivalente
    /// al Consumidor Final).</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(id, ct);

        var ahora = reloj.Ahora;
        articulo.DeletedAt = ahora;
        articulo.UpdatedAt = ahora;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>db-error-backstops: pre-chequeo best-effort — el backstop real sigue siendo
    /// <c>ux_codigos_barra_codigo_tenant</c> (23505 → 409 <c>codigo_barra_duplicado</c>,
    /// <c>ManejadorDeErrores</c>). <see cref="CodigoBarra"/> hereda de
    /// <see cref="Ways.Domain.Common.EntidadTenant"/>: <c>IdTenant</c> se auto-estampa en
    /// <c>SaveChangesAsync</c>, sin necesitar el estampado manual que sí requiere
    /// <see cref="ArticuloEmpresa"/> (task 1.4).</summary>
    public async Task<CodigoBarraListado> AgregarCodigoBarraAsync(
        int idArticulo, AltaCodigoBarra datos, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(idArticulo, ct);

        var codigo = NormalizarRequerido(datos.Codigo, "codigo", 50);

        await ExigirCodigoBarraDisponibleAsync(codigo, ct);

        var ahora = reloj.Ahora;
        var codigoBarra = new CodigoBarra
        {
            IdArticulo = articulo.Id,
            Codigo = codigo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        db.CodigosBarra.Add(codigoBarra);
        await db.SaveChangesAsync(ct);

        return new CodigoBarraListado(codigoBarra.Id, codigoBarra.IdArticulo, codigoBarra.Codigo, codigoBarra.Activo);
    }

    /// <summary>Baja lógica del código de barras — deja el código reutilizable después
    /// (índice parcial <c>WHERE deleted_at IS NULL</c>), mismo criterio que la baja de
    /// <c>cuit</c> en <see cref="Proveedores.ServicioDeProveedores"/>.</summary>
    public async Task EliminarCodigoBarraAsync(int idArticulo, int idCodigoBarra, CancellationToken ct = default)
    {
        // ADR-8: mismo 404 si el artículo no existe o es de otro tenant, antes de buscar el
        // código de barras.
        await BuscarAsync(idArticulo, ct);

        var codigoBarra = await db.CodigosBarra
            .FirstOrDefaultAsync(c => c.Id == idCodigoBarra && c.IdArticulo == idArticulo, ct)
            ?? throw ErrorDominio.NoEncontrado(
                $"No existe el código de barras {idCodigoBarra} del artículo {idArticulo}.");

        var ahora = reloj.Ahora;
        codigoBarra.DeletedAt = ahora;
        codigoBarra.UpdatedAt = ahora;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Spec: Margin-Based Price Suggestion — resuelve <c>margenGrupo</c>/
    /// <c>margenProveedor</c> desde las referencias del artículo y delega el cálculo puro en
    /// <see cref="SugeridorDePrecio"/>. Nunca persiste un <c>precios</c> row (design decision
    /// 8: "called but never auto-applied").</summary>
    public async Task<SugerenciaDePrecio> SugerirPrecioAsync(int idArticulo, CancellationToken ct = default)
    {
        var articulo = await BuscarAsync(idArticulo, ct);

        var margenGrupo = articulo.IdGrupo is { } idGrupo
            ? await db.Grupos.Where(g => g.Id == idGrupo).Select(g => g.Margen).FirstOrDefaultAsync(ct)
            : null;

        var margenProveedor = articulo.IdProveedorHabitual is { } idProveedor
            ? await db.Proveedores.Where(p => p.Id == idProveedor).Select(p => p.Margen).FirstOrDefaultAsync(ct)
            : null;

        var precioSugerido = SugeridorDePrecio.Sugerir(
            articulo.CostoNominal, articulo.CostoLista, articulo.DescuentoProveedor, margenGrupo, margenProveedor);

        return new SugerenciaDePrecio(precioSugerido);
    }

    /// <summary><see cref="ArticuloEmpresa.IdTenant"/> NO se auto-estampa (task 1.4: no hereda
    /// <see cref="Ways.Domain.Common.EntidadTenant"/>) — este es el punto de escritura real que el
    /// comentario de <c>WaysDbContext.AplicarFiltroDeTenantEnArticuloEmpresa</c> dejaba
    /// pendiente para esta slice: hay que asignarlo a mano, o el RLS <c>WITH CHECK</c> rechaza
    /// el INSERT con SQLSTATE 42501.</summary>
    private void AgregarFilasDeSubset(int idArticulo, int idTenant, IReadOnlyList<int> idsEmpresas)
    {
        foreach (var idEmpresa in idsEmpresas)
        {
            db.ArticulosEmpresas.Add(new ArticuloEmpresa
            {
                IdArticulo = idArticulo,
                IdEmpresa = idEmpresa,
                IdTenant = idTenant
            });
        }
    }

    private async Task<Articulo> BuscarAsync(int id, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant —
            // esto solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el artículo {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino
            // alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeArticulos requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    private static void ExigirIdRequerido(int valor, string campo)
    {
        if (valor <= 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }
    }

    /// <summary>Columna <c>numeric(10,2)</c> (migración <c>ArticulosYPreciosEtapa3</c>) — sin
    /// este chequeo, Postgres respondería con 22003 y, para un valor que además viene mal
    /// formado de negocio (negativo), terminaría en un 500 sin el backstop genérico del item
    /// 22003 llegando a cubrirlo del todo (ver <c>ManejadorDeErrores</c>). Mismo criterio de
    /// clase que <c>ServicioDeProveedores.ExigirMargenValido</c>.</summary>
    private static void ExigirUnidadesPorBultoValida(decimal? valor)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0 || v >= 100_000_000m)
        {
            throw new ErrorDominio(
                "unidades_por_bulto_invalido",
                "El campo unidades_por_bulto debe estar entre 0 y 99999999.99.",
                400);
        }
    }

    /// <summary>Aplica a <c>costo_lista</c>/<c>costo_nominal</c>, ambas <c>numeric(14,2)</c> —
    /// mismo bound que <c>ServicioDeClientes.ExigirLimiteCreditoValido</c> (misma precisión de
    /// columna).</summary>
    private static void ExigirCostoValido(decimal? valor, string campo)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0 || v >= 1_000_000_000_000m)
        {
            throw new ErrorDominio(
                $"{campo}_invalido", $"El campo {campo} debe estar entre 0 y 999999999999.99.", 400);
        }
    }

    /// <summary>Columna <c>numeric(5,2)</c> — mismo bound que
    /// <c>ServicioDeProveedores.ExigirMargenValido</c> (misma precisión de columna, misma
    /// familia semántica de "porcentaje").</summary>
    private static void ExigirDescuentoProveedorValido(decimal? valor)
    {
        if (valor is not { } v)
        {
            return;
        }

        if (v < 0 || v >= 1000m)
        {
            throw new ErrorDominio(
                "descuento_proveedor_invalido", "El campo descuento_proveedor debe estar entre 0 y 999.99.", 400);
        }
    }

    /// <summary>db-error-backstops: pre-chequeo de existencia tenant-scoped antes del INSERT —
    /// el backstop real sigue siendo <c>fk_articulos_area</c> (compuesta, 23503 → 400
    /// <c>referencia_invalida</c>, genérico desde la Slice 1).</summary>
    private async Task ExigirAreaValidaAsync(int id, CancellationToken ct)
    {
        if (!await db.Areas.AnyAsync(a => a.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el área {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_categoria</c> — <c>IdCategoria</c> es nullable (spec: Articulo Schema At
    /// Rest).</summary>
    private async Task ExigirCategoriaValidaAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await db.Categorias.AnyAsync(c => c.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la categoría {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_marca</c>.</summary>
    private async Task ExigirMarcaValidaAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await db.Marcas.AnyAsync(m => m.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la marca {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_grupo</c>.</summary>
    private async Task ExigirGrupoValidoAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await db.Grupos.AnyAsync(g => g.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el grupo {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirAreaValidaAsync"/>, para
    /// <c>fk_articulos_proveedor_habitual</c>.</summary>
    private async Task ExigirProveedorHabitualValidoAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await db.Proveedores.AnyAsync(p => p.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el proveedor {id}.", 400);
        }
    }

    /// <summary><c>alicuotas_iva</c> es <c>[global]</c> (no
    /// <see cref="Ways.Domain.Common.EntidadTenant"/>, ADR-11 gate #4): sin alcance de tenant
    /// que filtrar, mismo criterio que
    /// <c>ServicioDeClientes.ExigirCondicionFiscalValidaAsync</c>.</summary>
    private async Task ExigirAlicuotaIvaValidaAsync(int id, CancellationToken ct)
    {
        if (!await db.AlicuotasIva.AnyAsync(a => a.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la alícuota de IVA {id}.", 400);
        }
    }

    /// <summary>Spec: Cross-tenant empresa reference is blocked — pre-chequeo tenant-scoped
    /// (no <c>IgnoreQueryFilters</c>, per db-error-backstops) antes de escribir cualquier fila
    /// de <see cref="ArticuloEmpresa"/>: el filtro de EF ya deja afuera una empresa de otro
    /// tenant de <c>db.Empresas</c>, así que este chequeo cubre "no existe" y "es de otro
    /// tenant" con el mismo 400.</summary>
    private async Task ExigirEmpresasValidasAsync(IReadOnlyList<int> idsEmpresas, CancellationToken ct)
    {
        foreach (var idEmpresa in idsEmpresas)
        {
            if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
            {
                throw new ErrorDominio("referencia_invalida", $"No existe la empresa {idEmpresa}.", 400);
            }
        }
    }

    /// <summary>db-error-backstops: pre-chequeo best-effort — el backstop real sigue siendo
    /// <c>ux_articulos_codigo_interno</c> (23505 → 409 <c>codigo_interno_duplicado</c>,
    /// <c>ManejadorDeErrores</c>). No reemplaza la constraint: dos altas concurrentes con el
    /// mismo <c>codigo_interno</c> pueden pasar las dos este chequeo y competir recién en el
    /// <c>SaveChangesAsync</c> de la que pierde (spec: "Concurrent autogeneration yields no
    /// gaps or duplicates" cubre el camino autogenerado — acá es el camino de un valor
    /// provisto por el cliente HTTP, sin ningún lock de fila que serialice la carrera por
    /// construcción, task 2.8). Sin parámetro <c>excluirId</c> (judgment-day ronda 1, item 4a,
    /// dead code removido): <c>codigo_interno</c> es inmutable (ver <see cref="EdicionArticulo"/>),
    /// así que el único llamador es <see cref="CrearAsync"/>, que nunca necesita excluir su
    /// propio id porque todavía no existe.</summary>
    private async Task ExigirCodigoInternoDisponibleAsync(string codigoInterno, CancellationToken ct)
    {
        var tomado = await db.Articulos.AnyAsync(a => a.CodigoInterno == codigoInterno, ct);

        if (tomado)
        {
            throw ErrorDominio.Conflicto(
                "codigo_interno_duplicado", $"Ya existe un artículo con el código interno {codigoInterno} en este tenant.");
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirCodigoInternoDisponibleAsync"/>, para
    /// <c>ux_codigos_barra_codigo_tenant</c> (23505 → 409 <c>codigo_barra_duplicado</c>, task
    /// 2.9) — sin <c>excluirId</c>: a diferencia del <c>codigo_interno</c> de un artículo (que
    /// se puede volver a guardar con el mismo valor en una edición), agregar un código de
    /// barras siempre es un alta nueva, nunca una edición de una fila existente.</summary>
    private async Task ExigirCodigoBarraDisponibleAsync(string codigo, CancellationToken ct)
    {
        var tomado = await db.CodigosBarra.AnyAsync(c => c.Codigo == codigo, ct);

        if (tomado)
        {
            throw ErrorDominio.Conflicto("codigo_barra_duplicado", $"Ya existe el código de barras {codigo} en este tenant.");
        }
    }

    private static string? NormalizarCodigoInternoOpcional(string? valor)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (limpio.Length > 30)
        {
            throw new ErrorDominio(
                "codigo_interno_muy_largo", "El campo codigo_interno no puede superar los 30 caracteres.", 400);
        }

        return limpio;
    }

    private static string? NormalizarOpcional(string? valor, string campo, int? largoMaximo)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (largoMaximo is { } maximo && limpio.Length > maximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {maximo} caracteres.", 400);
        }

        return limpio;
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

    private static ArticuloListado Proyectar(Articulo a, IReadOnlyList<int> idsEmpresas) => new(
        a.Id, a.CodigoInterno, a.Nombre, a.Descripcion, a.IdArea, a.IdCategoria, a.IdMarca, a.IdGrupo,
        a.IdProveedorHabitual, a.IdAlicuotaIva, a.UnidadVenta, a.UnidadesPorBulto, a.EsProducto,
        a.CostoLista, a.DescuentoProveedor, a.CostoNominal, a.DisponibleParaTodas, idsEmpresas, a.Activo);
}
