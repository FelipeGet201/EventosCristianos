using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Encodings.Web; // Necesario para sanitización real
using System.Threading.Tasks;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Controllers
{
    public class GlobalController : Controller
    {
        public static string CadenaConexionGlobal { get; set; }
        public string sNombreConceptoDonacion = "[Aportación Recibida por Transferencia]";
        public string sAmbiente = "";
        public string sPrefijoTransfer = "AJP22901";
        public int nMisPaginasWeb = 0;
        public class CuentaBancariaInfo
        {
            public string Banco { get; set; }
            public string Titular { get; set; }
            public string Clabe { get; set; }
            public string Cuenta { get; set; }

            // Usado para donaciones (identifica si esta cuenta debe mostrarse en la página de ofrendas)
            public bool UsadoParaDonaciones { get; set; }
        }
        public static class DatosBancarios
        {
            // Diccionario "Casado" con el Código. 
            // La clave 'DEFAULT' siempre debe existir.
            private static readonly Dictionary<string, CuentaBancariaInfo> _cuentas = new Dictionary<string, CuentaBancariaInfo>
            {
                {
                    "DEFAULT", new CuentaBancariaInfo
                    {
                        Banco = "BBVA Bancomer",
                        Titular = "IGLESIA CRISTIANA INTERDENOMINACIONAL AR",
                        Clabe = "012840001970429573",
                        Cuenta = "0197042957",
                        UsadoParaDonaciones = true
                    }
                }
            };

            // <summary>
            // Obtiene la lista de claves disponibles para llenar el combo en el Editor.
            // </summary>
            //
            public static Dictionary<string, string> ObtenerCatalogoCuentas()
            {
                // Retorna Clave -> Texto para el Dropdown
                return _cuentas.ToDictionary(k => k.Key, v => $"{v.Value.Banco} - {v.Value.Titular}");
            }

            // <summary>
            // Obtiene la cuenta específica para un evento. Si la clave no existe o es nula, retorna la DEFAULT.
            // </summary>
            //
            public static CuentaBancariaInfo ObtenerCuenta(string clave)
            {
                if (string.IsNullOrEmpty(clave))
                {
                    return _cuentas["DEFAULT"];
                }

                if (int.TryParse(clave, out int idEvent))
                {
                    var cuentaBD = ObtenerCuentaDesdeBD(idEvent);
                    if (cuentaBD != null) return cuentaBD;
                }

                if (!_cuentas.ContainsKey(clave))
                {
                    return _cuentas["DEFAULT"];
                }
                return _cuentas[clave];
            }

            private static CuentaBancariaInfo ObtenerCuentaDesdeBD(int idEvento)
            {
                if (string.IsNullOrEmpty(GlobalController.CadenaConexionGlobal)) return null;

                try
                {
                    using (var con = new NpgsqlConnection(GlobalController.CadenaConexionGlobal))
                    {
                        con.Open();
                        string sql = @"SELECT ""Banco_Custom"", ""Titular_Custom"", ""Clabe_Custom"", ""Cuenta_Custom"" 
                                       FROM ""Eventos_Catalogo"" WHERE ""Id_Evento"" = @id";
                        using (var cmd = new NpgsqlCommand(sql, con))
                        {
                            cmd.Parameters.AddWithValue("@id", idEvento);
                            using (var r = cmd.ExecuteReader())
                            {
                                if (r.Read())
                                {
                                    return new CuentaBancariaInfo
                                    {
                                        Banco = r["Banco_Custom"] != DBNull.Value ? r["Banco_Custom"].ToString() : "",
                                        Titular = r["Titular_Custom"] != DBNull.Value ? r["Titular_Custom"].ToString() : "",
                                        Clabe = r["Clabe_Custom"] != DBNull.Value ? r["Clabe_Custom"].ToString() : "",
                                        Cuenta = r["Cuenta_Custom"] != DBNull.Value ? r["Cuenta_Custom"].ToString() : "",
                                        UsadoParaDonaciones = false
                                    };
                                }
                            }
                        }
                    }
                }
                catch
                {
                    return null;
                }
                return null;
            }

            /// <summary>
            /// NUEVO: Devuelve solo las cuentas marcadas para recibir donaciones públicas
            /// </summary>
            public static List<CuentaBancariaInfo> ObtenerCuentasParaDonaciones()
            {
                return _cuentas.Values.Where(c => c.UsadoParaDonaciones).ToList();
            }
        }
        protected async Task<bool> EsParametroActivo(string claveParametro)
        {
            // Obtenemos la cadena de conexión desde la inyección de dependencias del contexto actual
            var configuration = HttpContext.RequestServices.GetService<IConfiguration>();
            string cadenaConexion = configuration.GetConnectionString("MiConexion");

            // Delegamos la lógica a Funciones
            return await Funciones.ObtenerParametroBool(cadenaConexion, claveParametro);
        }

        // ==========================================
        // CONSTANTES DISPONIBLES PARA LOS HIJOS
        // ==========================================
        public Parametros.Permiso PermisoLeer => Parametros.Permisos.Leer;
        public Parametros.Permiso PermisoCrear => Parametros.Permisos.Crear;
        public Parametros.Permiso PermisoEditar => Parametros.Permisos.Editar;
        public Parametros.Permiso PermisoBorrar => Parametros.Permisos.Borrar;
        public Parametros.Permiso PermisoAdmin => Parametros.Permisos.Admin;


        public struct TipoMensaje
        {
            public const string Exito = "exito";
            public const string Error = "error";
            public const string Alerta = "alerta";
            public const string Info = "info";
        }

        /// <summary>
        /// Muestra el modal náutico global con protección XSS real.
        /// </summary>
        protected void MostrarMensaje(string titulo, string mensaje, string tipo = TipoMensaje.Info)
        {
            TempData["Error"] = null;
            TempData["Exito"] = null;
            TempData["Alerta"] = null;

            // FIX DE SEGURIDAD: Sanitización correcta para JavaScript
            // JavaScriptStringEncode maneja comillas, saltos de línea y caracteres especiales.
            // Nota: Se requiere System.Web o implementar un encoder simple, aquí usaremos reemplazo seguro manual 
            // o el encoder de .NET Core si inyectas JavaScriptEncoder.
            // Para simplicidad y robustez inmediata:

            string mensajeSeguro = "";
            if (!string.IsNullOrEmpty(mensaje))
            {
                // Codificamos para HTML para evitar inyección de etiquetas <script>
                mensajeSeguro = System.Net.WebUtility.HtmlEncode(mensaje);
                // Escapamos comillas para que no rompa el string JS '...'
                mensajeSeguro = mensajeSeguro.Replace("'", "&#39;").Replace("\"", "&quot;");
            }

            // 3. ASIGNAR
            TempData["Titulo"] = titulo;

            switch (tipo)
            {
                case TipoMensaje.Error:
                    TempData["Error"] = mensajeSeguro;
                    break;
                case TipoMensaje.Exito:
                    TempData["Exito"] = mensajeSeguro;
                    break;
                case TipoMensaje.Alerta:
                default:
                    TempData["Alerta"] = mensajeSeguro;
                    break;
            }
        }

        // =========================================================
        // RETORNO INTELIGENTE (ANTI-BUCLE)
        // =========================================================
        protected IActionResult RedirigirAtras(string mensaje, string tipo = "alerta")
        {
            MostrarMensaje("Atención", mensaje, tipo == "error" ? TipoMensaje.Error : TipoMensaje.Alerta);

            string referer = Request.Headers["Referer"].ToString();

            // Obtenemos el nombre del controlador actual (Ej: "Tienda")
            string currentController = ControllerContext.RouteData.Values["controller"]?.ToString();

            if (!string.IsNullOrEmpty(referer) && Uri.IsWellFormedUriString(referer, UriKind.RelativeOrAbsolute))
            {
                try
                {
                    Uri uriReferer = new Uri(referer);

                    // --- DETECCIÓN DE BUCLE ---
                    // 1. Si la URL anterior es IGUAL a la actual (Hizo Refresh F5)
                    // 2. O si la URL anterior contiene el nombre del controlador actual (Ej: venía de /Tienda/Carrito y va a /Tienda/Index)
                    //    significa que está atrapado dentro del módulo apagado.
                    bool esMismoSitio = uriReferer.AbsolutePath.Equals(Request.Path, StringComparison.OrdinalIgnoreCase);
                    bool esMismoModulo = !string.IsNullOrEmpty(currentController) &&
                                         uriReferer.AbsolutePath.Contains($"/{currentController}", StringComparison.OrdinalIgnoreCase);

                    if (esMismoSitio || esMismoModulo)
                    {
                        // ROMPEMOS EL CICLO: Lo mandamos a Nosotros
                        return RedirectToAction("Nosotros", "Home");
                    }

                    // Si viene de otro lado seguro (ej: Google, Facebook o el Home), lo regresamos ahí
                    return Redirect(referer);
                }
                catch
                {
                    // Si falla el análisis de URL, ruta segura
                    return RedirectToAction("Nosotros", "Home");
                }
            }

            // Default si escribió el link directo
            return RedirectToAction("Nosotros", "Home");
        }

        // =========================================================================
        // Validaciones globales para todas las acciones de cada ventana
        // =========================================================================
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var configuration = context.HttpContext.RequestServices.GetService<IConfiguration>();
            string cadenaConexion = configuration.GetConnectionString("MiConexion");

            CadenaConexionGlobal = cadenaConexion;

            // 1. Validaciones de Seguridad
            bool permisosOk = await ValidarPermisosActivos(context, cadenaConexion);
            // Si validación falla (false), el método ValidarPermisos ya redirigió, solo retornamos.
            if (!permisosOk) return;

            // 2. REGISTRO DE VISITAS OPTIMIZADO (Anti-Basura)
            try
            {
                await RegistrarVisitaOptimizado(context.HttpContext, cadenaConexion);

                // B. Si es Admin de Usuarios, obtenemos el contador para el Footer
                if (User.Identity.IsAuthenticated && User.TienePermiso(Modulos.Usuarios, PermisoLeer))
                {
                    // Nota de optimización: Esto sigue siendo una query por request para admins.
                    // Idealmente cachear este valor por 60 segundos.
                    using (var con = new NpgsqlConnection(cadenaConexion))
                    {
                        await con.OpenAsync();
                        // Contamos el total histórico
                        var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM \"Sist_Visitas\"", con);
                        long total = (long)await cmd.ExecuteScalarAsync();

                        // Lo guardamos en ViewBag para usarlo en el Layout
                        ((Controller)context.Controller).ViewBag.TotalVisitasAdmin = total;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error en módulo de visitas: " + ex.Message);
                // No detenemos el sitio si falla el contador
            }

            // 3. OBTENER AMBIENTE DE EJECUCIÓN
            try
            {
                sAmbiente = configuration["APP_ENVIRONMENT"]?.Trim().ToLower();
                ViewBag.sAmbiente = sAmbiente;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error en módulo de visitas: " + ex.Message);
                // No detenemos el sitio si falla el contador
            }

            // 4. CONTEO POR CLAIMS (Identidad del Usuario)
            if (context.HttpContext.User.Identity != null && context.HttpContext.User.Identity.IsAuthenticated)
            {
                var user = context.HttpContext.User;

                // Leemos el valor directamente del claim "nWebs" que creamos en el Login
                var nWebsClaim = User.FindFirst("nWebs");

                // Asignamos el valor a la variable global y al ViewBag
                nMisPaginasWeb = nWebsClaim != null ? int.Parse(nWebsClaim.Value) : 0;
                ((Controller)context.Controller).ViewBag.nMisPaginasWeb = nMisPaginasWeb;

                ((Controller)context.Controller).ViewBag.RequiereVinculacion = user.FindFirst("RequiereVinculacion")?.Value == "True";
                ((Controller)context.Controller).ViewBag.EstadoSolIglesia = user.FindFirst("EstadoSolIglesia")?.Value;
                ((Controller)context.Controller).ViewBag.MotivoRchIglesia = user.FindFirst("MotivoRchIglesia")?.Value;


                 bool bOcultarMascota = false;
                //La mascota bOcultarMascota se oculta si RequiereVinculacion es true y EstadoSolIglesia es "Pendiente" o "Rechazada"
                bOcultarMascota = user.FindFirst("RequiereVinculacion")?.Value == "True" &&
                (
                    user.FindFirst("EstadoSolIglesia")?.Value == null ||
                    user.FindFirst("EstadoSolIglesia")?.Value == "PEN" ||
                    user.FindFirst("EstadoSolIglesia")?.Value == "RCH"
                );
                ((Controller)context.Controller).ViewBag.bOcultarMascota = bOcultarMascota;
            }

            await next();
        }

        private async Task RegistrarVisitaOptimizado(HttpContext httpContext, string cadenaConexion)
        {
            var env = httpContext.RequestServices.GetService<IWebHostEnvironment>();

            // =========================================================
            // 1. FILTROS ESTRICTOS (Cero Basura)
            // =========================================================
            if (env != null && env.IsDevelopment()) return; // Ignorar tus pruebas en Visual Studio
            if (httpContext.Request.Method != "GET") return; // Ignorar envíos de formularios (POST)
            if (httpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest") return; // IGNORAR AJAX (Consultas en segundo plano)

            string urlActual = httpContext.Request.Path.Value?.ToLower() ?? "";

            // Filtro extra de seguridad: ignorar rutas de API o archivos estáticos
            if (urlActual.Contains("/api/") || urlActual.Contains("obtener")) return;

            // =========================================================
            // 2. LÓGICA DE RECORRIDO (Cooldown de 15 min por URL)
            // =========================================================
            string cookieKey = "AJP_Recorrido";
            string cookieValue = httpContext.Request.Cookies[cookieKey];
            long tiempoActual = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var paginasVisitadas = new Dictionary<string, long>();

            if (!string.IsNullOrEmpty(cookieValue))
            {
                var pares = cookieValue.Split('|', StringSplitOptions.RemoveEmptyEntries);
                foreach (var par in pares)
                {
                    var partes = par.Split("::");
                    if (partes.Length == 2 && long.TryParse(partes[1], out long tiempoGuardado))
                    {
                        // Si pasaron menos de 15 minutos (900 seg), recordamos que ya visitó esta página
                        if (tiempoActual - tiempoGuardado < 900)
                        {
                            paginasVisitadas[partes[0]] = tiempoGuardado;
                        }
                    }
                }
            }

            // =========================================================
            // 3. PREVENCIÓN DE SPAM (F5)
            // =========================================================
            if (paginasVisitadas.ContainsKey(urlActual))
            {
                // Ya registramos esta página exacta hace menos de 15 minutos.
                // No la guardamos en BD para evitar duplicados.
                return;
            }

            // =========================================================
            // 4. EXTRACCIÓN DE DATOS PARA BD
            // =========================================================
            string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
            string forwarded = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwarded)) ip = forwarded.Split(',')[0].Trim();

            string userAgent = httpContext.Request.Headers["User-Agent"].ToString();
            string dispositivo = "PC/Desktop";
            if (!string.IsNullOrEmpty(userAgent) && System.Text.RegularExpressions.Regex.IsMatch(userAgent, "Mobi|Android|iPhone|iPad", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                dispositivo = "Móvil/Tablet";
            }

            int idUsuario = 0;
            if (httpContext.User.Identity != null && httpContext.User.Identity.IsAuthenticated)
            {
                int.TryParse(httpContext.User.FindFirst("IdUsuario")?.Value, out idUsuario);
            }

            // =========================================================
            // 5. GUARDADO EN BASE DE DATOS
            // =========================================================
            try
            {
                using (var con = new NpgsqlConnection(cadenaConexion))
                {
                    await con.OpenAsync();
                    string sql = @"INSERT INTO ""Sist_Visitas"" 
           (""Ip_Address"", ""Url_Visitada"", ""User_Agent"", ""Dispositivo"", ""Id_Usuario"", ""Fecha"")
           VALUES (@ip, @url, @ua, @disp, @uid, NOW())";

                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@ip", ip);
                        cmd.Parameters.AddWithValue("@url", urlActual);
                        cmd.Parameters.AddWithValue("@ua", string.IsNullOrEmpty(userAgent) ? DBNull.Value : (object)userAgent);
                        cmd.Parameters.AddWithValue("@disp", dispositivo);
                        cmd.Parameters.AddWithValue("@uid", idUsuario);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                // =========================================================
                // 6. ACTUALIZAR COOKIE DEL NAVEGADOR
                // =========================================================
                paginasVisitadas[urlActual] = tiempoActual; // Añadimos la página actual al historial

                // Límite de seguridad: guardamos máximo las últimas 15 páginas para no hacer la cookie pesada
                var historialRecortado = paginasVisitadas.TakeLast(15);
                string nuevoValorCookie = string.Join("|", historialRecortado.Select(v => $"{v.Key}::{v.Value}"));

                httpContext.Response.Cookies.Append(cookieKey, nuevoValorCookie, new CookieOptions
                {
                    HttpOnly = true,
                    // AQUÍ ESTÁ LA MAGIA: Secure solo se activa si hay HTTPS. Evita que falle en local.
                    Secure = httpContext.Request.IsHttps,
                    Expires = DateTime.Now.AddMinutes(15),
                    SameSite = SameSiteMode.Lax
                });
            }
            catch (Exception)
            {
                // Fallo silencioso: si la base de datos se cae, no interrumpimos la navegación del usuario
            }
        }

        private async Task<bool> ValidarPermisosActivos(ActionExecutingContext context, string sCadenaConexion)
        {
            // Si no está logueado, no hay nada que validar, dejamos pasar (el atributo [Authorize] del hijo se encargará)
            if (!User.Identity.IsAuthenticated) return true;

            var idUserClaim = User.FindFirst("IdUsuario");
            var selloCookie = User.FindFirst("SelloSeguridad")?.Value;

            // Integridad básica de la cookie
            if (idUserClaim == null || selloCookie == null)
            {
                await ForzarCierreSesion(context);
                return false; // Detener flujo
            }

            try
            {
                string selloBD = "";

                using (var conexion = new NpgsqlConnection(sCadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Consulta ultra-rápida
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Sello_Seguridad"" FROM ""Sist_Usuarios"" WHERE ""Id_Usuario""=@id", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", int.Parse(idUserClaim.Value));
                        var result = await cmd.ExecuteScalarAsync();
                        if (result != null) selloBD = result.ToString();
                    }
                }

                // COMPARACIÓN CRÍTICA
                if (selloBD != selloCookie)
                {
                    // Los sellos no coinciden (Alguien cambió permisos/pass en BD)
                    await ForzarCierreSesion(context);
                    return false; // Detener flujo
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error crítico verificando sello: {ex.Message}");
                // FIX DE SEGURIDAD: Fail-Closed.
                // Si la BD falla, no podemos garantizar permisos, así que cerramos la puerta.
                await ForzarCierreSesion(context);
                return false;
            }

            return true;
        }

        private async Task ForzarCierreSesion(ActionExecutingContext context)
        {
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // Evitar bucles: si ya estamos en Login, no redirigir
            string controller = context.RouteData.Values["controller"]?.ToString();
            if (controller != "Login")
            {
                context.Result = new RedirectToActionResult("Index", "Login", null);
            }
        }
    }
}