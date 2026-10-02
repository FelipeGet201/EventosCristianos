using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System.Runtime.Intrinsics.Arm;

namespace RedAJP.Controllers
{
    public class RegistroController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IConfiguration _config;
        private Parametros.Modulo Modulo = Parametros.Modulos.Registro;

        public RegistroController(IConfiguration configuration)
        {
            _config = configuration;
            _cadenaConexion = _config.GetConnectionString("MiConexion");
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (User.Identity!.IsAuthenticated) return RedirectToAction("Index", "Home");

            ViewBag.RecaptchaSiteKey = _config["ReCaptcha:SiteKey"] ?? "6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI";

            // CONSULTAR CRÉDITOS
            int restantes = await Funciones.ObtenerCreditosBrevo(_config);

            // Configuración: Parar si quedan menos de 100 (O sea, ya se usaron 200)
            int limiteSeguridad = 100;

            if (restantes <= limiteSeguridad)
            {
                ViewData["BloqueoRegistro"] = true; // Bandera para la vista

                // Mensaje visual al entrar
                MostrarMensaje("Registro Pausado",
                    $"Por hoy hemos alcanzado el límite de registros diarios. Por favor intenta mañana.",
                    TipoMensaje.Alerta);
            }

            return View();
        }

        /// <summary>
        /// Proceso de registro con validaciones, control de duplicados inteligente, manejo de transacciones y envío de correo de verificación.
        /// </summary>
        /// <param name="modelo">Es el modelo que contiene los datos del formulario de registro.</param>
        /// <returns>Retorna la vista de registro con mensajes de error o éxito según corresponda.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(Registro modelo)
        {
            string siteKey = _config["ReCaptcha:SiteKey"] ?? "6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI";
            ViewBag.RecaptchaSiteKey = siteKey;

            // 0. CONTROL DE CUOTA (BREVO)
            int restantes = await Funciones.ObtenerCreditosBrevo(_config);
            if (restantes <= 100)
            {
                MostrarMensaje("Cupo Lleno", "Lo sentimos, se han agotado los registros por el día de hoy. Intenta mañana.", TipoMensaje.Error);
                ViewData["BloqueoRegistro"] = true;
                return View(modelo);
            }

            // 0.1 PROTECCIÓN HONEYPOT ANTI-BOTS
            string honeypot = Request.Form["Website_Url_Hp"].ToString();
            if (!string.IsNullOrWhiteSpace(honeypot))
            {
                // Bot detectado por haber llenado el campo trampa invisible
                MostrarMensaje("Acceso Denegado", "Se ha detectado una actividad automatizada no permitida.", TipoMensaje.Error);
                return View(modelo);
            }

            // 0.2 VALIDACIÓN RECAPTCHA GOOGLE
            string recaptchaResponse = Request.Form["g-recaptcha-response"].ToString();
            string recaptchaSecret = _config["ReCaptcha:SecretKey"] ?? "6LeIxAcTAAAAAGG-vFI1TnRWxMZNFuojJ4WifJWe";

            bool captchaValido = await Funciones.ValidarRecaptchaAsync(recaptchaResponse, recaptchaSecret);
            if (!captchaValido)
            {
                ViewData["ErrorCaptcha"] = "Debes completar la casilla de verificación 'No soy un robot'.";
                MostrarMensaje("Verificación requerida", "Por favor marca la casilla 'No soy un robot' para verificar tu registro.", TipoMensaje.Alerta);
                return View(modelo);
            }

            // 1. VALIDACIONES DE FORMULARIO
            var listaErrores = new List<string>(); // Para acumular mensajes

            if (string.IsNullOrEmpty(modelo.NombreCompleto))
            {
                ViewData["ErrorNombre"] = "El nombre es obligatorio";
                listaErrores.Add("Falta el nombre completo.");
            }

            // --- VALIDACIÓN DE USUARIO MEJORADA ---
            if (string.IsNullOrEmpty(modelo.Nombre_Usuario))
            {
                ViewData["ErrorUser"] = "El usuario es obligatorio";
                listaErrores.Add("Falta el nombre de usuario.");
            }
            else
            {
                // Regex: Solo letras, números, guion bajo y punto. Sin espacios.
                if (!System.Text.RegularExpressions.Regex.IsMatch(modelo.Nombre_Usuario, @"^[a-zA-Z0-9_.]+$"))
                {
                    ViewData["ErrorUser"] = "El usuario no puede tener espacios ni caracteres especiales (solo letras, números, _ y .)";
                    listaErrores.Add("El formato del usuario es inválido (sin espacios).");
                }
            }

            if (string.IsNullOrEmpty(modelo.Email))
            {
                ViewData["ErrorEmail"] = "El correo es obligatorio";
                listaErrores.Add("Falta el correo electrónico.");
            }

            if (string.IsNullOrEmpty(modelo.Genero) || (modelo.Genero != "H" && modelo.Genero != "M"))
            {
                ViewData["ErrorGenero"] = "Género inválido";
                listaErrores.Add("Selecciona un género válido.");
            }

            // Validación Fecha
            if (modelo.Fecha_Nacimiento == DateTime.MinValue || modelo.Fecha_Nacimiento > DateTime.Now)
            {
                ViewData["ErrorFecha"] = "Fecha inválida";
                listaErrores.Add("La fecha de nacimiento no es válida.");
            }
            else
            {
                var hoy = DateTime.Today;
                var edad = hoy.Year - modelo.Fecha_Nacimiento.Year;
                if (modelo.Fecha_Nacimiento.Date > hoy.AddYears(-edad)) edad--;

                if (edad < 15)
                {
                    ViewData["ErrorFecha"] = "Debes tener al menos 15 años.";
                    listaErrores.Add("Edad mínima no cumplida (15 años).");
                }
            }

            // Validación Teléfono
            string telLimpioInput = Funciones.LimpiarTelefono(modelo.Telefono);
            if (string.IsNullOrEmpty(modelo.Telefono))
            {
                ViewData["ErrorTel"] = "El teléfono es obligatorio";
                listaErrores.Add("Falta el teléfono.");
            }
            else if (telLimpioInput.Length != 10)
            {
                ViewData["ErrorTel"] = "El teléfono debe tener 10 dígitos.";
                listaErrores.Add("El teléfono debe ser de 10 dígitos.");
            }

            // Validación Password
            if (string.IsNullOrEmpty(modelo.Password))
            {
                ViewData["ErrorPass"] = "La contraseña es obligatoria";
                listaErrores.Add("Falta la contraseña.");
            }
            else if (modelo.Password != modelo.ConfirmPassword)
            {
                ViewData["ErrorConfirm"] = "Las contraseñas no coinciden";
                listaErrores.Add("Las contraseñas no coinciden.");
            }
            else
            {
                if (!Funciones.ValidarPasswordBasico(modelo.Password, out string msgPass))
                {
                    ViewData["ErrorPass"] = msgPass;
                    listaErrores.Add(msgPass); // Agregamos el detalle específico de la contraseña
                }
            }

            if (listaErrores.Count > 0)
            {
                // Construimos un mensaje HTML con bullets para la burbuja
                string htmlErrores = "<ul>" + string.Join("", listaErrores.Select(e => $"<li>{e}</li>")) + "</ul>";
                MostrarMensaje("Datos Incorrectos", htmlErrores, TipoMensaje.Alerta); // Nota: Asegúrate que tu MostrarMensaje soporte HTML o usa un string simple con \n

                // Si tu MostrarMensaje no soporta HTML, usa esto:
                // string textoErrores = string.Join("\n", listaErrores);
                // MostrarMensaje("Corrige los siguientes errores", textoErrores, TipoMensaje.Alerta);

                return View(modelo);
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 2. VERIFICAR DUPLICADOS INTELIGENTE (Email, Usuario, Nombre, Teléfono)
                    string sqlCheck = @"SELECT ""Id_Usuario"", ""Email"", ""Nombre_Usuario"", ""NombreCompleto"", ""Telefono"", ""Email_Verificado"" 
                        FROM ""Sist_Usuarios"" 
                        WHERE ""Email"" = @em 
                           OR ""Nombre_Usuario"" = @usr 
                           OR ""NombreCompleto"" = @nom
                           OR (""Telefono"" IS NOT NULL AND ""Telefono"" LIKE @telSearch)";

                    int idExistente = 0;
                    bool estaVerificado = false;
                    string campoDuplicado = "";

                    using (var cmd = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmd.Parameters.AddWithValue("@em", modelo.Email);
                        cmd.Parameters.AddWithValue("@usr", modelo.Nombre_Usuario);
                        cmd.Parameters.AddWithValue("@nom", modelo.NombreCompleto);
                        // Buscamos que CONTENGA la secuencia de 10 dígitos
                        cmd.Parameters.AddWithValue("@telSearch", "%" + telLimpioInput + "%");

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (reader.Read())
                            {
                                idExistente = (int)reader["Id_Usuario"];
                                estaVerificado = (bool)reader["Email_Verificado"];

                                string dbEmail = reader["Email"].ToString();
                                string dbUser = reader["Nombre_Usuario"].ToString();
                                string dbNombre = reader["NombreCompleto"].ToString();

                                // Limpiamos lo que viene de BD
                                string dbTelLimpio = Funciones.LimpiarTelefono(reader["Telefono"]?.ToString());

                                if (string.Equals(dbEmail, modelo.Email, StringComparison.OrdinalIgnoreCase))
                                    campoDuplicado = "correo electrónico";
                                else if (string.Equals(dbUser, modelo.Nombre_Usuario, StringComparison.OrdinalIgnoreCase))
                                    campoDuplicado = "nombre de usuario";
                                else if (string.Equals(dbNombre, modelo.NombreCompleto, StringComparison.OrdinalIgnoreCase))
                                    campoDuplicado = "nombre completo";
                                // Comparación inteligente: Contains
                                else if (dbTelLimpio.Contains(telLimpioInput))
                                    campoDuplicado = "número de teléfono";
                            }
                        }
                    }

                    // CASO A: YA EXISTE Y YA ESTÁ VERIFICADO -> ERROR
                    if (idExistente > 0 && estaVerificado)
                    {
                        if (string.IsNullOrEmpty(campoDuplicado)) campoDuplicado = "dato registrado";
                        MostrarMensaje("Ya registrado", $"Este {campoDuplicado} ya pertenece a una cuenta activa. Por favor inicia sesión.", TipoMensaje.Error);
                        return View(modelo);
                    }

                    // INICIO DE TRANSACCIÓN
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int idFinal = 0;
                            bool esReenvio = false;

                            // Encriptamos la contraseña SIEMPRE antes de guardar
                            string passwordSeguro = BCrypt.Net.BCrypt.HashPassword(modelo.Password);

                            // CASO B: EXISTE PERO NO HA VERIFICADO -> ACTUALIZAR Y REENVIAR
                            if (idExistente > 0 && !estaVerificado)
                            {
                                esReenvio = true;
                                string sqlUpd = @"UPDATE ""Sist_Usuarios"" 
                                  SET ""NombreCompleto""=@nom, ""Telefono""=@tel, ""Fecha_Nacimiento""=@nac, ""Genero""=@gen, ""PasswordHash""=@pass, ""Fecha_Modificacion""=NOW()
                                  WHERE ""Id_Usuario""=@id";

                                using (var cmd = new NpgsqlCommand(sqlUpd, conexion, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@nom", modelo.NombreCompleto);
                                    cmd.Parameters.AddWithValue("@tel", modelo.Telefono);
                                    cmd.Parameters.AddWithValue("@nac", modelo.Fecha_Nacimiento);

                                    // Usamos el password hasheado
                                    cmd.Parameters.AddWithValue("@pass", passwordSeguro);

                                    cmd.Parameters.AddWithValue("@id", idExistente);
                                    cmd.Parameters.AddWithValue("@gen", modelo.Genero);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                                idFinal = idExistente;
                            }
                            // CASO C: USUARIO NUEVO -> INSERTAR
                            else
                            {
                                int idRolDefault = (int)(await new NpgsqlCommand("SELECT \"Id_Rol\" FROM \"Sist_Roles\" WHERE \"Es_Predeterminado\" = TRUE LIMIT 1", conexion, transaccion).ExecuteScalarAsync() ?? 0);

                                string sqlIns = @"INSERT INTO ""Sist_Usuarios"" 
                                  (""NombreCompleto"", ""Email"", ""Nombre_Usuario"", ""PasswordHash"", ""Id_Rol"", ""Activo"", ""Email_Verificado"", ""Telefono"", ""Fecha_Nacimiento"", ""Genero"")
                                  VALUES (@nom, @em, @usr, @pass, @rol, TRUE, FALSE, @tel, @nac, @gen)
                                  RETURNING ""Id_Usuario""";

                                using (var cmd = new NpgsqlCommand(sqlIns, conexion, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@nom", modelo.NombreCompleto);
                                    cmd.Parameters.AddWithValue("@em", modelo.Email);
                                    cmd.Parameters.AddWithValue("@usr", modelo.Nombre_Usuario);

                                    // Usamos el password hasheado
                                    cmd.Parameters.AddWithValue("@pass", passwordSeguro);

                                    cmd.Parameters.AddWithValue("@rol", idRolDefault);
                                    cmd.Parameters.AddWithValue("@tel", modelo.Telefono);
                                    cmd.Parameters.AddWithValue("@nac", modelo.Fecha_Nacimiento);
                                    cmd.Parameters.AddWithValue("@gen", modelo.Genero);
                                    idFinal = (int)await cmd.ExecuteScalarAsync();
                                }
                            }

                            // --- VINCULACIÓN AUTOMÁTICA A GRUPOS WHATSAPP ---
                            await Funciones.VincularUsuarioAGruposPendientes(idFinal, telLimpioInput, conexion, transaccion);

                            // --- 3. ENVIAR CORREO ---
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            // Generamos la URL base sin el token para mandarla a Funciones
                            string urlBase = Url.Action("Verificar", "Registro", null, Request.Scheme);

                            bool correoEnviado = await Funciones.EnviarCorreoVerificacion(_config, conexion, transaccion, idFinal, modelo.NombreCompleto, modelo.Email, ip, urlBase);

                            if (correoEnviado)
                            {
                                // Registrar Bitácora
                                string detalle = $"{modelo.Nombre_Usuario} ({modelo.Email})";
                                await Funciones.RegistrarBitacora(conexion, idFinal, Modulo, esReenvio ? Parametros.AccionesBitacora.ReenvioCorreoRegistro : Parametros.AccionesBitacora.RegistroUsuario, detalle, ip, transaccion);

                                await transaccion.CommitAsync();

                                if (esReenvio)
                                {
                                    MostrarMensaje("¡Correo Reenviado!", "Notamos que ya te habías registrado pero no validaste tu cuenta. Te hemos enviado un nuevo enlace de activación.", TipoMensaje.Exito);
                                }
                                else
                                {
                                    MostrarMensaje("¡Registro Exitoso!", "Tu cuenta ha sido creada. Hemos enviado un enlace de activación a tu correo.", TipoMensaje.Exito);
                                }

                                return View("ConfirmacionEnviada");
                            }
                            else
                            {
                                await transaccion.RollbackAsync();
                                MostrarMensaje("Error de Correo", "No pudimos enviar el correo de verificación. Intenta más tarde.", TipoMensaje.Error);
                                return View(modelo);
                            }
                        }
                        catch (Exception exInt)
                        {
                            await transaccion.RollbackAsync();
                            throw exInt;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "Ocurrió un problema procesando tu solicitud: " + ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }
        
        public async Task<IActionResult> Verificar(string token)
        {
            // Validación inicial
            if (string.IsNullOrEmpty(token))
            {
                MostrarMensaje("Error", "No se ha recibido el token de verificación.", TipoMensaje.Error);
                return RedirectToAction("Index", "Login"); 
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Buscar Token Válido
                            string sqlBuscar = @"SELECT ""Id_Usuario"" FROM ""Sist_Verificaciones"" 
                                         WHERE ""Token""=@t AND ""Fecha_Expiracion"" > NOW()";

                            int idUser = 0;
                            using (var cmd = new NpgsqlCommand(sqlBuscar, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@t", token);
                                var res = await cmd.ExecuteScalarAsync();
                                if (res != null) idUser = (int)res;
                            }

                            // 2. Si encontramos usuario, procedemos
                            if (idUser > 0)
                            {
                                // Obtener email para bitácora
                                string emailUsuario = "Desconocido";
                                string sqlDatos = @"SELECT ""Email"" FROM ""Sist_Usuarios"" WHERE ""Id_Usuario"" = @id";
                                using (var cmdDatos = new NpgsqlCommand(sqlDatos, conexion, transaccion))
                                {
                                    cmdDatos.Parameters.AddWithValue("@id", idUser);
                                    var result = await cmdDatos.ExecuteScalarAsync();
                                    if (result != null) emailUsuario = result.ToString();
                                }

                                // Activar usuario
                                await new NpgsqlCommand($"UPDATE \"Sist_Usuarios\" SET \"Email_Verificado\"=TRUE WHERE \"Id_Usuario\"={idUser}", conexion, transaccion).ExecuteNonQueryAsync();

                                // Borrar el token usado (para que no se pueda usar dos veces)
                                await new NpgsqlCommand($"DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\"={idUser}", conexion, transaccion).ExecuteNonQueryAsync();

                                // Bitácora
                                string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                                string detalle = $"Usuario verificó su cuenta exitosamente. Email: {emailUsuario}";
                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.VerificaCorreo, detalle, ipUsuario, transaccion);

                                await transaccion.CommitAsync();

                                // ÉXITO: USAMOS LA BURBUJA (ESTO YA ESTABA BIEN)
                                MostrarMensaje("Cuenta Verificada", "Has verificado tu cuenta correctamente. Ya puedes iniciar sesión.", TipoMensaje.Exito);
                                return RedirectToAction("Index", "Login");
                            }
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                // Si hay error de sistema, mostramos mensaje de error
                MostrarMensaje("Error del Sistema", "Ocurrió un problema al procesar la verificación.", TipoMensaje.Error);
                return RedirectToAction("Index", "Login");
            }

            // --- CAMBIO AQUÍ: ERROR DE TOKEN INVÁLIDO ---
            // Antes: TempData["Error"] = ...
            // Ahora: Usamos MostrarMensaje para que salga la burbuja roja
            MostrarMensaje("Enlace no válido", "El enlace de verificación es incorrecto o ha expirado. Intenta registrarte nuevamente para recibir uno nuevo.", TipoMensaje.Error);

            return RedirectToAction("Index", "Login");
        }
    }
}