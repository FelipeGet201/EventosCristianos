using Microsoft.Extensions.Configuration;
using Npgsql;
using SixLabors.ImageSharp.Formats.Jpeg;
using Stripe;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Globales
{
    public static class Funciones
    {
        private static readonly string _claveSecreta = "RedAJP_Secreto_2026_ClaveSegura!!";
        private static readonly byte[] _aesKey = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(_claveSecreta));
        private static readonly byte[] _aesIv = new byte[16];

        // 1. CÁLCULO DE COMISIÓN DE MERCADO PAGO
        // Asegúrate de importar esto arriba:
        // using Microsoft.Extensions.Configuration;

        public static decimal CalcularPagoConComision(decimal montoBase, IConfiguration config)
        {
            // 1. Obtener valores de configuración (o usar defaults de Stripe MX si fallan)
            decimal tasa = config.GetValue<decimal>("Stripe:TasaPorcentaje", 0.036m); // 3.6%
            decimal fijo = config.GetValue<decimal>("Stripe:TasaFija", 3.0m);         // $3.00
            decimal iva = config.GetValue<decimal>("Stripe:IvaComision", 1.16m);      // 1.16 factor IVA

            // 2. FÓRMULA DE CÁLCULO INVERSO (Reverse Fee Calculation)
            // Queremos que: Total - (ComisiónStripe + IVA_Comisión) = MontoBase
            // Matemáticamente se despeja así:
            // Total = (MontoBase + (Fijo * IVA)) / (1 - (Tasa * IVA))

            decimal numerador = montoBase + (fijo * iva);
            decimal denominador = 1 - (tasa * iva);

            // Evitar división entre cero por seguridad
            if (denominador <= 0) return montoBase;

            decimal totalACobrar = numerador / denominador;

            // 3. Redondear hacia arriba AL CENTAVO (2 decimales)
            return Math.Ceiling(totalACobrar * 100m) / 100m;
        }

        /// <summary>
        /// Verifica si el usuario tiene un permiso específico en un módulo.
        /// </summary>
        /// <param name="user">El usuario actual (User)</param>
        /// <param name="modulo">Nombre clave del módulo (ej: "Fin_Caja")</param>
        /// <param name="tipo">Letra del permiso: 'L'ectura, 'E'scritura, 'M'odificar, 'A'dmin</param>
        public static bool TienePermiso(this IPrincipal user, Modulo modulo, Permiso tipo)
        {
            var claimsPrincipal = user as ClaimsPrincipal;
            if (claimsPrincipal == null || !claimsPrincipal.Identity.IsAuthenticated) return false;

            // Extraemos el string real (.Valor) de los objetos seguros
            string nombreModulo = modulo.Valor; // Ej: "Seguridad_Usuarios"
            string codigoAccion = tipo.Valor;   // Ej: "B"

            // Lógica de búsqueda en Claims
            var claimsDelModulo = claimsPrincipal.Claims
                .Where(c => c.Type == "PermisoDetalle" && c.Value.StartsWith(nombreModulo + "|"));

            foreach (var claim in claimsDelModulo)
            {
                var partes = claim.Value.Split('|');
                if (partes.Length < 2) continue;

                // Buscamos si la letra de la acción está en la lista del usuario
                if (partes[1].Split(',').Contains(codigoAccion))
                {
                    return true;
                }
            }

            return false;
        }

        // 2. BITÁCORA
        // Recibe 'conexion' (ya abierta) y 'transaccion' (opcional)
        public static async Task RegistrarBitacora(NpgsqlConnection conexion, int idUsuario, Modulo modulo, AccionBitacora accion, string detalle, string ip, NpgsqlTransaction? transaccion = null)
        {
            string sql = @"INSERT INTO ""Sist_Bitacora"" 
                   (""Id_Usuario"", ""Modulo"", ""Accion"", ""Detalle"", ""IP"", ""Fecha"")
                   VALUES (@id, @mod, @acc, @det, @ip, NOW())";

            // Reutilizamos la conexión que nos pasan
            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
            {
                cmd.Parameters.AddWithValue("@id", idUsuario == 0 ? (object)DBNull.Value : idUsuario);

                // Aquí extraemos el string real (.Valor) de tus clases seguras
                cmd.Parameters.AddWithValue("@mod", modulo.Valor);
                cmd.Parameters.AddWithValue("@acc", accion.Valor);

                cmd.Parameters.AddWithValue("@det", detalle);

                // Validación simple de IP por si llega nula
                cmd.Parameters.AddWithValue("@ip", string.IsNullOrEmpty(ip) ? "Local" : ip);

                await cmd.ExecuteNonQueryAsync();
            }
        }

        // =========================================================
        // 1. ORQUESTADOR DE CORREOS (Con Bitácora Completa)
        // =========================================================
        public static async Task<(bool Exito, string Error)> EnviarCorreo(IConfiguration config, string emailDestino, string asunto, string cuerpoHTML)
        {
            // 1. INTENTO PRINCIPAL: MAILRELAY
            var resultadoMR = await EnviarCorreoMailRelay(config, emailDestino, asunto, cuerpoHTML);

            if (resultadoMR.Exito)
            {
                // ÉXITO MAILRELAY: Registramos que salió bien y con quién
                await RegistrarLogCorreo(config, "MailRelay", true, $"Destino: {emailDestino} | Asunto: {asunto}");
                return (true, "");
            }
            else
            {
                // FALLO MAILRELAY: Registramos el error
                await RegistrarLogCorreo(config, "MailRelay", false, $"Destino: {emailDestino}, {resultadoMR.Error}");

                // 2. INTENTO DE RESPALDO: BREVO
                var resultadoBrevo = await EnviarCorreoBrevo(config, emailDestino, asunto, cuerpoHTML);

                if (resultadoBrevo.Exito)
                {
                    // ÉXITO BREVO (Respaldo)
                    await RegistrarLogCorreo(config, "Brevo", true, $"Destino: {emailDestino} | Asunto: {asunto} (Usado como respaldo)");
                    return (true, "Enviado con proveedor de respaldo (Brevo)");
                }
                else
                {
                    // FALLO TOTAL (Ambos)
                    await RegistrarLogCorreo(config, "Brevo", false, $"Destino: {emailDestino}, {resultadoBrevo.Error}");
                    return (false, $"Fallo total. MailRelay: {resultadoMR.Error} | Brevo: {resultadoBrevo.Error}");
                }
            }
        }

        // --- HELPER UNIFICADO DE BITÁCORA DE CORREOS ---
        private static async Task RegistrarLogCorreo(IConfiguration config, string proveedor, bool esExito, string detalleMensaje)
        {
            try
            {
                // A. Consultar Créditos (Try-Catch silencioso para no romper flujo)
                string infoCreditos = "N/A";
                int creditos = 0;
                try
                {
                    if (proveedor == "MailRelay") creditos = await ObtenerCreditosMailRelay(config);
                    else if (proveedor == "Brevo") creditos = await ObtenerCreditosBrevo(config);

                    infoCreditos = creditos.ToString("N0"); // Formato número (ej: 1,500)
                }
                catch { infoCreditos = "Error API"; }

                // B. Definir Acción y Mensaje Final
                var accionBitacora = esExito ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Error;
                string estadoTexto = esExito ? "ENVÍO EXITOSO" : "FALLO DE ENVÍO";

                string logFinal = $"[{estadoTexto}] Prov: {proveedor}. Créditos Restantes: {(creditos == -1 ? "N/A" : infoCreditos.ToString())}. Detalle: {detalleMensaje}";

                // C. Insertar en BD
                string cadenaConexion = config.GetConnectionString("MiConexion");
                using (var conexion = new NpgsqlConnection(cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Usamos ID 1 (Admin/Sistema) ya que esto corre en background
                    await RegistrarBitacora(conexion, 1, Parametros.Modulos.AlertasCorreos, accionBitacora, logFinal, "Sistema", null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error crítico guardando bitácora correo: {ex.Message}");
            }
        }

        // =========================================================
        // 2. MÉTODOS DE ENVÍO INDIVIDUALES (Privados)
        // =========================================================

        private static async Task<(bool Exito, string Error)> EnviarCorreoMailRelay(IConfiguration config, string emailDestino, string asunto, string cuerpoHTML)
        {
            try
            {
                string apiKey = config["MailRelay:ApiKey"];
                string dominioCuenta = config["MailRelay:Domain"];
                string remitenteEmail = "soporte@ministeriocorban.shop";
                string remitenteNombre = "Soporte Ministerio Corban";

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(dominioCuenta))
                    return (false, "Faltan credenciales de MailRelay.");

                // Separamos la cadena por comas o punto y coma, y quitamos espacios en blanco
                var listaDestinos = emailDestino.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(e => new { email = e.Trim(), name = "Usuario" })
                                                .ToArray();

                var datosCorreo = new
                {
                    from = new { email = remitenteEmail, name = remitenteNombre },
                    to = listaDestinos, // <--- Aquí pasamos el arreglo generado dinámicamente
                    subject = asunto,
                    html_part = cuerpoHTML,
                    text_part = "Por favor habilita HTML para ver este correo."
                };

                string jsonContenido = JsonSerializer.Serialize(datosCorreo);
                var httpContent = new StringContent(jsonContenido, Encoding.UTF8, "application/json");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("X-Auth-Token", apiKey);
                    string url = $"https://{dominioCuenta}/api/v1/send_emails";
                    var response = await client.PostAsync(url, httpContent);

                    if (response.IsSuccessStatusCode) return (true, "");

                    string errorRespuesta = await response.Content.ReadAsStringAsync();
                    return (false, $"Status: {response.StatusCode}. Detalle: {errorRespuesta}");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static async Task<(bool Exito, string Error)> EnviarCorreoBrevo(IConfiguration config, string emailDestino, string asunto, string cuerpoHTML)
        {
            try
            {
                string apiKey = config["ApiCorreos"]; // Brevo Key
                if (string.IsNullOrEmpty(apiKey)) return (false, "Falta ApiKey de Brevo.");

                // Separamos la cadena por comas o punto y coma, y quitamos espacios en blanco
                var listaDestinos = emailDestino.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(e => new { email = e.Trim(), name = "Usuario" })
                                                .ToArray();

                var datosCorreo = new
                {
                    sender = new { name = "Soporte Ministerio Corbán", email = "soporte@ministeriocorban.shop" },
                    to = listaDestinos, // <--- Aquí pasamos el arreglo generado
                    subject = asunto,
                    htmlContent = cuerpoHTML
                };

                string jsonContenido = JsonSerializer.Serialize(datosCorreo);
                var httpContent = new StringContent(jsonContenido, Encoding.UTF8, "application/json");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("api-key", apiKey);
                    var response = await client.PostAsync("https://api.brevo.com/v3/smtp/email", httpContent);

                    if (response.IsSuccessStatusCode) return (true, "");

                    string errorRespuesta = await response.Content.ReadAsStringAsync();
                    return (false, $"Status: {response.StatusCode}. Detalle: {errorRespuesta}");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // =========================================================
        // 3. CONSULTA DE CRÉDITOS
        // =========================================================

        public static async Task<int> ObtenerCreditosMailRelay(IConfiguration config)
        {
            return -1;//Temporalmente no hay manera de consultar créditos en MailRelay
            try
            {
                string apiKey = config["MailRelay:ApiKey"];
                string dominioCuenta = config["MailRelay:Domain"];

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(dominioCuenta)) return 9999;

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("X-Auth-Token", apiKey);
                    var response = await client.GetAsync($"https://{dominioCuenta}/api/v1/packages");

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            int totalDisponibles = 0;
                            foreach (var paquete in doc.RootElement.EnumerateArray())
                            {
                                if (paquete.GetProperty("status").GetString() == "active")
                                {
                                    int limite = paquete.GetProperty("fulfillment_limit").GetInt32();
                                    int usados = paquete.GetProperty("fulfillment_count").GetInt32();
                                    totalDisponibles += (limite - usados);
                                }
                            }
                            return totalDisponibles;
                        }
                    }
                }
                return 0;
            }
            catch { return 0; }
        }

        public static async Task<int> ObtenerCreditosBrevo(IConfiguration config)
        {
            try
            {
                string apiKey = config["ApiCorreos"];
                if (string.IsNullOrEmpty(apiKey)) return 0;

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("api-key", apiKey);
                    var response = await client.GetAsync("https://api.brevo.com/v3/account");

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            var root = doc.RootElement;
                            if (root.TryGetProperty("plan", out JsonElement planes))
                            {
                                foreach (var plan in planes.EnumerateArray())
                                {
                                    if (plan.GetProperty("creditsType").GetString() == "sendLimit")
                                    {
                                        return plan.GetProperty("credits").GetInt32();
                                    }
                                }
                            }
                        }
                    }
                }
                return 0;
            }
            catch { return 0; }
        }

        public static async Task<bool> EnviarCorreoGmail(IConfiguration config,string emailDestino, string asunto, string cuerpoHTML)
        {
            string sCuenta = config.GetValue<string>("Gmail:Cuenta",""); 
            string sPassword = config.GetValue<string>("Gmail:Password","");        

            try
            {
                // 1. CREDENCIALES DE ACCESO (Tu "Llave" para entrar al servidor)
                // Aquí OBLIGATORIAMENTE va tu gmail personal y tu contraseña de aplicación
                string usuarioAuth = sCuenta;
                string passwordAuth = sPassword; // La que generaste en Google

                // 2. DATOS DE QUIEN ENVÍA (La "Firma" del correo)
                // Aquí pones el correo que configuraste en la imagen que subiste
                string correoQueAparece = "soporte@lospescadores.org";
                string nombreQueAparece = "Soporte Los Pescadores";

                // Configuración del Servidor
                string host = "smtp.gmail.com";
                int puerto = 587;

                using (var smtp = new SmtpClient(host, puerto))
                {
                    smtp.EnableSsl = true;
                    smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
                    smtp.UseDefaultCredentials = false;

                    // AQUI ESTÁ EL TRUCO: Te logueas con GMAIL
                    smtp.Credentials = new NetworkCredential(usuarioAuth, passwordAuth);

                    // PERO construyes el mensaje diciendo que viene de SOPORTE
                    var fromAddress = new MailAddress(correoQueAparece, nombreQueAparece);
                    var msj = new MailMessage
                    {
                        From = fromAddress,
                        Subject = asunto,
                        Body = cuerpoHTML,
                        IsBodyHtml = true
                    };

                    // Aquí agregamos los correos uno por uno al objeto "To"
                    var listaCorreos = emailDestino.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var correo in listaCorreos)
                    {
                        msj.To.Add(correo.Trim());
                    }

                    // (Opcional) Refuerzo para evitar que salga "Enviado por..."
                    msj.Sender = fromAddress;
                    msj.ReplyToList.Add(fromAddress);

                    await smtp.SendMailAsync(msj);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error enviando correo: " + ex.Message);
                return false;
            }
        }
        public static async Task VincularUsuarioAGruposPendientes(int idUsuario, string telefono, NpgsqlConnection conexion, NpgsqlTransaction trans = null)
        {
            if (string.IsNullOrEmpty(telefono)) return;

            // Limpieza básica (Solo dígitos)
            string telLimpio = System.Text.RegularExpressions.Regex.Replace(telefono, "[^0-9]", "");
            if (telLimpio.Length < 10) return;

            // Match últimos 10 dígitos
            string matchPattern = telLimpio.Substring(telLimpio.Length - 10);

            // SQL: Actualiza y cuenta cuántos se actualizaron
            string sql = @"
        WITH rows AS (
            UPDATE ""Sist_Grupos_Miembros""
            SET ""Id_Usuario"" = @uid
            WHERE ""Id_Usuario"" IS NULL 
            AND ""Telefono_Origen"" LIKE '%' || @match
            RETURNING 1
        )
        SELECT count(*) FROM rows";

            using (var cmd = new NpgsqlCommand(sql, conexion, trans))
            {
                cmd.Parameters.AddWithValue("@uid", idUsuario);
                cmd.Parameters.AddWithValue("@match", matchPattern);

                long afectados = (long)await cmd.ExecuteScalarAsync();

                // BITÁCORA: Solo si hubo vinculación real
                if (afectados > 0)
                {
                    string ip = "Sistema"; // Opcional: pasar HttpContext
                                           // Asumiendo que tienes acceso a Parametros.Modulo desde aquí o lo pasas como argumento
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Parametros.Modulos.Usuarios,
                        Parametros.AccionesBitacora.Editar,
                        $"Vinculación automática a {afectados} grupo(s) de WhatsApp por coincidencia de teléfono.",
                        ip, trans);
                }
            }
        }
        public static string LimpiarTelefono(string telefono)
        {
            if (string.IsNullOrEmpty(telefono)) return "";

            // 1. Eliminar todo lo que no sea número
            string limpio = System.Text.RegularExpressions.Regex.Replace(telefono, "[^0-9]", "");

            // 2. Si tiene más de 10 dígitos, tomamos solo los últimos 10
            if (limpio.Length > 10)
            {
                return limpio.Substring(limpio.Length - 10);
            }

            // Si tiene 10 o menos, devolvemos lo que haya
            return limpio;
        }
        public static bool ValidarPasswordBasico(string password, out string mensajeError)
        {
            mensajeError = "";
            if (string.IsNullOrEmpty(password))
            {
                mensajeError = "La contraseña no puede estar vacía.";
                return false;
            }

            if (password.Length < 8)
            {
                mensajeError = "La contraseña debe tener al menos 8 caracteres.";
                return false;
            }

            // Validación básica: Al menos un número
            if (!System.Text.RegularExpressions.Regex.IsMatch(password, @"[0-9]"))
            {
                mensajeError = "La contraseña debe incluir al menos un número.";
                return false;
            }

            return true;
        }

        // =========================================================
        // 4. GENERADOR DE CÓDIGO DE ENTREGA (NUEVO)
        // =========================================================
        public static string GenerarCodigoEntrega()
        {
            // Lista de palabras temáticas (Bíblicas / Corbán)
            string[] palabras = {
                "PEZ", "RED", "MAR", "LUZ", "SAL", "FE", "SOL", "OLA",
                "RIO", "VIDA", "AMOR", "PAZ", "ROCA", "FARO", "NAVE",
                "VELA", "PROA", "ANCLA", "CORAL", "BRISA", "AGUA", "CIELO",
                "NUBE", "ARCA", "MONTE", "TRIGO", "PAN", "VINO", "ALFA",
                "OMEGA", "LEON", "OVEJA", "LIRIO", "PALMA", "CEDRO", "REINO",
                "GRACIA", "GOZO", "SENDA", "LAGO", "PLAYA", "ARENA", "ISLA",
                "MUELLE", "BARCO", "REMO", "TIMON", "MAPA", "NORTE", "SUR"
            };

            // Selecciona una palabra al azar
            string palabra = palabras[new Random().Next(palabras.Length)];

            // Agrega un número de 2 dígitos (10-99)
            int numero = new Random().Next(10, 99);

            // Resultado ejemplo: "ANCLA-45"
            return $"{palabra}-{numero}";
        }
        public static async Task<bool> EsModuloActivo(string cadenaConexion, Modulo nombreModulo)
        {            
            try
            {
                using (var conexion = new NpgsqlConnection(cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""Activo"" FROM ""Sist_Modulos"" WHERE ""Nombre_Clave"" = @nombre LIMIT 1";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@nombre", nombreModulo.Valor);
                        object resultado = await cmd.ExecuteScalarAsync();

                        // Si existe y es true, retorna true. Si es null o false, retorna false.
                        if (resultado != null && resultado != DBNull.Value)
                        {
                            return (bool)resultado;
                        }
                    }
                }
            }
            catch
            {
                // Si falla la BD, por seguridad asumimos que está activo o inactivo según prefieras. 
                // Aquí retorno false por seguridad
                return false;
            }

            return false; // Si no encontró el módulo
        }

        /// <summary>
        /// Encripta un ID individual (Ultra rápido, sin JSON)
        /// </summary>
        public static string EncriptarId(int id)
        {
            try
            {
                byte[] plainBytes = BitConverter.GetBytes(id);

                using (var aes = Aes.Create())
                using (var encryptor = aes.CreateEncryptor(_aesKey, _aesIv))
                {
                    byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    return Convert.ToBase64String(cipherBytes).TrimEnd('=').Replace("+", "-").Replace("/", "_");
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// Desencripta el ID individual
        /// </summary>
        public static int DesencriptarId(string token)
        {
            if (string.IsNullOrEmpty(token)) return 0;
            try
            {
                string base64 = token.Replace("-", "+").Replace("_", "/");
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                }

                byte[] cipherBytes = Convert.FromBase64String(base64);

                using (var aes = Aes.Create())
                using (var decryptor = aes.CreateDecryptor(_aesKey, _aesIv))
                {
                    byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return BitConverter.ToInt32(plainBytes, 0);
                }
            }
            catch { return 0; }
        }

        /// <summary>
        /// Encripta una lista de IDs (Empaquetado en bytes, sin JSON)
        /// </summary>
        public static string EncriptarIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return "";
            try
            {
                byte[] plainBytes = new byte[ids.Count * 4];
                for (int i = 0; i < ids.Count; i++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(ids[i]), 0, plainBytes, i * 4, 4);
                }

                using (var aes = Aes.Create())
                using (var encryptor = aes.CreateEncryptor(_aesKey, _aesIv))
                {
                    byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    return Convert.ToBase64String(cipherBytes).TrimEnd('=').Replace("+", "-").Replace("/", "_");
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// Desencripta una lista de IDs
        /// </summary>
        public static List<int> DesencriptarIds(string token)
        {
            if (string.IsNullOrEmpty(token)) return new List<int>();
            try
            {
                string base64 = token.Replace("-", "+").Replace("_", "/");
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                }

                byte[] cipherBytes = Convert.FromBase64String(base64);

                using (var aes = Aes.Create())
                using (var decryptor = aes.CreateDecryptor(_aesKey, _aesIv))
                {
                    byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

                    var lista = new List<int>();
                    for (int i = 0; i < plainBytes.Length; i += 4)
                    {
                        lista.Add(BitConverter.ToInt32(plainBytes, i));
                    }
                    return lista;
                }
            }
            catch { return new List<int>(); }
        }



        // --- VALIDACIÓN DE IP MEXICANA (BLINDAJE DE PRODUCCIÓN) ---
        private static readonly Dictionary<string, bool> _cachePaises = new Dictionary<string, bool>();

        public static async Task<bool> EsIpMexicana(string ip)
        {
            // 1. VALIDACIÓN INTERNA (Localhost / Red Privada)
            if (string.IsNullOrEmpty(ip)) return true;

            try
            {
                // Parseamos la IP para entender su naturaleza real
                if (System.Net.IPAddress.TryParse(ip, out var address))
                {
                    // Detecta automáticamente 127.0.0.1 y ::1 (localhost IPv6)
                    if (System.Net.IPAddress.IsLoopback(address)) return true;

                    // Opcional: Permitir rangos de red local (Intranet)
                    byte[] bytes = address.GetAddressBytes();
                    if (bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168)) return true;
                }
            }
            catch
            {
                // Si el parseo falla, seguimos con la validación externa
            }

            // 2. REVISAR CACHÉ (Para no saturar la API externa)
            if (_cachePaises.ContainsKey(ip)) return _cachePaises[ip];

            try
            {
                using (var client = new HttpClient())
                {
                    // Timeout estricto de 3 segundos
                    client.Timeout = System.TimeSpan.FromSeconds(3);

                    var response = await client.GetStringAsync($"http://ip-api.com/json/{ip}?fields=country");

                    using (var doc = System.Text.Json.JsonDocument.Parse(response))
                    {
                        if (doc.RootElement.TryGetProperty("country", out var elem))
                        {
                            string pais = elem.GetString();
                            // Verificamos si es México
                            bool esMexico = (pais != null && pais.Equals("Mexico", StringComparison.OrdinalIgnoreCase));

                            // Limpiar caché si es muy grande (evitar fuga de memoria)
                            if (_cachePaises.Count > 5000) _cachePaises.Clear();
                            _cachePaises[ip] = esMexico;

                            return esMexico;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // --- FAIL-CLOSED ---
                // Si la API falla, bloqueamos. Con el punto 1 ya no te bloqueará a ti en desarrollo.
                Console.WriteLine($"[SEGURIDAD] Fallo en verificación de IP externa {ip}: {ex.Message}");
                return false;
            }

            return false; // Si no es México confirmado, no entra.
        }

        public static async Task<bool> ObtenerParametroBool(string cadenaConexion, string claveParametro)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Buscamos el valor en la tabla Sist_Parametros
                    string sql = @"SELECT ""Valor"" FROM ""Sist_Parametros"" WHERE ""Clave"" = @clave AND ""Activo"" = true";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@clave", claveParametro);
                        var resultado = await cmd.ExecuteScalarAsync();

                        if (resultado != null && resultado != DBNull.Value)
                        {
                            string valor = resultado.ToString().ToLower().Trim();
                            // Soporta "true", "1", "si", "yes"
                            return valor == "true" || valor == "1" || valor == "si" || valor == "yes";
                        }
                    }
                }
            }
            catch
            {
                // Si falla la BD o no existe el parámetro, asumimos falso por seguridad
                return false;
            }
            return false;
        }

        // ENUM para claridad en el código
        public enum ModoVisualizacionImagen { Incrustado, VistaPrevia }

        /// <summary>
        /// Helper inteligente para transformar enlaces.
        /// Si es Google Drive, extrae el ID y genera el enlace correcto según el uso (IMG o A).
        /// Si no es Google (ej. Imgur, S3), devuelve la URL original.
        /// </summary>
        public static string NormalizarUrlImagen(string url, ModoVisualizacionImagen modo)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";

            // 1. Si no es de Google, lo dejamos pasar tal cual
            if (!url.Contains("google.com")) return url;

            string id = "";

            // CASO A: Formato estándar "/file/d/ID/..."
            var matchFile = System.Text.RegularExpressions.Regex.Match(url, @"/file/d/([a-zA-Z0-9_-]+)");
            if (matchFile.Success)
            {
                id = matchFile.Groups[1].Value;
            }
            // CASO B: Formato de parámetros "id=ID" (ej. open?id=... o uc?id=...)
            else
            {
                var matchId = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=([a-zA-Z0-9_-]+)");
                if (matchId.Success)
                {
                    id = matchId.Groups[1].Value;
                }
            }

            // Si encontramos un ID válido, construimos la URL específica
            if (!string.IsNullOrEmpty(id))
            {
                if (modo == ModoVisualizacionImagen.Incrustado)
                {
                    // Thumbnail para la etiqueta <IMG>
                    // sz=w1920 fuerza alta calidad y formato imagen puro (evita HTML wrapper)
                    return $"https://drive.google.com/thumbnail?id={id}&sz=w1920";
                }
                else
                {
                    // Preview para el enlace <A>
                    // El modo 'preview' carga el visor limpio de Google y evita errores 429 de descarga
                    return $"https://drive.google.com/file/d/{id}/preview";
                }
            }

            // Si falló la detección del ID, devolvemos la original
            return url;
        }

        /// <summary>
        /// Convierte un enlace de una imagen en bits de forma segura (Anti-SSRF).
        /// </summary>
        public static async Task<byte[]> ObtenerBytesImagen(string url, string webRoot)
        {
            if (string.IsNullOrEmpty(url)) return null;

            try
            {
                // 1. FILTRO DE SEGURIDAD: Ignorar placeholders conocidos
                // Evitar que peguen enlaces de sitios que bloquean servidores.
                if (url.Contains("kindpng.com") || url.Contains("placeholder")) return null;

                // 2. VALIDACIÓN DE URL ABSOLUTA
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult))
                {
                    // Si no es URL válida, pasamos a lógica local abajo
                }
                else
                {
                    // CASO A: IMAGEN REMOTA (HTTP/HTTPS)
                    if (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)
                    {
                        // --- BLINDAJE SSRF ---

                        // Lista Blanca (Whitelist)
                        string[] dominiosPermitidos = {
                            "drive.google.com",
                            "doc.googleusercontent.com",
                            "lh3.googleusercontent.com",
                            "googleusercontent.com"
                            // Agrega tu propio dominio si las imágenes se alojan ahí
                        };

                        bool dominioEsSeguro = dominiosPermitidos.Any(d => uriResult.Host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

                        // Bloqueo de Localhost y Metadata IPs
                        if (uriResult.IsLoopback || uriResult.Host == "169.254.169.254") return null;

                        if (!dominioEsSeguro) return null; // Fail-Closed

                        using (var client = new HttpClient())
                        {
                            client.Timeout = TimeSpan.FromSeconds(5);
                            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/99.0.0.0 Safari/537.36");
                            client.MaxResponseContentBufferSize = 5 * 1024 * 1024; // 5MB Max

                            // AHORA SÍ: El await es válido porque la firma es async Task<byte[]>
                            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                            if (response.IsSuccessStatusCode)
                            {
                                var contentType = response.Content.Headers.ContentType?.MediaType;
                                if (contentType == "image/jpeg" ||
                                    contentType == "image/png" ||
                                    contentType == "image/gif" ||
                                    contentType == "image/webp")
                                {
                                    return await response.Content.ReadAsByteArrayAsync();
                                }
                            }
                        }
                        return null;
                    }
                }

                // CASO B: IMAGEN LOCAL
                string cleanUrl = url.Replace("~", "")
                                     .Replace("/", Path.DirectorySeparatorChar.ToString())
                                     .TrimStart(Path.DirectorySeparatorChar);

                string path = Path.Combine(webRoot, cleanUrl);

                if (System.IO.File.Exists(path))
                {
                    // Path Traversal Check
                    if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(webRoot), StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    // Lectura asíncrona de archivo local
                    return await System.IO.File.ReadAllBytesAsync(path);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        /// <summary>
        /// Calcula el monto monetario exacto a descontar basado en las reglas del cupón.
        /// </summary>
        /// <param name="montoBase">El subtotal de la compra.</param>
        /// <param name="tipo">1 = Porcentaje, 2 = Monto Fijo.</param>
        /// <param name="valor">El valor numérico del descuento.</param>
        /// <param name="tope">El monto máximo a descontar (solo aplica en porcentajes).</param>
        /// <returns>El monto decimal que se debe restar al total.</returns>
        public static decimal CalcularMontoDescuento(decimal montoBase, int tipo, decimal valor, decimal? tope)
        {
            decimal descuento = 0;

            if (tipo == 2) // Monto Fijo
            {
                descuento = valor;
            }
            else // Porcentaje
            {
                descuento = montoBase * (valor / 100.0m);

                // Aplicar Techo (Tope Máximo)
                if (tope.HasValue && tope.Value > 0 && descuento > tope.Value)
                {
                    descuento = tope.Value;
                }
            }

            // Seguridad: No descontar más de lo que cuesta el producto (No dar dinero)
            if (descuento > montoBase) descuento = montoBase;

            return Math.Round(descuento, 2);
        }

        /// <summary>
        /// Consulta si una alerta está activa en BD y envía el correo a los destinatarios configurados.
        /// </summary>
        public static async Task EnviarAlertaPorBaseDatos(IConfiguration config, string claveAlerta, string asunto, string cuerpoHTML)
        {
            try
            {
                string correosDestino = "";
                bool alertaActiva = false;

                // Abrimos una conexión rápida e independiente para no afectar transacciones en curso
                string cadenaConexion = config.GetConnectionString("MiConexion");
                using (var conexion = new NpgsqlConnection(cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""Correos_Destino"", ""Activo"" FROM ""Sist_EnvioCorreos"" WHERE ""Clave_Evento"" = @clave LIMIT 1";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@clave", claveAlerta);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                alertaActiva = r["Activo"] != DBNull.Value && (bool)r["Activo"];
                                correosDestino = r["Correos_Destino"]?.ToString() ?? "";
                            }
                        }
                    }
                }

                // Si está activa y hay correos, mandamos llamar a tu Orquestador de Correos
                if (alertaActiva && !string.IsNullOrWhiteSpace(correosDestino))
                {
                    await EnviarCorreo(config, correosDestino, asunto, cuerpoHTML);
                }
            }
            catch (Exception ex)
            {
                // Silenciamos el error para no interrumpir el flujo del usuario (Fail-Safe)
                Console.WriteLine($"[SISTEMA] Error al disparar alerta '{claveAlerta}': {ex.Message}");
            }
        }

        // =========================================================
        // 5. ENVÍO DE CORREO DE VERIFICACIÓN 
        // =========================================================
        public static async Task<bool> EnviarCorreoVerificacion(IConfiguration config, NpgsqlConnection conexion, NpgsqlTransaction transaccion, int idUsuario,
            string nombre, string email, string ipUsuario, string urlBaseVerificacion)
        {
            try
            {
                // 1. Lógica de Tokens (Se mantiene intacta)
                string sqlDel = "DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\" = @id";
                using (var cmd = new NpgsqlCommand(sqlDel, conexion, transaccion))
                {
                    cmd.Parameters.AddWithValue("@id", idUsuario);
                    await cmd.ExecuteNonQueryAsync();
                }

                string token = Guid.NewGuid().ToString();
                string sqlTok = @"INSERT INTO ""Sist_Verificaciones"" (""Id_Usuario"", ""Token"", ""Fecha_Expiracion"") 
                                  VALUES (@id, @tok, NOW() + INTERVAL '24 hours')";

                using (var cmd = new NpgsqlCommand(sqlTok, conexion, transaccion))
                {
                    cmd.Parameters.AddWithValue("@id", idUsuario);
                    cmd.Parameters.AddWithValue("@tok", token);
                    await cmd.ExecuteNonQueryAsync();
                }

                // 2. Construir Correo (Se mantiene intacta)
                // Aquí construimos el link final sumando el token a la url base que nos manda el controlador
                string link = $"{urlBaseVerificacion}?token={token}";

                string cuerpoHTML = $@"
        <div style='font-family: Arial, sans-serif; padding: 20px; background-color: #f4f4f4;'>
            <div style='max-width: 500px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; border: 1px solid #ddd;'>
                <h2 style='margin-top: 0; color: #333;'>Verificación de Cuenta</h2>
                <p>Hola <strong>{nombre}</strong>,</p>
                <p>Para activar tu cuenta, por favor confirma tu correo electrónico haciendo clic en el siguiente botón:</p>
                <br>
                <div style='text-align: center;'>
                    <a href='{link}' style='background-color: #007bff; color: white; padding: 10px 20px; text-decoration: none; border-radius: 4px; font-weight: bold;'>VERIFICAR AHORA</a>
                </div>
                <br>
                <p style='font-size: 13px; color: #666;'>
                    Si el botón no funciona, copia y pega este enlace:<br>
                    <a href='{link}' style='color: #007bff;'>{link}</a>
                </p>
            </div>
        </div>";

                // 3. Envío
                var (enviado, errorDetalle) = await Funciones.EnviarCorreo(config, email, "Verificación de Cuenta", cuerpoHTML);

                if (enviado)
                {
                    return true;
                }
                else
                {
                    // Log de error (Se mantiene intacta)
                    try
                    {
                        string cadenaConexion = config.GetConnectionString("MiConexion");
                        using (var conLog = new NpgsqlConnection(cadenaConexion))
                        {
                            await conLog.OpenAsync();
                            await Funciones.RegistrarBitacora(conLog, 0, Parametros.Modulos.Registro, Parametros.AccionesBitacora.Error,
                                $"Fallo envío a {email}. Razón: {errorDetalle}", ipUsuario, null);
                        }
                    }
                    catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error Token: " + ex.Message);
                return false;
            }
        }
        public static async Task<byte[]> ComprimirImagen(IFormFile archivo)
        {
            using (var streamOriginal = archivo.OpenReadStream())
            {
                using (var image = await Image.LoadAsync(streamOriginal))
                {
                    // Redimensionar si es muy grande (Max 1000x1000)
                    int maxWidth = 1000;
                    int maxHeight = 1000;

                    if (image.Width > maxWidth || image.Height > maxHeight)
                    {
                        image.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Mode = ResizeMode.Max,
                            Size = new Size(maxWidth, maxHeight)
                        }));
                    }

                    // Comprimir a JPEG con 75% de calidad
                    using (var ms = new MemoryStream())
                    {
                        var encoder = new JpegEncoder() { Quality = 75 };
                        await image.SaveAsync(ms, encoder);
                        return ms.ToArray();
                    }
                }
            }
        }

        public static async Task<(string UrlTerminos, string UrlPrivacidad)> ObtenerDocumentosLegalesEventosAsync(NpgsqlConnection conexion)
        {
            string urlTerminos = "";
            string urlPrivacidad = "";

            try
            {
                string sql = @"
                    SELECT ""Id_Archivo"", ""Origen"", ""Url_Enlace"", ""Titulo""
                    FROM ""Rec_Archivos""
                    WHERE ""Origen"" IN ('Terminos_Eventos', 'Aviso_Privacidad', 'Formatos', 'Recursos')
                    ORDER BY ""Fecha_Creacion"" DESC";

                var items = new List<(int Id, string Origen, string UrlEnlace, string Titulo)>();

                using (var cmd = new NpgsqlCommand(sql, conexion))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        int id = r["Id_Archivo"] != DBNull.Value ? Convert.ToInt32(r["Id_Archivo"]) : 0;
                        string origen = r["Origen"]?.ToString() ?? "";
                        string url = r["Url_Enlace"]?.ToString() ?? "";
                        string titulo = r["Titulo"]?.ToString() ?? "";
                        items.Add((id, origen, url, titulo));
                    }
                }

                // 1. Prioridad: Coincidencia exacta por Origen ('Terminos_Eventos' y 'Aviso_Privacidad')
                foreach (var item in items)
                {
                    string urlFinal = !string.IsNullOrWhiteSpace(item.UrlEnlace) ? item.UrlEnlace : $"/Formatos/Ver/{item.Id}";

                    if (item.Origen == "Terminos_Eventos" && string.IsNullOrEmpty(urlTerminos))
                    {
                        urlTerminos = urlFinal;
                    }
                    else if (item.Origen == "Aviso_Privacidad" && string.IsNullOrEmpty(urlPrivacidad))
                    {
                        urlPrivacidad = urlFinal;
                    }
                }

                // 2. Fallback: Si no se encontró por Origen específico, buscar por palabras clave en el título
                if (string.IsNullOrEmpty(urlTerminos) || string.IsNullOrEmpty(urlPrivacidad))
                {
                    foreach (var item in items)
                    {
                        string urlFinal = !string.IsNullOrWhiteSpace(item.UrlEnlace) ? item.UrlEnlace : $"/Formatos/Ver/{item.Id}";
                        string tituloLower = item.Titulo.ToLowerInvariant();

                        if (string.IsNullOrEmpty(urlTerminos) && (tituloLower.Contains("termino") || tituloLower.Contains("término") || tituloLower.Contains("condicion") || tituloLower.Contains("condición")))
                        {
                            urlTerminos = urlFinal;
                        }
                        if (string.IsNullOrEmpty(urlPrivacidad) && (tituloLower.Contains("privacidad") || tituloLower.Contains("aviso")))
                        {
                            urlPrivacidad = urlFinal;
                        }
                    }
                }
            }
            catch { }

            return (urlTerminos, urlPrivacidad);
        }

        /// <summary>
        /// Valida la respuesta del token reCAPTCHA v2 con la API siteverify de Google.
        /// </summary>
        public static async Task<bool> ValidarRecaptchaAsync(string recaptchaResponse, string secretKey)
        {
            if (string.IsNullOrWhiteSpace(recaptchaResponse)) return false;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    var values = new Dictionary<string, string>
                    {
                        { "secret", secretKey },
                        { "response", recaptchaResponse }
                    };

                    var content = new FormUrlEncodedContent(values);
                    var response = await httpClient.PostAsync("https://www.google.com/recaptcha/api/siteverify", content);

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(jsonString))
                        {
                            if (doc.RootElement.TryGetProperty("success", out var successElement))
                            {
                                return successElement.GetBoolean();
                            }
                        }
                    }
                }
            }
            catch
            {
                // En caso de falla de red o tiempo de espera alcanzado
            }

            return false;
        }
    }

}