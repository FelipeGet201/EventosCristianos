
let AppState = {
    TemaVisual: {
        ColorFondo: "#F4F7F9", ColorSuperficie: "#FFFFFF", ColorPrimario: "#0F172A",
        ColorAcento: "#E11D48", ColorTextoPrincipal: "#334155", ColorTextoSecundario: "#64748B",
        FuenteTitulos: "'Outfit', sans-serif", FuenteCuerpo: "'Inter', sans-serif", RadioBordes: "16px"
    },
    Secciones: []
};

let theModal = null;
let alertModal = null;
let confirmModal = null;


document.addEventListener("DOMContentLoaded", () => {
    // 1. Inicializamos los modales base
    theModal = new bootstrap.Modal(document.getElementById('modalSelectDesign'));
    alertModal = new bootstrap.Modal(document.getElementById('sysAlertModal'));
    confirmModal = new bootstrap.Modal(document.getElementById('sysConfirmModal'));

    // 2. Cargamos configuraciones y renderizamos la UI
    if (configGuardada && configGuardada.TemaVisual) {
        AppState = configGuardada;
    }
    renderizarDropdownTemas();
    renderizarCatList();
    sincronizarInputsTema();
    aplicarTemaVivo();

    if (AppState.Secciones.length > 0) {
        renderizarCanvasCompleto();

        AppState.Secciones.forEach(sec => {
            const itemCatalogo = document.querySelector(`#catalog-list .module-drag-item[data-identificador="${sec.Identificador}"]`);
            if (itemCatalogo) {
                itemCatalogo.classList.add('d-none');
            }
        });
    }

    // 3. MOSTRAR EL AVISO DE BIENVENIDA AL FINAL DE LA CARGA DE LA UI
    requestAnimationFrame(() => {
        const welcomeModalEl = document.getElementById('sysWelcomeModal');

        // Eliminamos la validación del localStorage para que siempre entre
        if (welcomeModalEl) {
            const welcomeModal = new bootstrap.Modal(welcomeModalEl);
            welcomeModal.show();
        }
    });

    // Configuración de SortableJS para el lienzo
    const dropZone = document.getElementById('drop-zone');
    new Sortable(dropZone, {
        group: 'shared-canvas',
        animation: 150,
        handle: '.btn-move-mod',
        ghostClass: 'bg-light',
        scroll: true,
        scrollSensitivity: 80, // Empieza a scrollear cuando el mouse está a 80px del borde superior/inferior
        scrollSpeed: 20,
        bubbleScroll: true,
        // FIX PARA QUE NO SE ATORE EN LOS BORDES
        forceFallback: true,   // Obliga a usar el drag de JS puro (adiós HTML5 nativo)
        fallbackTolerance: 3,  // Margen de 3px para evitar que un clic normal se detecte como arrastre

        onAdd: function (evt) {
            const itemArrastrado = evt.item;
            const identificador = itemArrastrado.dataset.identificador;

            window.indiceSoltado = evt.newIndex;

            // Limpiamos la basura visual del drag and drop
            itemArrastrado.remove();

            // Validar si la sección ya existe para no duplicar módulos del mismo tipo
            const idxExistente = AppState.Secciones.findIndex(s => s.Identificador === identificador);
            if (idxExistente >= 0) {
                mostrarAlerta("Esta sección ya se encuentra en el lienzo. Si deseas cambiar su diseño, usa el botón de la paleta dentro de la sección.");
            } else {
                abrirModalDiseno(identificador);

                // NUEVO: Ocultamos el elemento del menú lateral (Catálogo)
                const itemCatalogo = document.querySelector(`#catalog-list [data-identificador="${identificador}"]`);
                if (itemCatalogo) {
                    itemCatalogo.classList.add('d-none');
                }
            }
        },
        onEnd: function () {
            actualizarOrdenInterno();
        }
    });
});

function mostrarAlerta(mensaje) {
    document.getElementById('sysAlertMessage').innerText = mensaje;
    alertModal.show();
}

function mostrarConfirmacion(mensaje, callbackAceptar) {
    document.getElementById('sysConfirmMessage').innerText = mensaje;
    const btnOk = document.getElementById('sysConfirmBtnOk');

    const newBtnOk = btnOk.cloneNode(true);
    btnOk.parentNode.replaceChild(newBtnOk, btnOk);

    newBtnOk.addEventListener('click', () => {
        confirmModal.hide();
        callbackAceptar();
    });
    confirmModal.show();
}

function toggleDevice(type) {
    const frame = document.getElementById('page-frame');
    const btnDesktop = document.getElementById('btn-desktop');
    const btnMobile = document.getElementById('btn-mobile');
    const scrollArea = document.getElementById('canvas-scroll-area');

    if (type === 'mobile') {
        frame.classList.add('mobile-view');
        btnMobile.classList.replace('btn-outline-light', 'btn-light');
        btnMobile.classList.remove('text-white-50');
        btnMobile.querySelector('i').style.color = '#005f73';

        btnDesktop.classList.replace('btn-light', 'btn-outline-light');
        btnDesktop.classList.add('text-white-50');
        btnDesktop.querySelector('i').style.color = 'inherit';
        scrollArea.style.alignItems = 'center';
    } else {
        frame.classList.remove('mobile-view');
        btnDesktop.classList.replace('btn-outline-light', 'btn-light');
        btnDesktop.classList.remove('text-white-50');
        btnDesktop.querySelector('i').style.color = '#005f73';

        btnMobile.classList.replace('btn-light', 'btn-outline-light');
        btnMobile.classList.add('text-white-50');
        btnMobile.querySelector('i').style.color = 'inherit';
        scrollArea.style.alignItems = 'flex-start';
    }
}

function renderizarCatList() {
    const container = document.getElementById('catalog-list');
    container.innerHTML = '';
    catalogoBruto.forEach(sec => {
        const div = document.createElement('div');
        div.className = 'module-drag-item';
        div.dataset.identificador = sec.Identificador;

        div.innerHTML = `
<span><i class="fa-solid fa-grip-vertical text-muted me-2" style="cursor: grab;"></i> ${sec.Nombre}</span>
<button type="button" class="btn btn-sm btn-light border py-0 px-2" onclick="botonAgregarCatalogo('${sec.Identificador}')">
    <i class="fa-solid fa-plus text-primary"></i>
</button>
`;
        container.appendChild(div);
    });

    new Sortable(container, {
        group: { name: 'shared-canvas', pull: 'clone', put: false },
        sort: false,
        animation: 150,
        scroll: true,
        scrollSensitivity: 80,
        scrollSpeed: 20,
        bubbleScroll: true,
        forceFallback: true,       
        fallbackOnBody: true,     
        fallbackClass: "sortable-fallback", // Le damos una clase para estilizarlo
        ghostClass: "sortable-ghost"        // Clase para el espacio que deja
    });
}

function botonAgregarCatalogo(identificador) {
    const idxExistente = AppState.Secciones.findIndex(s => s.Identificador === identificador);
    if (idxExistente >= 0) {
        mostrarAlerta("Esta sección ya se encuentra en el lienzo. Si deseas cambiar su diseño, usa el botón de la paleta dentro de la sección.");
    } else {
        abrirModalDiseno(identificador);
    }
}

function abrirModalDiseno(identificador, esCambio = false) {
    const catSec = catalogoBruto.find(s => s.Identificador === identificador);
    document.getElementById('modalSelectTitle').innerText = esCambio ? `Cambiar Diseño: ${catSec.Nombre}` : `Añadir: ${catSec.Nombre}`;
    const grid = document.getElementById('design-options-grid');
    grid.innerHTML = '';

    // 1. Obtenemos el diseño activo (si existe) desde el estado de la aplicación
    const seccionActiva = AppState.Secciones.find(s => s.Identificador === identificador);
    const vistaActiva = seccionActiva ? seccionActiva.Vista : null;

    catSec.Disenos.forEach(dis => {
        // 2. Comparamos si este diseño en el bucle es el que está seleccionado actualmente
        const esSeleccionado = (vistaActiva === dis.Vista);

        // 3. Asignamos estilos e indicadores visuales condicionalmente
        const borderColor = esSeleccionado ? '#005f73' : '#e2e8f0';
        const borderThickness = esSeleccionado ? '2px' : '1px';
        const extraShadow = esSeleccionado ? 'box-shadow: 0 4px 12px rgba(0, 95, 115, 0.15) !important;' : '';
        const indicadorHtml = esSeleccionado
            ? `<div class="position-absolute top-0 end-0 m-2"><span class="badge bg-success shadow-sm"><i class="fa-solid fa-check me-1"></i>Seleccionado</span></div>`
            : '';

        grid.innerHTML += `
                <div class="col-md-6">
                    <div class="card h-100 border-0 shadow-sm transition-all position-relative" 
                         style="cursor:pointer; border: ${borderThickness} solid ${borderColor} !important; ${extraShadow}" 
                         onclick="seleccionarDiseno('${identificador}', '${dis.Vista}')" 
                         onmouseover="this.style.borderColor='#005f73'" 
                         onmouseout="this.style.borderColor='${borderColor}'">
                         
                        ${indicadorHtml}
                        
                        <div class="card-body text-center p-4">
                            <h6 class="fw-bold mb-2" style="color: #005f73;">${dis.Nombre}</h6>
                            <p class="small text-muted mb-0">${dis.Descripcion}</p>
                        </div>
                    </div>
                </div>
            `;
    });
    theModal.show();
}

function seleccionarDiseno(identificador, vista) {
    theModal.hide();
    const idxExistente = AppState.Secciones.findIndex(s => s.Identificador === identificador);

    if (idxExistente >= 0) {
        AppState.Secciones[idxExistente].Vista = vista;
        recargarModuloUnicoHTML(identificador, vista);
    } else {
        // 1. Tomamos el índice guardado o lo mandamos al final
        let indexInsercion = (window.indiceSoltado !== undefined) ? window.indiceSoltado : AppState.Secciones.length;

        // 2. Insertamos la sección en su lugar exacto usando splice en lugar de push
        AppState.Secciones.splice(indexInsercion, 0, { Orden: indexInsercion + 1, Identificador: identificador, Vista: vista });

        // 3. Recalculamos el "Orden" para que sea consecutivo (1, 2, 3...)
        AppState.Secciones.forEach((s, i) => s.Orden = i + 1);

        // 4. Inyectamos pasándole el nuevo parámetro
        inyectarModuloNuevoUI(identificador, vista, indexInsercion);

        // Ocultamos el elemento del menú lateral al confirmar el diseño
        const itemCatalogo = document.querySelector(`#catalog-list .module-drag-item[data-identificador="${identificador}"]`);
        if (itemCatalogo) {
            itemCatalogo.classList.add('d-none');
        }

        // Limpiamos la variable temporal
        window.indiceSoltado = undefined;
    }
}

function renderizarCanvasCompleto() {
    const dz = document.getElementById('drop-zone');
    dz.innerHTML = '';
    AppState.Secciones.sort((a, b) => a.Orden - b.Orden).forEach(sec => {
        crearContenedorModuloUI(dz, sec.Identificador, sec.Vista);
        cargarModuloHTMLAJAX(sec.Identificador, sec.Vista);
    });
    actualizarVisibilidadMensaje();
}

function inyectarModuloNuevoUI(identificador, vista, indexInsercion) {
    const dz = document.getElementById('drop-zone');
    // Pasamos el índice
    crearContenedorModuloUI(dz, identificador, vista, indexInsercion);
    cargarModuloHTMLAJAX(identificador, vista);
    actualizarVisibilidadMensaje();
}

function crearContenedorModuloUI(parent, identificador, vista, indexInsercion) {
    // Detectamos si es un divisor leyendo el identificador que viene de tu BD
    const esDivisor = identificador.toLowerCase().includes('divisor') || identificador.toLowerCase().includes('separador');

    const wrapper = document.createElement('div');
    wrapper.className = 'rendered-module';
    wrapper.id = `mod-${identificador}`;
    wrapper.dataset.id = identificador;

    wrapper.innerHTML = `
<div class="module-wrapper-inner ${esDivisor ? 'is-divisor' : ''}">
    <div class="module-overlay"></div>
    <div class="module-action-bar">
        <button type="button" class="btn-move-mod" title="Arrastrar"><i class="fa-solid fa-arrows-up-down"></i></button>
        <button type="button" title="Subir" onclick="moverBoton('${identificador}', -1)"><i class="fa-solid fa-chevron-up"></i></button>
        <button type="button" title="Bajar" onclick="moverBoton('${identificador}', 1)"><i class="fa-solid fa-chevron-down"></i></button>
        ${esDivisor ? '' : `<button type="button" title="Cambiar Diseño" onclick="abrirModalDiseno('${identificador}', true)"><i class="fa-solid fa-palette"></i></button>`}
        <button type="button" class="btn-delete-mod" title="Quitar" onclick="eliminarModulo('${identificador}')"><i class="fa-solid fa-xmark"></i></button>
    </div>
    <div class="module-content">
        <div class="module-loader"><i class="fa-solid fa-circle-notch fa-spin fs-2"></i></div>
    </div>
</div>
${esDivisor ? '' : '<div class="module-separator"></div>'}
`;

    // NUEVO: Verificamos el índice para insertarlo exactamente donde el usuario lo soltó
    if (indexInsercion !== undefined && indexInsercion < parent.children.length) {
        parent.insertBefore(wrapper, parent.children[indexInsercion]);
    } else {
        parent.appendChild(wrapper);
    }
}

function cargarModuloHTMLAJAX(identificador, vista) {
    const sId = document.getElementById('sIdIglesia').value;
    const contentDiv = document.querySelector(`#mod-${identificador} .module-content`);

    fetch(`/IglesiasWeb/RenderizarModuloDemo?sId=${sId}&identificador=${identificador}&vista=${vista}`)
        .then(res => res.text())
        .then(html => { contentDiv.innerHTML = html; })
        .catch(err => { contentDiv.innerHTML = `<div class="alert alert-danger m-3">Error cargando vista.</div>`; });
}

function recargarModuloUnicoHTML(identificador, nuevaVista) {
    const contentDiv = document.querySelector(`#mod-${identificador} .module-content`);
    contentDiv.innerHTML = `<div class="module-loader"><i class="fa-solid fa-circle-notch fa-spin fs-2"></i></div>`;
    cargarModuloHTMLAJAX(identificador, nuevaVista);
}

function eliminarModulo(identificador) {
    mostrarConfirmacion('¿Deseas quitar esta sección del diseño?', () => {
        document.getElementById(`mod-${identificador}`).remove();
        AppState.Secciones = AppState.Secciones.filter(s => s.Identificador !== identificador);
        actualizarOrdenInterno();
        actualizarVisibilidadMensaje();

        // NUEVO: Volvemos a mostrar el elemento en el catálogo lateral
        const itemCatalogo = document.querySelector(`#catalog-list .module-drag-item[data-identificador="${identificador}"]`);
        if (itemCatalogo) {
            itemCatalogo.classList.remove('d-none');
        }
    });
}

function moverBoton(identificador, direccion) {
    const index = AppState.Secciones.findIndex(s => s.Identificador === identificador);
    if (index < 0) return;

    const newIndex = index + direccion;
    if (newIndex < 0 || newIndex >= AppState.Secciones.length) return;

    const temp = AppState.Secciones[index];
    AppState.Secciones[index] = AppState.Secciones[newIndex];
    AppState.Secciones[newIndex] = temp;

    AppState.Secciones.forEach((s, i) => s.Orden = i + 1);
    renderizarCanvasCompleto();
}

function actualizarOrdenInterno() {
    const divs = document.querySelectorAll('#drop-zone .rendered-module');
    const nuevaLista = [];

    divs.forEach((div, idx) => {
        const id = div.dataset.id;
        const secState = AppState.Secciones.find(s => s.Identificador === id);
        if (secState) {
            secState.Orden = idx + 1;
            nuevaLista.push(secState);
        }
    });
    AppState.Secciones = nuevaLista;
}

function actualizarVisibilidadMensaje() {
    const msg = document.getElementById('empty-message');
    if (AppState.Secciones.length > 0) { if (msg) msg.style.display = 'none'; }
    else { if (msg) msg.style.display = 'flex'; }
}

function aplicarTemaPredefinido(index, event) {
    if (event) event.preventDefault();
    const tema = temasPredefinidos[index];

    document.getElementById('thm-bg').value = tema.colores.bg;
    document.getElementById('thm-surface').value = tema.colores.surface;
    document.getElementById('thm-primary').value = tema.colores.primary;
    document.getElementById('thm-accent').value = tema.colores.accent;

    // NUEVOS COLORES:
    document.getElementById('thm-text-main').value = tema.colores.textMain;
    document.getElementById('thm-text-muted').value = tema.colores.textMuted;

    actualizarBotonTemaSeleccionado(tema);
    aplicarTemaVivo();
}

function sincronizarInputsTema() {
    const t = AppState.TemaVisual;
    document.getElementById('thm-bg').value = t.ColorFondo;
    document.getElementById('thm-surface').value = t.ColorSuperficie;
    document.getElementById('thm-primary').value = t.ColorPrimario;
    document.getElementById('thm-accent').value = t.ColorAcento;

    // NUEVOS COLORES:
    document.getElementById('thm-text-main').value = t.ColorTextoPrincipal || "#1E293B";
    document.getElementById('thm-text-muted').value = t.ColorTextoSecundario || "#64748B";

    document.getElementById('thm-font-title').value = t.FuenteTitulos;
    document.getElementById('thm-font-body').value = t.FuenteCuerpo;
    document.getElementById('thm-radius').value = t.RadioBordes;

    document.getElementById('thm-bg-txt').value = t.ColorFondo;
    document.getElementById('thm-surface-txt').value = t.ColorSuperficie;
    document.getElementById('thm-primary-txt').value = t.ColorPrimario;
    document.getElementById('thm-accent-txt').value = t.ColorAcento;

    // NUEVOS TEXTOS:
    document.getElementById('thm-text-main-txt').value = t.ColorTextoPrincipal || "#1E293B";
    document.getElementById('thm-text-muted-txt').value = t.ColorTextoSecundario || "#64748B";
}
function aplicarTemaVivo() {
    AppState.TemaVisual.ColorFondo = document.getElementById('thm-bg').value;
    AppState.TemaVisual.ColorSuperficie = document.getElementById('thm-surface').value;
    AppState.TemaVisual.ColorPrimario = document.getElementById('thm-primary').value;
    AppState.TemaVisual.ColorAcento = document.getElementById('thm-accent').value;

    AppState.TemaVisual.ColorTextoPrincipal = document.getElementById('thm-text-main').value;
    AppState.TemaVisual.ColorTextoSecundario = document.getElementById('thm-text-muted').value;

    AppState.TemaVisual.FuenteTitulos = document.getElementById('thm-font-title').value;
    AppState.TemaVisual.FuenteCuerpo = document.getElementById('thm-font-body').value;
    AppState.TemaVisual.RadioBordes = document.getElementById('thm-radius').value;

    sincronizarInputsTema();

    const styleTag = document.getElementById('live-theme-styles');
    styleTag.innerHTML = `
        /* 1. Declaramos variables globalmente igual que en LayoutPaginasWEB (:root) */
        :root {
            --theme-bg: ${AppState.TemaVisual.ColorFondo};
            --theme-surface: ${AppState.TemaVisual.ColorSuperficie};
            --theme-primary: ${AppState.TemaVisual.ColorPrimario};
            --theme-accent: ${AppState.TemaVisual.ColorAcento};
            --theme-text-main: ${AppState.TemaVisual.ColorTextoPrincipal};
            --theme-text-muted: ${AppState.TemaVisual.ColorTextoSecundario};
            --theme-font-title: ${AppState.TemaVisual.FuenteTitulos};
            --theme-font-body: ${AppState.TemaVisual.FuenteCuerpo};
            --radius-lg: ${AppState.TemaVisual.RadioBordes} !important;
        }

        /* 2. Aplicamos la fuente base al lienzo */
        #page-frame {
            font-family: var(--theme-font-body);
            background-color: var(--theme-bg) !important;
            color: var(--theme-text-main);
        }

        /* 3. El pseudo-selector :where() anula el "peso" del ID #page-frame.
           Esto hace que la especificidad sea exactamente igual a poner "h1, h2..."
           permitiendo que las clases de tus módulos ganen la prioridad. */
        :where(#page-frame) h1,
        :where(#page-frame) h2,
        :where(#page-frame) h3,
        :where(#page-frame) h4,
        :where(#page-frame) h5,
        :where(#page-frame) h6 {
            font-family: var(--theme-font-title);
            color: var(--theme-primary);
        }
    `;
}

function guardarDiseno() {
    if (AppState.Secciones.length === 0) {
        mostrarAlerta("Agrega al menos una sección al lienzo antes de guardar.");
        return;
    }

    const sId = document.getElementById('sIdIglesia').value;
    const formData = new FormData();
    formData.append('sId', sId);
    formData.append('configuracionJson', JSON.stringify(AppState));

    // BUSCAMOS EL TOKEN DE SEGURIDAD Y LO AGREGAMOS
    const tokenElement = document.querySelector('input[name="__RequestVerificationToken"]');
    if (tokenElement) {
        formData.append('__RequestVerificationToken', tokenElement.value);
    }

    // Le agregamos un catch para poder ver si falla la conexión
    fetch('/IglesiasWeb/GuardarConstructor', { method: 'POST', body: formData })
        .then(res => {
            if (!res.ok) throw new Error("Error en la conexión con el servidor (HTTP " + res.status + ")");
            return res.json();
        })
        .then(data => {
            if (data.success) {
                mostrarAlerta("Diseño guardado exitosamente en borrador.");
                setTimeout(() => { window.location.href = '/IglesiasWeb/Index'; }, 1500);
            } else {
                mostrarAlerta("Error: " + data.message);
            }
        })
        .catch(error => {
            console.error("Falla en fetch:", error);
            mostrarAlerta("No se pudo guardar. Revisa la consola (F12) para más detalles.");
        });
}

function reiniciarDiseno() {
    mostrarConfirmacion("¿Vaciar todo el lienzo? Se perderá la configuración actual que no hayas guardado.", () => {
        // 1. Limpiamos el estado
        AppState.Secciones = [];

        // 2. Restauramos el HTML del lienzo vacío
        document.getElementById('drop-zone').innerHTML = `<div class="canvas-empty" id="empty-message"><i class="fa-solid fa-layer-group fs-1 mb-3"></i><h5>Lienzo Vacío</h5><p class="mb-0">Arrastra una sección o usa el botón (+).</p></div>`;
        actualizarVisibilidadMensaje();

        // 3. NUEVO: Restauramos TODOS los elementos ocultos en el panel lateral (Catálogo)
        const itemsCatalogo = document.querySelectorAll('#catalog-list .module-drag-item');
        itemsCatalogo.forEach(item => {
            item.classList.remove('d-none');
        });
    });
}
// --- LÓGICA DE TEMAS PREDEFINIDOS ---
const temasPredefinidos = [
    // Monocromáticos y Neutros
    { nombre: "Minimalista Claro", colores: { bg: "#F8FAFC", surface: "#FFFFFF", primary: "#0F172A", accent: "#3B82F6", textMain: "#1E293B", textMuted: "#64748B" } },
    { nombre: "Oscuro Elegante", colores: { bg: "#0F172A", surface: "#1E293B", primary: "#F8FAFC", accent: "#EAB308", textMain: "#F8FAFC", textMuted: "#94A3B8" } },
    { nombre: "Gris Pizarra", colores: { bg: "#F3F4F6", surface: "#FFFFFF", primary: "#111827", accent: "#4B5563", textMain: "#1F2937", textMuted: "#6B7280" } },
    { nombre: "Carbón y Esmeralda", colores: { bg: "#18181B", surface: "#27272A", primary: "#FAFAFA", accent: "#10B981", textMain: "#FAFAFA", textMuted: "#A1A1AA" } },

    // Tonos Azules y Fríos (Textos en grises azulados)
    { nombre: "Azul Marino", colores: { bg: "#EFF6FF", surface: "#FFFFFF", primary: "#1E3A8A", accent: "#2563EB", textMain: "#1E293B", textMuted: "#64748B" } },
    { nombre: "Cian Moderno", colores: { bg: "#ECFEFF", surface: "#FFFFFF", primary: "#164E63", accent: "#0891B2", textMain: "#1E293B", textMuted: "#475569" } },
    { nombre: "Océano Profundo", colores: { bg: "#082F49", surface: "#0C4A6E", primary: "#F0F9FF", accent: "#38BDF8", textMain: "#F0F9FF", textMuted: "#BAE6FD" } },

    // Tonos Cálidos (Rojos, Naranjas, Amarillos) (Textos en grises cálidos)
    { nombre: "Rojo Carmesí", colores: { bg: "#FEF2F2", surface: "#FFFFFF", primary: "#7F1D1D", accent: "#DC2626", textMain: "#1C1917", textMuted: "#57534E" } },
    { nombre: "Burdeos Clásico", colores: { bg: "#FFF1F2", surface: "#FFFFFF", primary: "#881337", accent: "#E11D48", textMain: "#18181B", textMuted: "#52525B" } },
    { nombre: "Terracota", colores: { bg: "#FFF7ED", surface: "#FFFFFF", primary: "#7C2D12", accent: "#EA580C", textMain: "#1C1917", textMuted: "#57534E" } },
    { nombre: "Dorado y Ébano", colores: { bg: "#FEFCE8", surface: "#FFFFFF", primary: "#713F12", accent: "#CA8A04", textMain: "#1C1917", textMuted: "#57534E" } },

    // Tonos Verdes y Tierra (Textos en grises neutros/cálidos)
    { nombre: "Verde Bosque", colores: { bg: "#F0FDF4", surface: "#FFFFFF", primary: "#14532D", accent: "#16A34A", textMain: "#1F2937", textMuted: "#4B5563" } },
    { nombre: "Oliva Suave", colores: { bg: "#F7FEE7", surface: "#FFFFFF", primary: "#3F6212", accent: "#65A30D", textMain: "#1F2937", textMuted: "#4B5563" } },
    { nombre: "Crema y Tierra", colores: { bg: "#FAF7F2", surface: "#FFFFFF", primary: "#432818", accent: "#99582A", textMain: "#1C1917", textMuted: "#57534E" } },
    { nombre: "Café Tostado", colores: { bg: "#FEF8F5", surface: "#FFFFFF", primary: "#3E2723", accent: "#5D4037", textMain: "#1C1917", textMuted: "#57534E" } },

    // Tonos Púrpuras
    { nombre: "Púrpura Real", colores: { bg: "#FAF5FF", surface: "#FFFFFF", primary: "#4C1D95", accent: "#9333EA", textMain: "#1F2937", textMuted: "#4B5563" } }
];

function renderizarDropdownTemas() {
    const lista = document.getElementById('lista-temas-predefinidos');
    lista.innerHTML = '';

    temasPredefinidos.forEach((tema, index) => {
        const li = document.createElement('li');
        // Usamos los 3 colores principales para la vista previa del ítem
        li.innerHTML = `
<a class="dropdown-item d-flex align-items-center gap-3 py-2 border-bottom" href="#" onclick="aplicarTemaPredefinido(${index}, event)">
    <div class="d-flex gap-1" style="font-size: 1.1rem;">
        <i class="fa-solid fa-circle" style="color: ${tema.colores.surface}; text-shadow: 0 0 2px rgba(0,0,0,0.3);" title="Superficie"></i>
        <i class="fa-solid fa-circle" style="color: ${tema.colores.primary}; text-shadow: 0 0 1px rgba(0,0,0,0.1);" title="Primario"></i>
        <i class="fa-solid fa-circle" style="color: ${tema.colores.accent}; text-shadow: 0 0 1px rgba(0,0,0,0.1);" title="Acento"></i>
    </div>
    <span class="fw-semibold text-dark">${tema.nombre}</span>
</a>
`;
        lista.appendChild(li);
    });
}

function actualizarBotonTemaSeleccionado(tema) {
    const preview = document.getElementById('selected-theme-preview');
    if (tema) {
        preview.innerHTML = `
                <div class="d-flex gap-1" style="font-size: 1rem;">
                    <i class="fa-solid fa-circle" style="color: ${tema.colores.surface}; text-shadow: 0 0 2px rgba(0,0,0,0.3);"></i>
                    <i class="fa-solid fa-circle" style="color: ${tema.colores.primary};"></i>
                    <i class="fa-solid fa-circle" style="color: ${tema.colores.accent};"></i>
                </div>
                <span class="fw-bold text-dark">${tema.nombre}</span>
            `;
    } else {
        preview.innerHTML = `<i class="fa-solid fa-palette text-muted"></i> <span>Personalizado</span>`;
    }
}

function onColorManualChange() {
    // Si el usuario mueve el selector de color a mano, el tema se vuelve "Personalizado"
    actualizarBotonTemaSeleccionado(null);
    aplicarTemaVivo();
}