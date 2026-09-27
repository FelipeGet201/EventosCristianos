/* =========================================
   LÓGICA DEL SISTEMA DE PECES Y MILAGRO
   ========================================= */

// --- VARIABLES DE CONFIGURACIÓN ---
const BLUE_MULTIPLIER = 3;            // Cantidad de peces azules si vienen en cardumen
const MAX_EVENTOS_SIMULTANEOS = 4;    // Cuántos encuentros pueden pasar en pantalla al mismo tiempo
const TIEMPO_NUEVO_EVENTO_MIN = 2000; // Tiempo mínimo (en ms) para el siguiente evento
const TIEMPO_NUEVO_EVENTO_MAX = 4500; // Tiempo máximo (en ms) para el siguiente evento
const CANTIDAD_ALGAS = 25;            // Número total de algas animadas en el fondo
const CANTIDAD_PECES_FONDO = 10;      // Número total de peces grises de fondo
const VELOCIDAD_PIXELES = 0.6;        // Velocidad real en píxeles por frame
const SEPARACION_PIXELES = 5;         // Píxeles reales de separación. (Acepta números negativos como -5 o -10 si el ícono tiene espacio transparente)

let eventosActivos = 0;       // Variable interna para llevar la cuenta (no modificar)
let cardumenActivo = false;   // Candado para asegurar un solo cardumen a la vez

document.addEventListener("DOMContentLoaded", function () {
    const contenedor = document.getElementById('mar-animado');

    if (contenedor) {
        // 1. Llenar el fondo
        for (let i = 0; i < CANTIDAD_ALGAS; i++) crearAlga(contenedor);
        for (let i = 0; i < CANTIDAD_PECES_FONDO; i++) crearPezFondo(contenedor);

        // 2. Iniciar el Gestor
        gestionarCiclos(contenedor);
    }
});

function crearPezFondo(contenedor) {
    const pez = document.createElement('i');
    pez.classList.add('fa-solid', 'fa-fish', 'pez', 'pez-fondo');

    const duracion = Math.random() * 30 + 40;
    const retraso = Math.random() * 40;
    const altura = Math.random() * 95;

    pez.style.top = altura + 'vh';
    pez.style.animationName = 'nadarFondo';
    pez.style.animationDuration = duracion + 's';
    pez.style.animationDelay = '-' + retraso + 's';
    pez.style.animationTimingFunction = 'linear';
    pez.style.animationIterationCount = 'infinite';

    contenedor.appendChild(pez);
}

function crearAlga(contenedor) {
    const alga = document.createElement('i');
    const tipos = ['fa-leaf', 'fa-seedling', 'fa-feather'];
    alga.classList.add('fa-solid', tipos[Math.floor(Math.random() * tipos.length)], 'alga');

    alga.style.fontSize = (Math.random() * 0.8 + 0.4) + 'rem';
    alga.style.top = Math.random() * 100 + 'vh';
    alga.style.left = Math.random() * 100 + 'vw';

    alga.style.animationName = 'flujoAlgas';
    alga.style.animationDuration = (Math.random() * 20 + 25) + 's';
    alga.style.animationTimingFunction = 'linear';
    alga.style.animationIterationCount = 'infinite';

    contenedor.appendChild(alga);
}

function gestionarCiclos(contenedor) {
    if (eventosActivos < MAX_EVENTOS_SIMULTANEOS) {
        eventosActivos++;
        cicloNarrativo(contenedor, eventosActivos === 1);
    }

    const tiempoEspera = Math.random() * (TIEMPO_NUEVO_EVENTO_MAX - TIEMPO_NUEVO_EVENTO_MIN) + TIEMPO_NUEVO_EVENTO_MIN;
    setTimeout(() => gestionarCiclos(contenedor), tiempoEspera);
}

function cicloNarrativo(contenedor, esInicio = false) {
    let minX = esInicio ? 30 : 10;
    let maxX = esInicio ? 70 : 80;
    const puntoEncuentroX = Math.random() * maxX + minX;
    const alturaEncuentro = Math.random() * 60 + 20;

    // --- LÓGICA DE CARDUMEN ÚNICO ---
    let esCardumen = false;
    if (!cardumenActivo) {
        esCardumen = Math.random() > 0.5;
    }

    if (esCardumen) {
        cardumenActivo = true;
    }

    const cantidadAzules = esCardumen ? BLUE_MULTIPLIER : 1;

    // --- CREACIÓN CON ALEATORIEDAD ORGÁNICA ---
    const evangelistas = [];
    const offsets = [];

    for (let i = 0; i < cantidadAzules; i++) {
        const evangelista = document.createElement('i');
        evangelista.classList.add('fa-solid', 'fa-fish', 'pez', 'pez-actor', 'pez-azul-actor');

        const offsetY = (i * 2.5) + (Math.random() * 3 - 1.5);
        const offsetX = i === 0 ? 0 : (Math.random() * 3 + 1);

        evangelista.style.top = (alturaEncuentro + offsetY) + 'vh';

        offsets.push(offsetX);
        contenedor.appendChild(evangelista);
        evangelistas.push(evangelista);
    }

    const buscador = document.createElement('i');
    buscador.classList.add('fa-solid', 'fa-fish', 'pez', 'pez-actor', 'pez-gris-actor');
    buscador.style.top = alturaEncuentro + 'vh';
    contenedor.appendChild(buscador);

    const distanciaViaje = esInicio ? 35 : 100;
    let posEvangelista = puntoEncuentroX + distanciaViaje;
    let posBuscador = puntoEncuentroX - distanciaViaje;

    evangelistas.forEach((pez, index) => {
        pez.style.left = (posEvangelista + offsets[index]) + 'vw';
    });
    buscador.style.left = posBuscador + 'vw';

    let estado = 'nadando';
    let animationId;
    let lastTime = null; // Variable para controlar los FPS del monitor

    function animarPeces(timestamp) {
        // 1. Corrección de hercios (FPS) del monitor con Delta Time
        if (!lastTime) lastTime = timestamp;
        const deltaTime = timestamp - lastTime;
        lastTime = timestamp;

        // 2. Corrección de redimensionamiento de ventana
        // Calculamos la velocidad en VW basándonos en el ancho ACTUAL
        const velocidadActualVW = (VELOCIDAD_PIXELES / window.innerWidth) * 100;

        // Ajustamos la velocidad final combinando el tamaño de pantalla y los FPS
        const velocidadAjustada = velocidadActualVW * (deltaTime / 16.66);

        if (estado === 'nadando') {
            const rectBuscador = buscador.getBoundingClientRect();
            const rectEvangelista = evangelistas[0].getBoundingClientRect();

            if (rectBuscador.right + SEPARACION_PIXELES >= rectEvangelista.left) {
                estado = 'transformando';
                realizarMilagro(buscador);
            } else {
                posEvangelista -= velocidadAjustada;
                posBuscador += velocidadAjustada;

                evangelistas.forEach((pez, index) => {
                    pez.style.left = (posEvangelista + offsets[index]) + 'vw';
                });
                buscador.style.left = posBuscador + 'vw';
            }
        }
        else if (estado === 'transformando') {
            // Esperando...
        }
        else if (estado === 'regresando') {
            posEvangelista -= velocidadAjustada;
            posBuscador -= velocidadAjustada;

            evangelistas.forEach((pez, index) => {
                pez.style.left = (posEvangelista + offsets[index]) + 'vw';
            });
            buscador.style.left = posBuscador + 'vw';
        }

        if (posEvangelista < -15) {
            evangelistas.forEach(pez => pez.remove());
            buscador.remove();

            eventosActivos--;
            if (esCardumen) {
                cardumenActivo = false;
            }
            return;
        }

        animationId = requestAnimationFrame(animarPeces);
    }

    animationId = requestAnimationFrame(animarPeces);

    function realizarMilagro(pezGris) {
        setTimeout(() => {
            pezGris.classList.remove('pez-gris-actor');
            pezGris.classList.add('iluminado');

            setTimeout(() => {
                pezGris.classList.remove('iluminado');
                pezGris.classList.add('pez-azul-actor');
                estado = 'regresando';

                evangelistas.push(pezGris);
                offsets.push(Math.random() * 3 + 1);

            }, 2000);
        }, 1000);
    }
}