/* =========================================================
   mascota.js - VERSIÓN OPTIMIZADA CON LÍMITES DE PANTALLA
   ========================================================= */
(function () {
    const URL_DONACION = '/Donaciones';
    const CLAVE_OCULTAR_TEMPORAL = 'mascotaCorazonOculta_v1';
    const MINUTOS_OCULTO = 15;
    const PROBABILIDAD_APARICION = 0.2;//1 Siempre, 0.5 para 50%, etc.

    if (typeof window === 'undefined' || window.innerWidth < 992) return;

    const reducirMovimiento = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    document.addEventListener('DOMContentLoaded', inicializarMascota);

    function inicializarMascota() {
        const mascota = document.getElementById('mascota-flotante');
        if (!mascota) return;

        // --- 1. LÓGICA DE SESIÓN Y APARICIÓN ---
        if (mascota.dataset.reset === 'true') {
            localStorage.removeItem('mascotaPosX');
            localStorage.removeItem('mascotaPosY');
            sessionStorage.removeItem('mascotaMostrarSesion');
        }

        let mostrarEnSesion = sessionStorage.getItem('mascotaMostrarSesion');
        if (!mostrarEnSesion) {
            mostrarEnSesion = Math.random() < PROBABILIDAD_APARICION ? 'true' : 'false';
            sessionStorage.setItem('mascotaMostrarSesion', mostrarEnSesion);
        }
        if (mostrarEnSesion === 'false') return;

        const ocultaHasta = parseInt(localStorage.getItem(CLAVE_OCULTAR_TEMPORAL) || '0', 10);
        if (ocultaHasta && Date.now() < ocultaHasta) return;

        mascota.classList.remove('mascota-oculta-inicial');
        mascota.style.setProperty('display', 'flex', 'important');

        // --- FUNCIÓN ANTI-PÉRDIDAS: Mantiene la mascota dentro del monitor ---
        function mantenerEnPantalla() {
            const rect = mascota.getBoundingClientRect();
            const margen = 20; // Espacio mínimo desde el borde
            const maxRight = window.innerWidth - rect.width - margen;
            const maxBottom = window.innerHeight - rect.height - margen;

            let x = parseFloat(mascota.style.left || rect.left);
            let y = parseFloat(mascota.style.top || rect.top);
            let corregido = false;

            if (x > maxRight) { x = maxRight; corregido = true; }
            if (y > maxBottom) { y = maxBottom; corregido = true; }
            if (x < margen) { x = margen; corregido = true; }
            if (y < margen) { y = margen; corregido = true; }

            if (corregido) {
                mascota.style.left = x + 'px';
                mascota.style.top = y + 'px';
                localStorage.setItem('mascotaPosX', x + 'px');
                localStorage.setItem('mascotaPosY', y + 'px');
                return true;
            }
            return false;
        }

        // --- 2. POSICIÓN INICIAL ---
        const posGuardadaX = localStorage.getItem('mascotaPosX');
        const posGuardadaY = localStorage.getItem('mascotaPosY');
        if (posGuardadaX && posGuardadaY) {
            mascota.style.left = posGuardadaX;
            mascota.style.top = posGuardadaY;
            mascota.style.right = 'auto';
            mascota.style.bottom = 'auto';
            // Verificamos inmediatamente si al cargar quedó fuera de la pantalla
            mantenerEnPantalla();
        }

        // --- 3. CACHÉ DE OJOS Y SEGUIMIENTO OPTIMIZADO ---
        const pupilas = Array.from(mascota.querySelectorAll('.pupila-mascota'));
        const ojos = Array.from(mascota.querySelectorAll('.ojo-mascota'));
        let rectsOjos = [];

        function actualizarCachéOjos() {
            rectsOjos = ojos.map(ojo => ojo.getBoundingClientRect());
        }

        function moverPupilas(clientX, clientY) {
            if (reducirMovimiento || !clientX) return;

            pupilas.forEach((pupila, idx) => {
                const rect = rectsOjos[idx];
                if (!rect) return;

                const centroX = rect.left + rect.width / 2;
                const centroY = rect.top + rect.height / 2;
                const dx = clientX - centroX;
                const dy = clientY - centroY;
                const angulo = Math.atan2(dy, dx);

                const radioMaximoX = (rect.width / 2) - 4;
                const radioMaximoY = (rect.height / 2) - 4;
                const limite = Math.min(radioMaximoX, radioMaximoY);

                const baseIzquierda = (rect.width - 8) / 2;
                const baseArriba = (rect.height - 8) / 2;

                pupila.style.left = (baseIzquierda + Math.cos(angulo) * limite) + 'px';
                pupila.style.top = (baseArriba + Math.sin(angulo) * limite) + 'px';
            });
        }

        let ticking = false;
        function alMoverPuntero(e) {
            const clientX = e.clientX !== undefined ? e.clientX : (e.touches && e.touches[0].clientX);
            const clientY = e.clientY !== undefined ? e.clientY : (e.touches && e.touches[0].clientY);

            if (!ticking) {
                window.requestAnimationFrame(() => {
                    moverPupilas(clientX, clientY);
                    ticking = false;
                });
                ticking = true;
            }
        }

        // Inicializar caché y suscribir a eventos
        actualizarCachéOjos();
        document.addEventListener('mousemove', alMoverPuntero);
        document.addEventListener('touchmove', alMoverPuntero, { passive: true });

        // --- RESIZE REACCIONA A CAMBIOS DE PANTALLA ---
        window.addEventListener('resize', () => {
            // Si la ventana cambia de tamaño, empujamos a la mascota adentro si quedó fuera
            if (mantenerEnPantalla()) {
                actualizarCachéOjos();
            } else {
                actualizarCachéOjos();
            }
        });

        // --- 4. CLIC Y CIERRE ---
        mascota.addEventListener('click', (evt) => {
            if (evt.target.closest('.boton-cerrar-mascota')) return;
            if (mascota.dataset.recienArrastrado === 'true') {
                mascota.dataset.recienArrastrado = 'false';
                return;
            }
            window.location.href = URL_DONACION;
        });

        const botonCerrar = mascota.querySelector('.boton-cerrar-mascota');
        if (botonCerrar) {
            botonCerrar.addEventListener('click', (e) => {
                e.stopPropagation();
                localStorage.setItem(CLAVE_OCULTAR_TEMPORAL, String(Date.now() + MINUTOS_OCULTO * 60000));
                mascota.style.setProperty('display', 'none', 'important');
            });
        }

        // --- 5. DRAG & DROP ---
        let arrastrando = false, inX = 0, inY = 0, elIzq = 0, elArr = 0;

        mascota.addEventListener('pointerdown', (e) => {
            if (e.target.closest('.boton-cerrar-mascota')) return;
            arrastrando = true;
            mascota.classList.add('arrastrando');
            const rect = mascota.getBoundingClientRect();
            inX = e.clientX; inY = e.clientY;
            elIzq = rect.left; elArr = rect.top;
            mascota.setPointerCapture(e.pointerId);
        });

        mascota.addEventListener('pointermove', (e) => {
            if (!arrastrando) return;
            mascota.style.left = (elIzq + (e.clientX - inX)) + 'px';
            mascota.style.top = (elArr + (e.clientY - inY)) + 'px';
            mascota.dataset.recienArrastrado = 'true';
        });

        mascota.addEventListener('pointerup', (e) => {
            if (!arrastrando) return;
            arrastrando = false;
            mascota.classList.remove('arrastrando');

            // Justo antes de guardar, aseguramos que la soltó dentro de la pantalla
            mantenerEnPantalla();

            localStorage.setItem('mascotaPosX', mascota.style.left);
            localStorage.setItem('mascotaPosY', mascota.style.top);

            actualizarCachéOjos();

            setTimeout(() => { mascota.dataset.recienArrastrado = 'false'; }, 150);
        });
    }
})();