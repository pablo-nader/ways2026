// Conector MCP (experimental): prueba de punta a punta del handshake OAuth + MCP contra la API de Ways.
//
// Uso: node src/Ways.Api/scripts/probar-conector-mcp.mjs [urlBase] [urlPublica] [--solo-publico]
//   urlBase: adonde se mandan las requests (por defecto http://localhost:5080).
//   urlPublica: la Mcp:UrlPublica con la que corre la API, si es distinta de urlBase; las URLs
//     públicas que devuelve el servidor se traducen a urlBase antes de pedirlas.
//   --solo-publico: solo las verificaciones sin credenciales (desafío 401, documentos de
//     descubrimiento, página de consentimiento y solicitudes inválidas). Es el modo para producción.
//
// El modo completo lee las credenciales de variables de entorno, nunca de un archivo:
//   WAYS_MCP_MAIL / WAYS_MCP_PASSWORD: un usuario de tenant incluido en Mcp:MailsHabilitados.
//   WAYS_MCP_TENANT (opcional): nombre del tenant que tiene que mostrar quien_soy.
//   WAYS_MCP_MAIL_PLATAFORMA / WAYS_MCP_PASSWORD_PLATAFORMA (opcionales): el usuario de plataforma,
//     para verificar que no puede autorizar aunque esté en la lista.
// La verificación de reuso del refresh token espera que la API corra con
// Mcp__SegundosDeToleranciaDeReusoDeRefresh=0.
//
// Sin dependencias (Node 18+). Nunca imprime tokens, códigos, contraseñas ni el header Authorization.

import { createHash, randomBytes } from 'node:crypto';

const argumentos = process.argv.slice(2);
const SOLO_PUBLICO = argumentos.includes('--solo-publico');
const [baseArgumento, publicaArgumento] = argumentos.filter((a) => !a.startsWith('--'));
const BASE = (baseArgumento ?? 'http://localhost:5080').replace(/\/+$/, '');
const PUBLICA = (publicaArgumento ?? BASE).replace(/\/+$/, '');
const URL_MCP = `${PUBLICA}/mcp`;
const ID_CLIENTE = process.env.WAYS_MCP_CLIENTE ?? 'claude-ways';
const REDIRECCION = 'https://claude.ai/api/mcp/auth_callback';
const ALCANCES = 'ways.mcp offline_access';
const CAMPO_ANTIFORGERY = '__RequestVerificationToken';
const PROTOCOLO_2025 = '2025-11-25';
const PROTOCOLO_2026 = '2026-07-28';

const resultados = [];

function registrar(estado, nombre, detalle) {
  resultados.push({ estado, nombre });
  console.log(`${estado.padEnd(4)} ${nombre}${detalle ? ` -- ${detalle}` : ''}`);
}

const verificar = (nombre, condicion, detalle) => registrar(condicion ? 'PASS' : 'FAIL', nombre, detalle);
const informar = (nombre, detalle) => registrar('INFO', nombre, detalle);
const seccion = (titulo) => console.log(`\n=== ${titulo} ===`);
const b64url = (buffer) => Buffer.from(buffer).toString('base64url');

function pkce() {
  const verificador = b64url(randomBytes(32));
  const desafio = b64url(createHash('sha256').update(verificador).digest());
  return { verificador, desafio };
}

function aLocal(url) {
  const destino = new URL(url);
  if (destino.origin !== new URL(PUBLICA).origin) return url;
  return `${BASE}${destino.pathname}${destino.search}`;
}

async function pedir(url, opciones = {}) {
  const inicio = Date.now();
  const respuesta = await fetch(aLocal(url), { redirect: 'manual', signal: AbortSignal.timeout(15000), ...opciones });
  respuesta.ms = Date.now() - inicio;
  return respuesta;
}

const FORMULARIO = { 'content-type': 'application/x-www-form-urlencoded' };

async function leerJson(respuesta) {
  try {
    return JSON.parse(await respuesta.text());
  } catch {
    return null;
  }
}

function decodificarEntidades(texto) {
  return texto
    .replace(/&#x([0-9a-f]+);/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, dec) => String.fromCodePoint(parseInt(dec, 10)))
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');
}

const extraerOcultos = (html) =>
  [...html.matchAll(/<input type="hidden" name="([^"]*)" value="([^"]*)">/g)].map(([, nombre, valor]) => [
    decodificarEntidades(nombre),
    decodificarEntidades(valor),
  ]);

// fetch no guarda cookies: la de antiforgery que deja la página se reenvía a mano en el POST.
const cookiesDe = (respuesta) =>
  (respuesta.headers.getSetCookie?.() ?? []).map((cookie) => cookie.split(';')[0]).join('; ');

function parsearMensajes(texto, tipo) {
  if (!texto) return [];
  if ((tipo ?? '').includes('text/event-stream')) {
    return texto
      .split(/\r?\n/)
      .filter((linea) => linea.startsWith('data:'))
      .map((linea) => {
        try {
          return JSON.parse(linea.slice(5).trim());
        } catch {
          return { crudo: linea };
        }
      });
  }
  try {
    const json = JSON.parse(texto);
    return Array.isArray(json) ? json : [json];
  } catch {
    return [{ crudo: texto.slice(0, 200) }];
  }
}

async function llamarMcp(token, mensaje, encabezados = {}) {
  const respuesta = await pedir(URL_MCP, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      accept: 'application/json, text/event-stream',
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...encabezados,
    },
    body: JSON.stringify(mensaje),
  });
  const texto = await respuesta.text();
  return { respuesta, mensajes: parsearMensajes(texto, respuesta.headers.get('content-type')) };
}

const resumirError = (mensaje) =>
  mensaje?.error ? `error ${mensaje.error.code}: ${mensaje.error.message}` : 'sin error JSON-RPC';

function credenciales(prefijo, sufijo = '') {
  const mail = process.env[`${prefijo}MAIL${sufijo}`];
  const password = process.env[`${prefijo}PASSWORD${sufijo}`];
  return mail && password ? { mail, password } : null;
}

function urlDeAutorizacion(metadata, desafio, cambios = {}) {
  const parametros = new URLSearchParams({
    response_type: 'code',
    client_id: ID_CLIENTE,
    redirect_uri: REDIRECCION,
    scope: ALCANCES,
    state: b64url(randomBytes(16)),
    code_challenge: desafio,
    code_challenge_method: 'S256',
    resource: URL_MCP,
    ...cambios,
  });
  for (const [clave, valor] of Object.entries(cambios)) {
    if (valor === undefined) parametros.delete(clave);
  }
  return `${metadata.authorization_endpoint}?${parametros}`;
}

// OpenIddict redirige el error al cliente solo cuando ya validó la redirect_uri; los errores
// detectados antes (PKCE, resource) vuelven como 400 local con cuerpo "error:<código>".
async function errorDeAutorizacion(respuesta) {
  const ubicacion = respuesta.headers.get('location');
  if (ubicacion) return `${new URL(ubicacion).searchParams.get('error')} (redirigido)`;
  const cuerpo = await respuesta.text();
  return `${/^error:\s*(\S+)/m.exec(cuerpo)?.[1] ?? '?'} (local)`;
}

// (a) y (b): desafío 401 y documentos de descubrimiento.
async function descubrir() {
  seccion('(a) /mcp sin token');
  const { respuesta } = await llamarMcp(null, { jsonrpc: '2.0', id: 1, method: 'tools/list' });
  const desafio = respuesta.headers.get('www-authenticate') ?? '';
  verificar('POST /mcp sin token responde 401', respuesta.status === 401, `status ${respuesta.status}`);
  const metadataUrl = /resource_metadata="([^"]+)"/.exec(desafio)?.[1];
  verificar('WWW-Authenticate incluye resource_metadata', Boolean(metadataUrl), desafio || '(sin header)');
  verificar('resource_metadata apunta a la base pública', metadataUrl?.startsWith(`${PUBLICA}/`) ?? false);

  const rGet = await pedir(URL_MCP, { headers: { accept: 'text/event-stream' } });
  verificar('GET /mcp sin token responde 401', rGet.status === 401, `status ${rGet.status}`);

  seccion('(b) Metadata del recurso protegido (RFC 9728)');
  const rPrm = await pedir(metadataUrl ?? `${PUBLICA}/.well-known/oauth-protected-resource/mcp`);
  const prm = await leerJson(rPrm);
  verificar('PRM responde 200 con JSON', rPrm.status === 200 && prm !== null, `status ${rPrm.status}, ${rPrm.ms} ms`);
  verificar('PRM.resource es exactamente la URL MCP', prm?.resource === URL_MCP, `resource=${prm?.resource}`);
  informar('PRM campos', Object.keys(prm ?? {}).join(', '));

  const rPrmRaiz = await pedir(`${PUBLICA}/.well-known/oauth-protected-resource`);
  informar('PRM en la raíz', `status ${rPrmRaiz.status}, resource=${(await leerJson(rPrmRaiz))?.resource}`);

  seccion('(b) Metadata del servidor de autorización');
  const emisorPrm = prm?.authorization_servers?.[0] ?? `${PUBLICA}/`;
  const origen = new URL(emisorPrm);
  const rutaEmisor = origen.pathname.replace(/\/+$/, '');
  const candidatos = {
    'RFC 8414 /.well-known/oauth-authorization-server': `${origen.origin}/.well-known/oauth-authorization-server${rutaEmisor}`,
    'OIDC /.well-known/openid-configuration': `${origen.origin}${rutaEmisor}/.well-known/openid-configuration`,
  };

  let metadata = null;
  for (const [nombre, url] of Object.entries(candidatos)) {
    const r = await pedir(url);
    const json = await leerJson(r);
    informar(`AS discovery ${nombre}`, `status ${r.status}, ${r.ms} ms${json ? '' : ', sin JSON'}`);
    metadata ??= r.status === 200 ? json : null;
  }

  verificar('Al menos un documento de AS responde', metadata !== null);
  if (!metadata) return null;

  const requeridos = ['issuer', 'authorization_endpoint', 'token_endpoint', 'code_challenge_methods_supported', 'grant_types_supported', 'response_types_supported', 'scopes_supported'];
  const faltantes = requeridos.filter((campo) => metadata[campo] === undefined);
  verificar('AS tiene todos los campos requeridos por Claude', faltantes.length === 0, faltantes.length ? `faltan: ${faltantes.join(', ')}` : undefined);
  verificar('AS.issuer == PRM.authorization_servers[0]', metadata.issuer === emisorPrm, `issuer=${metadata.issuer}`);
  verificar('code_challenge_methods_supported es solo S256', JSON.stringify(metadata.code_challenge_methods_supported) === '["S256"]', JSON.stringify(metadata.code_challenge_methods_supported));
  verificar('grant_types_supported incluye authorization_code y refresh_token', ['authorization_code', 'refresh_token'].every((g) => metadata.grant_types_supported?.includes(g)), JSON.stringify(metadata.grant_types_supported));
  verificar('response_types_supported incluye code', metadata.response_types_supported?.includes('code'), JSON.stringify(metadata.response_types_supported));
  verificar('scopes_supported incluye ways.mcp y offline_access', ['ways.mcp', 'offline_access'].every((s) => metadata.scopes_supported?.includes(s)), JSON.stringify(metadata.scopes_supported));
  verificar('authorization_endpoint y token_endpoint usan la base pública', [metadata.authorization_endpoint, metadata.token_endpoint].every((url) => url?.startsWith(`${PUBLICA}/`)));
  informar('token_endpoint_auth_methods_supported', JSON.stringify(metadata.token_endpoint_auth_methods_supported));

  const rDesconocido = await pedir(`${PUBLICA}/.well-known/no-existe`);
  verificar('una ruta /.well-known desconocida responde 404', rDesconocido.status === 404, `status ${rDesconocido.status}`);
  return metadata;
}

// Solicitudes de autorización sin credenciales: la página válida y las inválidas.
async function probarSolicitudesSinCredenciales(metadata) {
  seccion('Página de consentimiento y solicitudes inválidas (sin credenciales)');
  const rPagina = await pedir(urlDeAutorizacion(metadata, pkce().desafio));
  const html = await rPagina.text();
  verificar('GET authorize muestra la página de consentimiento', rPagina.status === 200 && html.includes('<form'), `status ${rPagina.status}`);
  const csp = rPagina.headers.get('content-security-policy') ?? '';
  verificar('la página prohíbe el embebido (X-Frame-Options y frame-ancestors)', rPagina.headers.get('x-frame-options') === 'DENY' && csp.includes("frame-ancestors 'none'"));
  verificar('la CSP no restringe form-action', !csp.includes('form-action'), csp);
  verificar('la página no se cachea ni manda Referer', rPagina.headers.get('cache-control') === 'no-store' && rPagina.headers.get('referrer-policy') === 'no-referrer');
  verificar('el formulario lleva token antiforgery', extraerOcultos(html).some(([nombre]) => nombre === CAMPO_ANTIFORGERY));

  const casos = [
    ['redirect_uri no registrada', { redirect_uri: 'https://atacante.example/callback' }],
    ['sin PKCE', { code_challenge: undefined, code_challenge_method: undefined }],
    ['PKCE plain', { code_challenge_method: 'plain' }],
    ['resource desconocido', { resource: 'https://otro.example/mcp' }],
  ];
  for (const [nombre, cambios] of casos) {
    const respuesta = await pedir(urlDeAutorizacion(metadata, pkce().desafio, cambios));
    const error = await errorDeAutorizacion(respuesta);
    verificar(`${nombre} -> rechazada sin mostrar la página`, respuesta.status !== 200 && !error.startsWith('?'), `status ${respuesta.status}, error=${error}`);
  }
}

// (c): código de autorización + PKCE con la redirect_uri de Claude, y canje por tokens.
async function pedirCodigo(metadata, credencialesDelUsuario, conAntiforgery = true) {
  const { verificador, desafio } = pkce();
  const rPagina = await pedir(urlDeAutorizacion(metadata, desafio));
  const html = await rPagina.text();
  const ocultos = extraerOcultos(html).filter(([nombre]) => conAntiforgery || nombre !== CAMPO_ANTIFORGERY);
  const estado = ocultos.find(([nombre]) => nombre === 'state')?.[1];
  const respuesta = await pedir(metadata.authorization_endpoint, {
    method: 'POST',
    headers: { ...FORMULARIO, cookie: cookiesDe(rPagina) },
    body: new URLSearchParams([...ocultos, ['mail', credencialesDelUsuario.mail], ['password', credencialesDelUsuario.password], ['accion', 'aprobar']]),
  });
  const ubicacion = respuesta.headers.get('location') ?? '';
  const destino = ubicacion.startsWith(`${REDIRECCION}?`) ? new URL(ubicacion) : null;
  return { respuesta, destino, codigo: destino?.searchParams.get('code'), estado, verificador };
}

async function autorizar(metadata, credencialesDelUsuario) {
  seccion('(c) Código de autorización + PKCE');
  const { respuesta, destino, codigo, estado, verificador } = await pedirCodigo(metadata, credencialesDelUsuario);
  verificar('aprobar redirige (302) a la redirect_uri de Claude con code', respuesta.status === 302 && Boolean(codigo), `status ${respuesta.status}`);
  if (!codigo) return null;

  verificar('la redirección devuelve el mismo state', destino.searchParams.get('state') === estado);
  verificar('iss de la respuesta de autorización (RFC 9207) == issuer', destino.searchParams.get('iss') === metadata.issuer, `iss=${destino.searchParams.get('iss')}`);

  const rToken = await pedir(metadata.token_endpoint, {
    method: 'POST',
    headers: FORMULARIO,
    body: new URLSearchParams({ grant_type: 'authorization_code', code: codigo, redirect_uri: REDIRECCION, client_id: ID_CLIENTE, code_verifier: verificador, resource: URL_MCP }),
  });
  const tokens = await leerJson(rToken);
  verificar('canje del código responde 200 con access y refresh token', rToken.status === 200 && Boolean(tokens?.access_token) && Boolean(tokens?.refresh_token), `status ${rToken.status}, ${rToken.ms} ms, token_type=${tokens?.token_type}, expires_in=${tokens?.expires_in}, scope=${tokens?.scope}${tokens?.error ? `, error=${tokens.error}` : ''}`);
  if (!tokens?.access_token) return null;

  informar('formato del access token', `${String(tokens.access_token).split('.').length} segmentos (5 = JWE cifrado, opaco para el cliente)`);
  return tokens;
}

// (d): handshake MCP 2025-11-25 y flujo 2026-07-28 con el bearer.
async function probarMcp(token) {
  seccion('(d) MCP con el bearer');
  const comun = { 'mcp-protocol-version': PROTOCOLO_2025 };

  const inicio = await llamarMcp(token, {
    jsonrpc: '2.0',
    id: 1,
    method: 'initialize',
    params: { protocolVersion: PROTOCOLO_2025, capabilities: {}, clientInfo: { name: 'probar-conector-mcp', version: '0.1' } },
  });
  const resultadoInicio = inicio.mensajes.find((m) => m.id === 1)?.result;
  verificar('2025-11-25 initialize responde', inicio.respuesta.status === 200 && Boolean(resultadoInicio), `status ${inicio.respuesta.status}, protocolVersion=${resultadoInicio?.protocolVersion}, serverInfo=${JSON.stringify(resultadoInicio?.serverInfo)}`);

  const notificacion = await llamarMcp(token, { jsonrpc: '2.0', method: 'notifications/initialized' }, comun);
  verificar('notifications/initialized responde 202', notificacion.respuesta.status === 202, `status ${notificacion.respuesta.status}`);

  const lista = await llamarMcp(token, { jsonrpc: '2.0', id: 2, method: 'tools/list' }, comun);
  const herramienta = lista.mensajes.find((m) => m.id === 2)?.result?.tools?.find((t) => t.name === 'quien_soy');
  verificar('tools/list incluye quien_soy', Boolean(herramienta), `title=${herramienta?.title}, annotations=${JSON.stringify(herramienta?.annotations)}`);

  const llamada = await llamarMcp(token, { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'quien_soy', arguments: {} } }, comun);
  const texto = llamada.mensajes.find((m) => m.id === 3)?.result?.content?.find((c) => c.type === 'text')?.text ?? '';
  verificar('tools/call quien_soy devuelve texto', llamada.respuesta.status === 200 && texto.length > 0, `status ${llamada.respuesta.status}`);
  if (process.env.WAYS_MCP_TENANT) {
    verificar('quien_soy muestra el tenant esperado', texto.includes(process.env.WAYS_MCP_TENANT));
  }
  console.log(texto.split('\n').map((linea) => `       | ${linea}`).join('\n'));

  const meta = {
    'io.modelcontextprotocol/protocolVersion': PROTOCOLO_2026,
    'io.modelcontextprotocol/clientCapabilities': {},
    'io.modelcontextprotocol/clientInfo': { name: 'probar-conector-mcp', version: '0.1' },
  };
  const encabezados2026 = (metodo, nombre) => ({ 'mcp-protocol-version': PROTOCOLO_2026, 'mcp-method': metodo, ...(nombre ? { 'mcp-name': nombre } : {}) });

  const descubrimiento = await llamarMcp(token, { jsonrpc: '2.0', id: 10, method: 'server/discover', params: { _meta: meta } }, encabezados2026('server/discover'));
  const resultadoDescubrimiento = descubrimiento.mensajes.find((m) => m.id === 10);
  verificar('2026-07-28 server/discover responde', descubrimiento.respuesta.status === 200 && Boolean(resultadoDescubrimiento?.result), `status ${descubrimiento.respuesta.status}, ${resumirError(resultadoDescubrimiento)}`);
  informar('server/discover: versiones y servidor', `${JSON.stringify(resultadoDescubrimiento?.result?.supportedVersions)} ${JSON.stringify(resultadoDescubrimiento?.result?._meta?.['io.modelcontextprotocol/serverInfo'])}`);

  const llamada2026 = await llamarMcp(token, { jsonrpc: '2.0', id: 12, method: 'tools/call', params: { name: 'quien_soy', arguments: {}, _meta: meta } }, encabezados2026('tools/call', 'quien_soy'));
  const resultado2026 = llamada2026.mensajes.find((m) => m.id === 12);
  verificar('2026-07-28 tools/call quien_soy devuelve texto', llamada2026.respuesta.status === 200 && Boolean(resultado2026?.result?.content?.[0]?.text), `status ${llamada2026.respuesta.status}, ${resumirError(resultado2026)}`);

  const rGet = await pedir(URL_MCP, { headers: { authorization: `Bearer ${token}`, accept: 'text/event-stream' } });
  verificar('GET /mcp con token responde 405 (Allow: POST)', rGet.status === 405 && rGet.headers.get('allow') === 'POST', `status ${rGet.status}`);
}

// (e): rotación del refresh token y rechazo del token ya canjeado.
async function probarRefresh(metadata, tokens) {
  seccion('(e) Refresh token');
  const refrescar = (refreshToken) =>
    pedir(metadata.token_endpoint, {
      method: 'POST',
      headers: FORMULARIO,
      body: new URLSearchParams({ grant_type: 'refresh_token', refresh_token: refreshToken, client_id: ID_CLIENTE, resource: URL_MCP }),
    });

  const rNuevo = await refrescar(tokens.refresh_token);
  const nuevos = await leerJson(rNuevo);
  verificar('refresh emite tokens nuevos y rota el refresh token', rNuevo.status === 200 && Boolean(nuevos?.access_token) && nuevos.refresh_token !== tokens.refresh_token, `status ${rNuevo.status}, ${rNuevo.ms} ms`);

  const rReuso = await refrescar(tokens.refresh_token);
  const reuso = await leerJson(rReuso);
  verificar('reusar el refresh token anterior -> invalid_grant', rReuso.status === 400 && reuso?.error === 'invalid_grant', `status ${rReuso.status}, error=${reuso?.error}${rReuso.status === 200 ? ' (¿la API corre con Mcp__SegundosDeToleranciaDeReusoDeRefresh=0?)' : ''}`);

  if (nuevos?.access_token) {
    const { respuesta } = await llamarMcp(nuevos.access_token, { jsonrpc: '2.0', id: 1, method: 'tools/list' }, { 'mcp-protocol-version': PROTOCOLO_2025 });
    verificar('access token de la cadena revocada en /mcp -> 401', respuesta.status === 401, `status ${respuesta.status}`);
  }
}

// (f): casos negativos que necesitan credenciales.
async function probarNegativos(metadata, tokens, usuario) {
  seccion('(f) Casos negativos con credenciales');
  const sinAntiforgery = await pedirCodigo(metadata, usuario, false);
  verificar('aprobar sin token antiforgery -> sin código', !sinAntiforgery.codigo, `status ${sinAntiforgery.respuesta.status}`);

  const plataforma = credenciales('WAYS_MCP_', '_PLATAFORMA');
  if (plataforma) {
    const { respuesta, codigo } = await pedirCodigo(metadata, plataforma);
    verificar('el usuario de plataforma no recibe código', !codigo, `status ${respuesta.status}`);
  } else {
    informar('usuario de plataforma', 'sin WAYS_MCP_MAIL_PLATAFORMA/WAYS_MCP_PASSWORD_PLATAFORMA: se omite');
  }

  if (tokens?.access_token) {
    const rApi = await pedir(`${BASE}/api/auth/me`, { headers: { authorization: `Bearer ${tokens.access_token}` } });
    verificar('access token MCP en GET /api/auth/me -> 401', rApi.status === 401, `status ${rApi.status}`);
  }

  const rLogin = await pedir(`${BASE}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ mail: usuario.mail, password: usuario.password, solicitarBearer: true }),
  });
  const sesion = await leerJson(rLogin);
  if (rLogin.status === 200 && sesion?.token) {
    const { respuesta } = await llamarMcp(sesion.token, { jsonrpc: '2.0', id: 1, method: 'tools/list' }, { 'mcp-protocol-version': PROTOCOLO_2025 });
    verificar('bearer de Ways (POST /api/auth/login) en /mcp -> 401', respuesta.status === 401, `status ${respuesta.status}`);
  } else {
    verificar('login de Ways con solicitarBearer', false, `status ${rLogin.status}`);
  }

  const rCookie = await pedir(`${BASE}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ mail: usuario.mail, password: usuario.password }),
  });
  const cookie = cookiesDe(rCookie);
  if (rCookie.status === 200 && cookie.includes('ways.sesion=')) {
    const { respuesta } = await llamarMcp(null, { jsonrpc: '2.0', id: 1, method: 'tools/list' }, { 'mcp-protocol-version': PROTOCOLO_2025, cookie });
    verificar('cookie ways.sesion en /mcp -> 401', respuesta.status === 401, `status ${respuesta.status}`);
  }
}

async function principal() {
  console.log(`Base: ${BASE}   Base pública: ${PUBLICA}   Recurso MCP: ${URL_MCP}   Cliente: ${ID_CLIENTE}${SOLO_PUBLICO ? '   (solo público)' : ''}`);

  const metadata = await descubrir();
  if (!metadata) return;
  await probarSolicitudesSinCredenciales(metadata);
  if (SOLO_PUBLICO) return;

  const usuario = credenciales('WAYS_MCP_');
  if (!usuario) {
    verificar('credenciales del usuario de tenant', false, 'faltan WAYS_MCP_MAIL y WAYS_MCP_PASSWORD (o usar --solo-publico)');
    return;
  }

  const tokens = await autorizar(metadata, usuario);
  if (tokens) {
    await probarMcp(tokens.access_token);
  }
  await probarNegativos(metadata, tokens, usuario);
  if (tokens) {
    await probarRefresh(metadata, tokens);
  }
}

try {
  await principal();
} catch (error) {
  registrar('FAIL', 'error inesperado del script', error?.message ?? String(error));
}

const fallidos = resultados.filter((r) => r.estado === 'FAIL');
console.log(`\nResumen: ${resultados.filter((r) => r.estado === 'PASS').length} PASS, ${fallidos.length} FAIL, ${resultados.filter((r) => r.estado === 'INFO').length} INFO`);
process.exitCode = fallidos.length > 0 ? 1 : 0;
