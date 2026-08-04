using System.Linq.Expressions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Clientes;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia;

public class WaysDbContext(DbContextOptions<WaysDbContext> options, ITenantActual tenantActual)
    : DbContext(options), IWaysDbContext, IDataProtectionKeyContext
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<PuntoVenta> PuntosVenta => Set<PuntoVenta>();

    // Catálogos de tenant (ADR-11) y globales (ADR-11, gate #4) — sin DbSet en
    // IWaysDbContext todavía: Application los consume recién en la capa de servicios
    // (ServicioDeCatalogo<T>/ServicioDeParametros, tareas 3.15-3.18), que no es parte de
    // este lote (domain + persistence machine, hasta el gate #3).
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Categoria> Categorias => Set<Categoria>();
    public DbSet<Marca> Marcas => Set<Marca>();
    public DbSet<Grupo> Grupos => Set<Grupo>();
    public DbSet<MedioPago> MediosPago => Set<MedioPago>();
    public DbSet<CondicionFiscal> CondicionesFiscales => Set<CondicionFiscal>();
    public DbSet<AlicuotaIva> AlicuotasIva => Set<AlicuotaIva>();
    public DbSet<TipoComprobante> TiposComprobante => Set<TipoComprobante>();
    public DbSet<Parametro> Parametros => Set<Parametro>();

    // Stage 2 (clientes-proveedores, DB CHANGE GATE pendiente): modelo adelantado a la
    // migración, mismo trámite que las 5 catálogos de tenant en stage 1 (ver el comentario
    // de WaysApiFixture.ConfigureWebHost). Los 4 sí están en IWaysDbContext desde este lote:
    // AsignadorDeNumeroCliente/ServicioDeAprovisionamiento/InicializadorDeBaseDeDatos ya los
    // consumen acá (a diferencia de los catálogos de tenant, que no tenían consumidor de
    // Application en su propio lote).
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Proveedor> Proveedores => Set<Proveedor>();
    public DbSet<ListaPrecio> ListasPrecio => Set<ListaPrecio>();
    public DbSet<NumeracionCliente> NumeracionesClientes => Set<NumeracionCliente>();

    // stage-3-articulos-y-precios, Slice 1 (schema/domain foundation, DB CHANGE GATE
    // pendiente): modelo adelantado a la migración, mismo trámite que las catálogos de tenant
    // en stage 1 — sin DbSet en IWaysDbContext todavía, ningún caso de uso de Application los
    // consume en este lote (AsignadorDeCodigoInternoArticulo solo necesita Database/SQL crudo
    // sobre numeraciones_articulos, no un DbSet).
    public DbSet<Articulo> Articulos => Set<Articulo>();
    public DbSet<CodigoBarra> CodigosBarra => Set<CodigoBarra>();
    public DbSet<ArticuloEmpresa> ArticulosEmpresas => Set<ArticuloEmpresa>();
    public DbSet<NumeracionArticulo> NumeracionesArticulos => Set<NumeracionArticulo>();
    public DbSet<Precio> Precios => Set<Precio>();

    // stage-4-ofertas, Slice 2: ServicioDeOfertas es el primer consumidor de Application —
    // los dos DbSet ya están expuestos en IWaysDbContext (Slice 1 solo adelantaba el modelo a
    // la migración, sin consumidor todavía).
    public DbSet<Oferta> Ofertas => Set<Oferta>();
    public DbSet<OfertaLista> OfertasListas => Set<OfertaLista>();

    /// <summary>Referenciado por los query filters de tenant (ver <see cref="AplicarFiltroDeTenant"/>):
    /// EF reconoce el acceso a un miembro de instancia del propio DbContext dentro de un
    /// filtro y lo reata a la instancia que ejecuta cada query, no a la que armó el modelo.</summary>
    internal ITenantActual TenantActual { get; } = tenantActual;

    /// <summary>
    /// Claves de Data Protection, que son las que firman la cookie de sesión.
    /// Viven en la base y no en el sistema de archivos del contenedor: si no,
    /// cada redeploy genera claves nuevas y echa a todos los usuarios logueados.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // citext: comparación de texto case-insensitive a nivel motor.
        // Evita índices sobre lower(columna) para el unique de usuario y mail.
        modelBuilder.HasPostgresExtension("citext");

        // El enum estado_usuario / estado_tenant NO se declara acá: lo registra el
        // MapEnum<T>() de las opciones de Npgsql. Declararlo en los dos lados genera el
        // tipo dos veces en la migración, y con los valores en orden alfabético en vez
        // del orden del enum.

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WaysDbContext).Assembly);

        AplicarFiltroDeBajaLogica(modelBuilder);
        AplicarFiltroDeTenant(modelBuilder);
        AplicarFiltroDeTenantEnTenant(modelBuilder);
        AplicarFiltroDeTenantEnUsuario(modelBuilder);
        AplicarFiltroDeTenantEnNumeracionCliente(modelBuilder);
        AplicarFiltroDeTenantEnNumeracionArticulo(modelBuilder);
        AplicarFiltroDeTenantEnArticuloEmpresa(modelBuilder);
        AplicarFiltroDeTenantEnOfertaLista(modelBuilder);
    }

    /// <summary>
    /// Estampa <c>IdTenant</c> en cada fila nueva y rechaza que se modifique en una
    /// existente: ningún caso de uso lee ni escribe <c>IdTenant</c> a mano (doc 09).
    /// En modo plataforma no se pisa: quien siembra o aprovisiona ya lo setea explícito,
    /// pero se valida que lo haya hecho.
    ///
    /// Los cuatro puntos de entrada públicos de <c>SaveChanges</c> pasan por acá: ninguno
    /// puede saltear el estampado llamando a la variante sync o a la sobrecarga con
    /// <c>acceptAllChangesOnSuccess</c>.
    /// </summary>
    public override int SaveChanges()
    {
        EstamparTenant();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EstamparTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EstamparTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EstamparTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EstamparTenant()
    {
        RechazarEscriturasDeNumeracionCliente();
        RechazarEscriturasDeNumeracionArticulo();

        foreach (var entrada in ChangeTracker.Entries<EntidadTenant>())
        {
            switch (entrada.State)
            {
                case EntityState.Added when !TenantActual.EsPlataforma:
                    entrada.Property(e => e.IdTenant).CurrentValue = TenantActual.Id
                        ?? throw new InvalidOperationException(
                            "No hay tenant en contexto: no se puede insertar una fila scopeada.");
                    break;

                case EntityState.Added when entrada.Entity.IdTenant == 0:
                    throw new InvalidOperationException(
                        "En modo plataforma hay que setear id_tenant explícito antes de insertar.");

                case EntityState.Modified when entrada.Property(e => e.IdTenant).IsModified:
                    throw new InvalidOperationException(
                        "El id_tenant de una fila existente no se puede modificar.");
            }
        }

        // Usuario no hereda de EntidadTenant (ver el comentario de Usuario.IdTenant), así que
        // el loop de arriba no lo alcanza: necesita el mismo rechazo escrito a mano, igual que
        // ya tiene su propio filtro de tenant (AplicarFiltroDeTenantEnUsuario).
        //
        // A propósito no se valida acá el estado Added (a diferencia del loop de EntidadTenant):
        // ServicioDeUsuarios.CrearAsync deriva IdTenant del actor de la identidad de sesión
        // (ActorDeGestion.IdTenant, doc 09 ADR-8), que es un dato de confianza distinto —y
        // deliberadamente separado— del TenantActual de la conexión, así que no hay un valor
        // único contra el cual estampar o validar acá sin duplicar esa lógica de negocio. Además,
        // NULL es un valor legítimo para Usuario.IdTenant en modo plataforma (staff de plataforma
        // y la semilla de root, InicializadorDeBaseDeDatos.SembrarRootAsync), así que ni siquiera
        // se puede reusar el sentinel "IdTenant == 0" del loop de EntidadTenant. RLS
        // (WITH CHECK de usuarios_tenant) es el backstop real para un Added con id_tenant ajeno.
        foreach (var entrada in ChangeTracker.Entries<Usuario>())
        {
            if (entrada.State == EntityState.Modified && entrada.Property(e => e.IdTenant).IsModified)
            {
                throw new InvalidOperationException(
                    "El id_tenant de una fila existente no se puede modificar.");
            }
        }
    }

    /// <summary>
    /// Judgment-day (ronda 1, item de hardening): <see cref="NumeracionCliente"/> documenta
    /// que <see cref="AsignadorDeNumeroCliente"/> es su único punto de
    /// escritura legítimo, y que lo hace con SQL crudo dentro de la transacción del llamador
    /// — nunca vía <c>SaveChangesAsync</c> (design decision 3). Ese contrato dependía
    /// enteramente de que nadie escribiera la entidad por el <c>ChangeTracker</c> por error;
    /// este guard lo convierte en un rechazo explícito, mismo patrón defense-in-depth que
    /// <see cref="EstamparTenant"/> aplica sobre <c>IdTenant</c>: un <c>Added</c>/<c>Modified</c>
    /// de <see cref="NumeracionCliente"/> que llega hasta acá solo puede ser un bypass del
    /// contador atómico (una carrera lo corrompería), así que se frena antes de tocar la base.
    /// </summary>
    private void RechazarEscriturasDeNumeracionCliente()
    {
        foreach (var entrada in ChangeTracker.Entries<NumeracionCliente>())
        {
            if (entrada.State is EntityState.Added or EntityState.Modified)
            {
                throw new InvalidOperationException(
                    "numeraciones_clientes solo se escribe con SQL crudo, vía " +
                    $"{nameof(AsignadorDeNumeroCliente)} — nunca por " +
                    $"{nameof(SaveChanges)}/{nameof(SaveChangesAsync)}.");
            }
        }
    }

    /// <summary>
    /// stage-3-articulos-y-precios (design decision 6): mismo guard que
    /// <see cref="RechazarEscriturasDeNumeracionCliente"/>, acá para
    /// <see cref="NumeracionArticulo"/> — <see cref="AsignadorDeCodigoInternoArticulo"/> es su
    /// único punto de escritura legítimo, con SQL crudo.
    /// </summary>
    private void RechazarEscriturasDeNumeracionArticulo()
    {
        foreach (var entrada in ChangeTracker.Entries<NumeracionArticulo>())
        {
            if (entrada.State is EntityState.Added or EntityState.Modified)
            {
                throw new InvalidOperationException(
                    "numeraciones_articulos solo se escribe con SQL crudo, vía " +
                    $"{nameof(AsignadorDeCodigoInternoArticulo)} — nunca por " +
                    $"{nameof(SaveChanges)}/{nameof(SaveChangesAsync)}.");
            }
        }
    }

    /// <summary>
    /// Toda entidad que hereda de <see cref="EntidadBase"/> filtra las bajas lógicas
    /// automáticamente, bajo la clave <c>"BajaLogica"</c>. Para verlas hay que pedir
    /// <c>IgnoreQueryFilters(["BajaLogica"])</c> explícitamente — así no se arrastra
    /// también el filtro de tenant (ADR-6).
    /// </summary>
    private static void AplicarFiltroDeBajaLogica(ModelBuilder modelBuilder)
    {
        foreach (var entidad in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(EntidadBase).IsAssignableFrom(entidad.ClrType))
            {
                continue;
            }

            if (entidad.ClrType == typeof(DataProtectionKey))
            {
                continue;
            }

            var parametro = Expression.Parameter(entidad.ClrType, "e");
            var propiedad = Expression.Property(parametro, nameof(EntidadBase.DeletedAt));
            var comparacion = Expression.Equal(
                propiedad, Expression.Constant(null, typeof(DateTimeOffset?)));

            entidad.SetQueryFilter("BajaLogica", Expression.Lambda(comparacion, parametro));
        }
    }

    /// <summary>
    /// Toda entidad que hereda de <see cref="EntidadTenant"/> filtra por tenant bajo la
    /// clave <c>"Tenant"</c> (ADR-1, ADR-6): plataforma ve todo, un tenant solo lo suyo.
    /// <c>IgnoreQueryFilters(["Tenant"])</c> lo saltea sin tocar la baja lógica — solo
    /// tiene sentido bajo una sesión de plataforma; RLS es quien realmente lo impide.
    /// </summary>
    private void AplicarFiltroDeTenant(ModelBuilder modelBuilder)
    {
        foreach (var entidad in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(EntidadTenant).IsAssignableFrom(entidad.ClrType))
            {
                continue;
            }

            var parametro = Expression.Parameter(entidad.ClrType, "e");
            var propiedadIdTenant = Expression.Property(parametro, nameof(EntidadTenant.IdTenant));
            var filtro = ConstruirFiltroDeTenant(parametro, propiedadIdTenant);

            entidad.SetQueryFilter("Tenant", filtro);
        }
    }

    /// <summary>
    /// <see cref="Tenant"/> no hereda de <see cref="EntidadTenant"/> (su propia <c>Id</c>
    /// ES el alcance, ADR-1), así que necesita la variante escrita a mano en vez de la
    /// del loop por convención de <see cref="AplicarFiltroDeTenant"/>.
    /// </summary>
    private void AplicarFiltroDeTenantEnTenant(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(Tenant))!;

        var parametro = Expression.Parameter(typeof(Tenant), "e");
        var propiedadId = Expression.Property(parametro, nameof(Tenant.Id));
        var filtro = ConstruirFiltroDeTenant(parametro, propiedadId);

        entidad.SetQueryFilter("Tenant", filtro);
    }

    /// <summary>
    /// <see cref="Usuario"/> no hereda de <see cref="EntidadTenant"/> (su <c>IdTenant</c> es
    /// nullable = plataforma, ADR-1), así que necesita su propia variante en vez de la del
    /// loop de <see cref="AplicarFiltroDeTenant"/>: además de "plataforma o mismo tenant",
    /// también deja pasar todo en modo <see cref="ModoDeAcceso.Login"/> — el único momento
    /// en que se busca una cuenta por <c>mail</c> sin tenant resuelto todavía, exactamente
    /// lo que permiten las policies <c>usuarios_login_lectura</c>/<c>_actualiza</c> del lado
    /// de RLS (gate #2 pendiente). Sin esa rama, un login de un usuario de tenant nunca
    /// encontraría la fila: el filtro compararía <c>IdTenant</c> contra un
    /// <see cref="ITenantActual.Id"/> que en modo login es <c>null</c>.
    /// </summary>
    private void AplicarFiltroDeTenantEnUsuario(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(Usuario))!;

        var parametro = Expression.Parameter(typeof(Usuario), "e");
        var propiedadIdTenant = Expression.Property(parametro, nameof(Usuario.IdTenant));

        var contexto = Expression.Constant(this, typeof(WaysDbContext));
        var tenantActualDelContexto = Expression.Property(contexto, nameof(TenantActual));

        var esPlataforma = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.EsPlataforma));
        var modo = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.Modo));
        var esLogin = Expression.Equal(modo, Expression.Constant(ModoDeAcceso.Login));
        var esTenant = Expression.Equal(modo, Expression.Constant(ModoDeAcceso.Tenant));
        var idDelContexto = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.Id));

        // `esTenant &&` es obligatorio acá y no en ConstruirFiltroDeTenant: ahí el lado
        // de la comparación nunca es NULL (id de un tenant real), así que un
        // TenantActual.Id nulo (modo Ninguno, fail-closed) jamás iguala por accidente. Acá
        // sí puede — Usuario.IdTenant también es NULL para plataforma — así que sin este
        // guard, modo Ninguno (Id nulo) terminaría viendo las cuentas de plataforma
        // (NULL == NULL) en vez de fallar cerrado.
        var comparacion = Expression.AndAlso(esTenant, Expression.Equal(propiedadIdTenant, idDelContexto));

        var filtro = Expression.OrElse(Expression.OrElse(esPlataforma, esLogin), comparacion);

        entidad.SetQueryFilter("Tenant", Expression.Lambda(filtro, parametro));
    }

    /// <summary>
    /// <see cref="NumeracionCliente"/> no hereda de <see cref="EntidadTenant"/> (su
    /// <c>IdTenant</c> ES la PK, no una FK opcional — mismo motivo que <see cref="Tenant"/>),
    /// así que necesita la variante escrita a mano en vez de la del loop de
    /// <see cref="AplicarFiltroDeTenant"/>.
    /// </summary>
    private void AplicarFiltroDeTenantEnNumeracionCliente(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(NumeracionCliente))!;

        var parametro = Expression.Parameter(typeof(NumeracionCliente), "e");
        var propiedadIdTenant = Expression.Property(parametro, nameof(NumeracionCliente.IdTenant));
        var filtro = ConstruirFiltroDeTenant(parametro, propiedadIdTenant);

        entidad.SetQueryFilter("Tenant", filtro);
    }

    /// <summary>
    /// <see cref="NumeracionArticulo"/> no hereda de <see cref="EntidadTenant"/> (mismo motivo
    /// que <see cref="NumeracionCliente"/>), así que necesita la variante escrita a mano.
    /// </summary>
    private void AplicarFiltroDeTenantEnNumeracionArticulo(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(NumeracionArticulo))!;

        var parametro = Expression.Parameter(typeof(NumeracionArticulo), "e");
        var propiedadIdTenant = Expression.Property(parametro, nameof(NumeracionArticulo.IdTenant));
        var filtro = ConstruirFiltroDeTenant(parametro, propiedadIdTenant);

        entidad.SetQueryFilter("Tenant", filtro);
    }

    /// <summary>
    /// <see cref="ArticuloEmpresa"/> no hereda de <see cref="EntidadTenant"/> (task 1.4:
    /// junction PK-only, sin baja lógica), así que el loop por convención de
    /// <see cref="AplicarFiltroDeTenant"/> no la alcanza — necesita la misma variante escrita
    /// a mano que <see cref="AplicarFiltroDeTenantEnNumeracionCliente"/>/
    /// <see cref="AplicarFiltroDeTenantEnNumeracionArticulo"/>, con la diferencia de que acá SÍ
    /// se escribe por <c>SaveChangesAsync</c> normal (no hay guard de rechazo: a diferencia de
    /// los contadores, esta tabla no tiene un asignador atómico que proteger).
    ///
    /// <see cref="ArticuloEmpresa.IdTenant"/> tampoco se auto-estampa acá — al no heredar de
    /// <see cref="EntidadTenant"/>, queda fuera del interceptor que completa <c>IdTenant</c> por
    /// convención; quien construya la fila DEBE asignarlo, y el RLS <c>WITH CHECK</c> rechaza el
    /// INSERT con SQLSTATE 42501 si falta. Pendiente para Slice 2 cuando aterrice el camino de
    /// escritura real (ServicioDeArticulos).
    /// </summary>
    private void AplicarFiltroDeTenantEnArticuloEmpresa(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(ArticuloEmpresa))!;

        var parametro = Expression.Parameter(typeof(ArticuloEmpresa), "e");
        var propiedadIdTenant = Expression.Property(parametro, nameof(ArticuloEmpresa.IdTenant));
        var filtro = ConstruirFiltroDeTenant(parametro, propiedadIdTenant);

        entidad.SetQueryFilter("Tenant", filtro);
    }

    /// <summary>
    /// <see cref="OfertaLista"/> no hereda de <see cref="EntidadTenant"/> (junction PK-only,
    /// mismo motivo que <see cref="ArticuloEmpresa"/>), así que necesita la misma variante
    /// escrita a mano que <see cref="AplicarFiltroDeTenantEnArticuloEmpresa"/>.
    ///
    /// <see cref="OfertaLista.IdTenant"/> tampoco se auto-estampa acá — quien construya la fila
    /// DEBE asignarlo, y el RLS <c>WITH CHECK</c> rechaza el INSERT con SQLSTATE 42501 si
    /// falta. Pendiente para Slice 2 cuando aterrice el camino de escritura real
    /// (<c>ServicioDeOfertas</c>, replace-set de <c>ofertas_listas</c>).
    /// </summary>
    private void AplicarFiltroDeTenantEnOfertaLista(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(OfertaLista))!;

        var parametro = Expression.Parameter(typeof(OfertaLista), "e");
        var propiedadIdTenant = Expression.Property(parametro, nameof(OfertaLista.IdTenant));
        var filtro = ConstruirFiltroDeTenant(parametro, propiedadIdTenant);

        entidad.SetQueryFilter("Tenant", filtro);
    }

    /// <summary><c>e => this.TenantActual.EsPlataforma || propiedadDeAlcance == this.TenantActual.Id</c>.
    /// <c>Expression.Constant(this, typeof(WaysDbContext))</c> es lo que EF reconoce como
    /// acceso a la instancia en ejecución, no un valor fijo capturado al armar el modelo.</summary>
    private LambdaExpression ConstruirFiltroDeTenant(ParameterExpression parametro, Expression propiedadDeAlcance)
    {
        var contexto = Expression.Constant(this, typeof(WaysDbContext));
        var tenantActualDelContexto = Expression.Property(contexto, nameof(TenantActual));

        var esPlataforma = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.EsPlataforma));
        var idDelContexto = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.Id));

        var alcanceComoNullable = propiedadDeAlcance.Type == typeof(int?)
            ? propiedadDeAlcance
            : Expression.Convert(propiedadDeAlcance, typeof(int?));

        var comparacion = Expression.Equal(alcanceComoNullable, idDelContexto);

        return Expression.Lambda(Expression.OrElse(esPlataforma, comparacion), parametro);
    }
}
