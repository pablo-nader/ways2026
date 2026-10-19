> Parte de [10 — Modelo de datos](../10-modelo-de-datos.md) · 11. Conector MCP (almacén OAuth)

## 11. Conector MCP: almacén OAuth

El servidor de autorización del conector MCP (OpenIddict, ver `docs/07-despliegue.md`) guarda su
cliente, autorizaciones, alcances y tokens en estas cuatro tablas. Son de **plataforma** (doc 09,
categoría global): no tienen `id_tenant` ni RLS, y solo las lee y escribe OpenIddict. El usuario
de Ways de cada conexión viaja en `sujeto` (el `id_usuario`) y en el contenido protegido del token.

Las define `ContextoOAuthDelConector` (`src/Ways.Api/ConectorMcp/`), un contexto aparte de
`WaysDbContext` con migraciones propias (`ConectorMcp/Migraciones`) y su propia tabla de historial,
`historial_migraciones_conector_mcp`. Las migraciones se aplican al arrancar, solo con
`Mcp:Habilitado` encendido. Los tipos y largos de columna son los de las entidades de OpenIddict;
acá solo se renombran.

```sql
aplicaciones_oauth (                     -- [global] un cliente OAuth (hoy, solo el de Claude)
    id_aplicacion_oauth     text NOT NULL,          -- lo genera OpenIddict
    id_cliente              varchar(100) NULL,      -- Mcp:IdDeCliente
    secreto_cliente         text NULL,              -- NULL: cliente público
    tipo_aplicacion         varchar(50) NULL,
    tipo_cliente            varchar(50) NULL,
    tipo_consentimiento     varchar(50) NULL,
    nombre_visible          text NULL,
    nombres_visibles        text NULL,              -- JSON
    claves_json_web         text NULL,              -- JSON
    permisos                text NULL,              -- JSON
    uris_redireccion        text NULL,              -- JSON
    uris_redireccion_cierre_sesion text NULL,       -- JSON
    requisitos              text NULL,              -- JSON
    ajustes                 text NULL,              -- JSON
    propiedades             text NULL,              -- JSON
    token_concurrencia      varchar(50) NULL
);
-- pk_aplicaciones_oauth (id_aplicacion_oauth); ux_aplicaciones_oauth_id_cliente (id_cliente).

autorizaciones_oauth (                   -- [global] el consentimiento de un usuario a un cliente
    id_autorizacion_oauth   text NOT NULL,
    id_aplicacion_oauth     text NULL,
    sujeto                  varchar(400) NULL,      -- usuarios.id_usuario como texto
    estado                  varchar(50) NULL,       -- valid | revoked
    tipo                    varchar(50) NULL,       -- permanent | ad-hoc
    alcances                text NULL,              -- JSON
    fecha_creacion          timestamptz NULL,
    propiedades             text NULL,
    token_concurrencia      varchar(50) NULL
);
-- pk_autorizaciones_oauth; fk_autorizaciones_oauth_aplicacion (id_aplicacion_oauth);
-- ix_autorizaciones_oauth_aplicacion_estado_sujeto_tipo.

alcances_oauth (                         -- [global] hoy vacía: el alcance ways.mcp se registra en memoria
    id_alcance_oauth        text NOT NULL,
    nombre                  varchar(200) NULL,
    nombre_visible          text NULL,
    nombres_visibles        text NULL,
    descripcion             text NULL,
    descripciones           text NULL,
    recursos                text NULL,
    propiedades             text NULL,
    token_concurrencia      varchar(50) NULL
);
-- pk_alcances_oauth; ux_alcances_oauth_nombre (nombre).

tokens_oauth (                           -- [global] códigos, access y refresh tokens emitidos
    id_token_oauth          text NOT NULL,
    id_aplicacion_oauth     text NULL,
    id_autorizacion_oauth   text NULL,
    sujeto                  varchar(400) NULL,
    estado                  varchar(50) NULL,       -- valid | redeemed | revoked | inactive | rejected
    tipo                    varchar(150) NULL,      -- authorization_code | access_token | refresh_token
    id_referencia           varchar(100) NULL,      -- solo tokens por referencia
    contenido               text NULL,              -- solo tokens por referencia
    fecha_creacion          timestamptz NULL,
    fecha_vencimiento       timestamptz NULL,
    fecha_canje             timestamptz NULL,
    propiedades             text NULL,
    token_concurrencia      varchar(50) NULL
);
-- pk_tokens_oauth; fk_tokens_oauth_aplicacion; fk_tokens_oauth_autorizacion;
-- ix_tokens_oauth_aplicacion_estado_sujeto_tipo; ix_tokens_oauth_autorizacion;
-- ux_tokens_oauth_id_referencia (id_referencia).
```

- **Desvíos de los principios del índice:** sin `EntidadBase` (no hay `created_at`/`updated_at`/
  `deleted_at`) y PKs de texto, porque el esquema lo dicta OpenIddict. Las bajas son físicas: las
  hace OpenIddict al revocar o podar, y el arranque al borrar un cliente que ya no es
  `Mcp:IdDeCliente`.
- **Poda:** una vez por día se borran los tokens y las autorizaciones que ya no sirven (vencidos,
  canjeados, revocados o autorizaciones sin tokens) creados hace más de 14 días
  (`PodaDelAlmacenDelConector`).
- Los tokens no se guardan en claro: el access token, el código y el refresh token van protegidos
  con Data Protection, cuyo anillo de claves vive en `"DataProtectionKeys"`.
