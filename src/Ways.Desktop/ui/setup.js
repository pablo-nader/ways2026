// Pagina local de configuracion: sin framework ni build step.
// Usa el puente global window.__TAURI__ (habilitado via withGlobalTauri).

(function () {
  const OPCION_IMPRESORA_PREDETERMINADA = "";

  function invoke(comando, argumentos) {
    return window.__TAURI__.core.invoke(comando, argumentos);
  }

  function elementos() {
    return {
      formulario: document.getElementById("formulario-configuracion"),
      urlServidor: document.getElementById("url-servidor"),
      impresora: document.getElementById("impresora"),
      botonGuardar: document.getElementById("boton-guardar"),
      botonImprimirPrueba: document.getElementById("boton-imprimir-prueba"),
      mensaje: document.getElementById("mensaje"),
      pieVersion: document.getElementById("pie-version"),
    };
  }

  function mostrarMensaje(el, texto, tipo) {
    el.mensaje.textContent = texto;
    el.mensaje.className = "mensaje" + (tipo ? " " + tipo : "");
  }

  async function cargarOpcionPredeterminada(el) {
    const opcionPredeterminada = el.impresora.querySelector(
      'option[value="' + OPCION_IMPRESORA_PREDETERMINADA + '"]'
    );
    try {
      const predeterminada = await invoke("impresora_predeterminada_detallada");
      if (!predeterminada.nombre) {
        opcionPredeterminada.textContent =
          "Impresora predeterminada de Windows (ninguna configurada)";
        return;
      }
      const advertencia = predeterminada.es_virtual
        ? " - no es una impresora de tickets"
        : "";
      opcionPredeterminada.textContent =
        "Impresora predeterminada de Windows (" + predeterminada.nombre + ")" + advertencia;
    } catch (error) {
      // El texto generico ya sirve como respaldo si esto falla.
    }
  }

  async function cargarImpresoras(el, impresoraGuardada) {
    try {
      const impresoras = await invoke("listar_impresoras_con_detalle");
      for (const impresora of impresoras) {
        const opcion = document.createElement("option");
        opcion.value = impresora.nombre;
        if (impresora.es_virtual) {
          opcion.textContent = impresora.nombre + " (no es una impresora de tickets)";
          opcion.disabled = true;
        } else {
          opcion.textContent = impresora.nombre;
        }
        el.impresora.appendChild(opcion);
      }
      await cargarOpcionPredeterminada(el);

      if (impresoraGuardada) {
        el.impresora.value = impresoraGuardada;
        if (el.impresora.value !== impresoraGuardada) {
          // La impresora guardada ya no esta instalada: se agrega igual
          // para no perder el dato, mostrada al final de la lista.
          const opcion = document.createElement("option");
          opcion.value = impresoraGuardada;
          opcion.textContent = impresoraGuardada + " (no detectada)";
          el.impresora.appendChild(opcion);
          el.impresora.value = impresoraGuardada;
        }
      } else {
        el.impresora.value = OPCION_IMPRESORA_PREDETERMINADA;
      }
    } catch (error) {
      mostrarMensaje(el, "No se pudieron listar las impresoras: " + error, "error");
    }
  }

  async function cargarConfiguracionActual(el) {
    try {
      const configuracion = await invoke("leer_configuracion");
      if (configuracion) {
        el.urlServidor.value = configuracion.url_servidor || "";
      }
      await cargarImpresoras(el, configuracion ? configuracion.impresora : null);
    } catch (error) {
      await cargarImpresoras(el, null);
    }
  }

  async function cargarInfoApp(el) {
    try {
      const info = await invoke("info_app");
      el.pieVersion.textContent = "Ways POS v" + info.version;
    } catch (error) {
      // La version es solo informativa; no bloquea el uso de la pagina.
    }
  }

  async function manejarEnvio(evento, el) {
    evento.preventDefault();
    el.botonGuardar.disabled = true;
    mostrarMensaje(el, "Guardando...", "");

    const configuracion = {
      url_servidor: el.urlServidor.value.trim(),
      impresora: el.impresora.value || null,
    };

    try {
      await invoke("guardar_configuracion", { configuracion });
      mostrarMensaje(el, "Configuracion guardada. Abriendo el punto de venta...", "ok");
    } catch (error) {
      mostrarMensaje(el, String(error), "error");
      el.botonGuardar.disabled = false;
    }
  }

  async function manejarImprimirPrueba(el) {
    el.botonImprimirPrueba.disabled = true;
    mostrarMensaje(el, "Imprimiendo prueba...", "");
    try {
      await invoke("imprimir_prueba", { impresora: el.impresora.value || null });
      mostrarMensaje(el, "Ticket de prueba enviado a la impresora.", "ok");
    } catch (error) {
      mostrarMensaje(el, String(error), "error");
    } finally {
      el.botonImprimirPrueba.disabled = false;
    }
  }

  document.addEventListener("DOMContentLoaded", () => {
    const el = elementos();
    cargarConfiguracionActual(el);
    cargarInfoApp(el);
    el.formulario.addEventListener("submit", (evento) => manejarEnvio(evento, el));
    el.botonImprimirPrueba.addEventListener("click", () => manejarImprimirPrueba(el));
  });
})();
