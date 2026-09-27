using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales; 

namespace RedAJP.Controllers
{
    [Authorize]
    public class PerfilController : GlobalController
    {
        private readonly string _cadenaConexion;

        public PerfilController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }
        // GET: /Perfil
        public async Task<IActionResult> Index()
        {
            // 1. Obtener ID de la cookie
            var idClaim = User.FindFirst("IdUsuario");
            if (idClaim == null) return RedirectToAction("Login", "Home");

            int idUsuario = int.Parse(idClaim.Value);
            Usuario modelo = new Usuario();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 2. QUERY PARA LEER DATOS
                    string sql = "SELECT * FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\" = @id";

                    using (var comando = new NpgsqlCommand(sql, conexion))
                    {
                        comando.Parameters.AddWithValue("@id", idUsuario);

                        using (var lector = await comando.ExecuteReaderAsync())
                        {
                            if (lector.Read())
                            {
                                // 3. LLENAR EL MODELO MANUALMENTE
                                modelo.Id_Usuario = int.Parse(lector["Id_Usuario"].ToString());
                                modelo.NombreCompleto = lector["NombreCompleto"].ToString();
                                modelo.Email = lector["Email"].ToString();
                                modelo.Nombre_Usuario = lector["Nombre_Usuario"].ToString();
                                modelo.Telefono = lector["Telefono"] != DBNull.Value ? lector["Telefono"].ToString() : "";
                                modelo.Email_Temporal = lector["Email_Temporal"] != DBNull.Value ? lector["Email_Temporal"].ToString() : "";
                                ViewBag.TieneCambioPendiente = !string.IsNullOrEmpty(modelo.Email_Temporal);
                            }
                            else
                            {
                                return NotFound();
                            }
                        }
                    }

                    // 3. VERIFICAR SI YA CAMBIÓ SU NOMBRE

                    using (var cmdNombre = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Sist_Bitacora"" WHERE ""Id_Usuario"" = @id AND ""Detalle"" LIKE '%Actualizó su nombre%'", conexion))
                    {
                        cmdNombre.Parameters.AddWithValue("@id", idUsuario);
                        long cambios = (long)await cmdNombre.ExecuteScalarAsync();
                        ViewBag.PuedeCambiarNombre = (cambios == 0); // Si es 0, sí puede.
                    }

                    // 4. OBTENER LOS GRUPOS DEL USUARIO
                    using (var cmdGrupos = new NpgsqlCommand(@"
                    SELECT g.""Nombre_Grupo"" 
                    FROM ""Sist_Grupos_Whatsapp"" g 
                    JOIN ""Sist_Grupos_Miembros"" m ON g.""Id_Grupo"" = m.""Id_Grupo"" 
                    WHERE m.""Id_Usuario"" = @id", conexion))
                    {
                        cmdGrupos.Parameters.AddWithValue("@id", idUsuario);
                        var gruposDelUsuario = new List<string>();

                        using (var readerGrupos = await cmdGrupos.ExecuteReaderAsync())
                        {
                            while (await readerGrupos.ReadAsync())
                            {
                                gruposDelUsuario.Add(readerGrupos["Nombre_Grupo"].ToString());
                            }
                        }
                        // Guardamos la lista en el ViewBag para leerla en la vista
                        ViewBag.GruposUsuario = gruposDelUsuario;
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Error al cargar perfil: " + ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        /// <summary>
        /// Guardar los cambios realizados en el perfil del usuario.
        /// </summary>
        /// <param name="datosEditados">Es el modelo Usuario con los datos editados.</param>
        /// <returns>Retorna a la vista de perfil con mensajes de éxito o error.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(Usuario datosEditados)
        {
            // 0. Limpieza de Modelo
            ModelState.Remove("Nombre_Usuario");
            ModelState.Remove("PasswordHash");

            var idClaim = User.FindFirst("IdUsuario");
            if (idClaim == null) return RedirectToAction("Index", "Login");

            int idUsuario = int.Parse(idClaim.Value);

            // 1. VALIDACIÓN PREVIA DE TELÉFONO (10 DÍGITOS)
            string telLimpioInput = "";
            if (!string.IsNullOrEmpty(datosEditados.Telefono))
            {
                telLimpioInput = Funciones.LimpiarTelefono(datosEditados.Telefono);
                if (telLimpioInput.Length != 10)
                {
                    MostrarMensaje("Formato Incorrecto", "El teléfono debe tener 10 dígitos exactos.", TipoMensaje.Error);
                    return RedirectToAction("Index");
                }
            }

            // VALIDACIÓN ANTI-MANIPULACIÓN:
            if (string.IsNullOrWhiteSpace(datosEditados.Email))
            {
                MostrarMensaje("Error", "El correo electrónico no puede estar vacío.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // INICIO DE TRANSACCIÓN PARA PERFIL
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 2. VALIDAR QUE EL TELÉFONO NO LO USE OTRA PERSONA
                            if (!string.IsNullOrEmpty(telLimpioInput))
                            {
                                // Buscamos coincidencia avanzada (CONTAINS)
                                string sqlTelCheck = @"SELECT ""Telefono"" FROM ""Sist_Usuarios"" 
                                       WHERE ""Id_Usuario"" != @id 
                                       AND ""Telefono"" LIKE @search";

                                using (var cmdTel = new NpgsqlCommand(sqlTelCheck, conexion, trans))
                                {
                                    cmdTel.Parameters.AddWithValue("@id", idUsuario);
                                    cmdTel.Parameters.AddWithValue("@search", "%" + telLimpioInput + "%");

                                    using (var r = await cmdTel.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            string dbTel = Funciones.LimpiarTelefono(r["Telefono"]?.ToString());
                                            // Si la BD contiene mi número nuevo -> Error
                                            if (dbTel.Contains(telLimpioInput))
                                            {
                                                MostrarMensaje("Teléfono Ocupado", "El número de teléfono ya está asociado a otra cuenta.", TipoMensaje.Error);
                                                return RedirectToAction("Index");
                                            }
                                        }
                                    }
                                }
                            }

                            // 3. OBTENER PASSWORD, NOMBRE Y EMAIL ACTUAL REAL
                            string passActualBD = "";
                            string nombreActualBD = "";
                            string emailActualBD = "";
                            string sqlGet = "SELECT \"PasswordHash\", \"NombreCompleto\", \"Email\" FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\" = @id";

                            using (var cmdGet = new NpgsqlCommand(sqlGet, conexion, trans))
                            {
                                cmdGet.Parameters.AddWithValue("@id", idUsuario);
                                using (var readerGet = await cmdGet.ExecuteReaderAsync())
                                {
                                    if (await readerGet.ReadAsync())
                                    {
                                        passActualBD = readerGet["PasswordHash"]?.ToString() ?? "";
                                        nombreActualBD = readerGet["NombreCompleto"]?.ToString() ?? "";
                                        emailActualBD = readerGet["Email"]?.ToString() ?? "";
                                    }
                                }
                            }

                            // 4. LÓGICA DE VALIDACIONES (Nombre, Contraseña y Email)
                            bool actualizoNombre = false;
                            if (!string.IsNullOrWhiteSpace(datosEditados.NombreCompleto) && datosEditados.NombreCompleto.Trim() != nombreActualBD)
                            {
                                using (var cmdNombre = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Sist_Bitacora"" WHERE ""Id_Usuario"" = @id AND ""Detalle"" LIKE '%Actualizó su nombre%'", conexion, trans))
                                {
                                    cmdNombre.Parameters.AddWithValue("@id", idUsuario);
                                    if ((long)await cmdNombre.ExecuteScalarAsync() > 0)
                                    {
                                        MostrarMensaje("No permitido", "Ya has cambiado tu nombre anteriormente. Solo se permite una vez.", TipoMensaje.Error);
                                        return RedirectToAction("Index");
                                    }
                                }
                                actualizoNombre = true;
                            }

                            bool actualizarPass = false;
                            if (datosEditados.CambiarPassword)
                            {
                                if (string.IsNullOrEmpty(datosEditados.CurrentPassword)) { MostrarMensaje("Atención", "Ingresa tu contraseña actual.", TipoMensaje.Alerta); return RedirectToAction("Index"); }

                                bool currentPassEsValido = (!string.IsNullOrEmpty(passActualBD) && passActualBD.StartsWith("$2")) ? BCrypt.Net.BCrypt.Verify(datosEditados.CurrentPassword, passActualBD) : (passActualBD == datosEditados.CurrentPassword);

                                if (!currentPassEsValido) { MostrarMensaje("Error", "La contraseña actual es incorrecta.", TipoMensaje.Error); return RedirectToAction("Index"); }
                                if (datosEditados.NewPassword != datosEditados.ConfirmPassword) { MostrarMensaje("Error", "La confirmación no coincide.", TipoMensaje.Error); return RedirectToAction("Index"); }
                                if (!Funciones.ValidarPasswordBasico(datosEditados.NewPassword, out string msgPass)) { MostrarMensaje("Contraseña Débil", msgPass, TipoMensaje.Error); return RedirectToAction("Index"); }

                                actualizarPass = true;
                            }

                            // Validar si cambió el correo
                            bool actualizoEmail = false;

                            string nuevoEmailLimpiado = datosEditados.Email.Trim().ToLower();
                            if (nuevoEmailLimpiado != emailActualBD.Trim().ToLower())
                            {
                                // 1. Validar que no esté en uso por otro
                                using (var cmdEm = new NpgsqlCommand("SELECT COUNT(*) FROM \"Sist_Usuarios\" WHERE (\"Email\" = @em OR \"Email_Temporal\" = @em) AND \"Id_Usuario\" != @id", conexion, trans))
                                {
                                    cmdEm.Parameters.AddWithValue("@em", nuevoEmailLimpiado);
                                    cmdEm.Parameters.AddWithValue("@id", idUsuario);
                                    if ((long)await cmdEm.ExecuteScalarAsync() > 0)
                                    {
                                        MostrarMensaje("Correo en uso", "El correo ingresado ya pertenece a otra cuenta.", TipoMensaje.Error);
                                        return RedirectToAction("Index");
                                    }
                                }
                                actualizoEmail = true;
                            }

                            // 5. ACTUALIZAR USUARIO EN BD
                            string sqlUpdate = @"
                            UPDATE ""Sist_Usuarios"" 
                            SET ""Telefono"" = @tel" +
                                                        (actualizoEmail ? ", \"Email_Temporal\" = @emailTemp" : "") +
                                                        (actualizoNombre ? ", \"NombreCompleto\" = @nombre" : "") +
                                                        (actualizarPass ? ", \"PasswordHash\" = @newPass, \"Sello_Seguridad\" = gen_random_uuid()" : "") + @"
                            WHERE ""Id_Usuario"" = @id";

                            using (var comando = new NpgsqlCommand(sqlUpdate, conexion, trans))
                            {
                                comando.Parameters.AddWithValue("@tel", string.IsNullOrEmpty(datosEditados.Telefono) ? DBNull.Value : datosEditados.Telefono);
                                if (actualizoEmail) comando.Parameters.AddWithValue("@emailTemp", nuevoEmailLimpiado);
                                if (actualizoNombre) comando.Parameters.AddWithValue("@nombre", datosEditados.NombreCompleto.Trim());
                                if (actualizarPass) comando.Parameters.AddWithValue("@newPass", BCrypt.Net.BCrypt.HashPassword(datosEditados.NewPassword));
                                comando.Parameters.AddWithValue("@id", idUsuario);
                                await comando.ExecuteNonQueryAsync();
                            }

                            // 6. ENVIAR CORREO SI HUBÓ CAMBIO DE EMAIL
                            if (actualizoEmail)
                            {
                                // Limpiar tokens viejos
                                await new NpgsqlCommand($"DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\" = {idUsuario}", conexion, trans).ExecuteNonQueryAsync();

                                string token = Guid.NewGuid().ToString();
                                var cmdTok = new NpgsqlCommand("INSERT INTO \"Sist_Verificaciones\" (\"Id_Usuario\", \"Token\", \"Fecha_Expiracion\") VALUES (@id, @t, NOW() + INTERVAL '2 hours')", conexion, trans);
                                cmdTok.Parameters.AddWithValue("@id", idUsuario);
                                cmdTok.Parameters.AddWithValue("@t", token);
                                await cmdTok.ExecuteNonQueryAsync();

                                string link = Url.Action("ConfirmarCambioCorreo", "Perfil", new { t = token }, Request.Scheme);
                                string cuerpoHTML = $"<h3>Confirmación de Cambio de Correo</h3><p>Hola. Hemos recibido una solicitud para cambiar el correo de tu cuenta a esta dirección.</p><a href='{link}' style='padding: 10px 20px; background-color: #00334e; color: white; text-decoration: none; border-radius: 5px;'>Confirmar Nuevo Correo</a>";

                                // Asumiendo que tu IConfiguration se inyectó en el controlador
                                var configuration = HttpContext.RequestServices.GetService<IConfiguration>();
                                await Funciones.EnviarCorreo(configuration, nuevoEmailLimpiado, "Confirma tu nuevo correo", cuerpoHTML);
                            }

                            // 7. BITÁCORA
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
                            string detalle = $"Actualizó perfil. Tel: {datosEditados.Telefono}" +
                                             (actualizarPass ? " (Cambió Pass)" : "") +
                                             (actualizoNombre ? " (Actualizó su nombre)" : "") +
                                             (actualizoEmail ? $" (Solicitó cambio de email a {nuevoEmailLimpiado})" : "");

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Parametros.Modulos.Usuarios,
                                actualizoEmail ? Parametros.AccionesBitacora.SolicitaCambioCorreo : Parametros.AccionesBitacora.Editar,
                                detalle, ip, trans);

                            // 8. CONFIRMAR TRANSACCIÓN
                            await trans.CommitAsync();

                            if (actualizoEmail)
                            {
                                MostrarMensaje("Casi listo", "Tus datos se guardaron. Te enviamos un correo a tu nueva dirección; debes confirmarlo para que el cambio surta efecto.", TipoMensaje.Exito);
                            }
                            else
                            {
                                MostrarMensaje("Éxito", "¡Perfil actualizado con éxito!", TipoMensaje.Exito);
                            }
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Técnico", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }



        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ConfirmarCambioCorreo(string t)
        {
            if (string.IsNullOrEmpty(t))
            {
                MostrarMensaje("Error", "No se ha recibido el token de confirmación.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Buscar token
                        int idUsuario = 0;
                        using (var cmd = new NpgsqlCommand("SELECT \"Id_Usuario\" FROM \"Sist_Verificaciones\" WHERE \"Token\" = @t AND \"Fecha_Expiracion\" > NOW()", conexion, trans))
                        {
                            cmd.Parameters.AddWithValue("@t", t);
                            var res = await cmd.ExecuteScalarAsync();
                            if (res != null) idUsuario = (int)res;
                        }

                        if (idUsuario > 0)
                        {
                            // 1. OBTENER EL CORREO TEMPORAL PARA RE-VALIDAR
                            string emailTemp = "";
                            using (var cmdTemp = new NpgsqlCommand("SELECT \"Email_Temporal\" FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\" = @id", conexion, trans))
                            {
                                cmdTemp.Parameters.AddWithValue("@id", idUsuario);
                                emailTemp = (await cmdTemp.ExecuteScalarAsync())?.ToString();
                            }

                            if (!string.IsNullOrEmpty(emailTemp))
                            {
                                // 2. RE-VALIDAR QUE NADIE MÁS LO HAYA TOMADO MIENTRAS TANTO
                                using (var cmdEm = new NpgsqlCommand("SELECT COUNT(*) FROM \"Sist_Usuarios\" WHERE \"Email\" = @em AND \"Id_Usuario\" != @id", conexion, trans))
                                {
                                    cmdEm.Parameters.AddWithValue("@em", emailTemp);
                                    cmdEm.Parameters.AddWithValue("@id", idUsuario);
                                    if ((long)await cmdEm.ExecuteScalarAsync() > 0)
                                    {
                                        await trans.RollbackAsync();
                                        MostrarMensaje("Correo no disponible", "Lamentablemente, alguien más registró este correo mientras confirmabas. Solicita el cambio nuevamente con un correo distinto.", TipoMensaje.Error);
                                        return RedirectToAction("Index");
                                    }
                                }

                                // 3. HACER EL SWAP DE CORREOS SEGURO
                                string sqlUpdate = @"UPDATE ""Sist_Usuarios"" 
                             SET ""Email"" = ""Email_Temporal"", ""Email_Temporal"" = NULL, ""Email_Verificado"" = TRUE 
                             WHERE ""Id_Usuario"" = @id";

                                using (var cmdUpd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                                {
                                    cmdUpd.Parameters.AddWithValue("@id", idUsuario);
                                    int rows = await cmdUpd.ExecuteNonQueryAsync();

                                    if (rows > 0)
                                    {
                                        await new NpgsqlCommand($"DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\"={idUsuario}", conexion, trans).ExecuteNonQueryAsync();
                                        await Funciones.RegistrarBitacora(conexion, idUsuario, Parametros.Modulos.Usuarios, Parametros.AccionesBitacora.Editar, "Confirmó cambio de correo electrónico.", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);
                                        await trans.CommitAsync();

                                        MostrarMensaje("Correo Actualizado", "Tu correo ha sido cambiado y verificado correctamente.", TipoMensaje.Exito);
                                        return RedirectToAction("Index");
                                    }
                                }
                            }
                        }
                        await trans.RollbackAsync();
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            MostrarMensaje("Enlace inválido", "El enlace expiró o es incorrecto.", TipoMensaje.Error);
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarCambioEmail()
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await new NpgsqlCommand($"UPDATE \"Sist_Usuarios\" SET \"Email_Temporal\" = NULL WHERE \"Id_Usuario\" = {idUsuario}", conexion).ExecuteNonQueryAsync();
                    await new NpgsqlCommand($"DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\" = {idUsuario}", conexion).ExecuteNonQueryAsync();
                }
                MostrarMensaje("Cambio Cancelado", "Se canceló la solicitud de cambio de correo.", TipoMensaje.Info);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReenviarCorreoCambio()
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Validar Límite de 1 hora
                    using (var cmdTime = new NpgsqlCommand(@"SELECT MAX(""Fecha"") FROM ""Sist_Bitacora"" WHERE ""Id_Usuario"" = @id AND ""Detalle"" LIKE '%(Reenvió correo de confirmación de cambio)%'", conexion))
                    {
                        cmdTime.Parameters.AddWithValue("@id", idUsuario);
                        var lastTime = await cmdTime.ExecuteScalarAsync();
                        if (lastTime != DBNull.Value)
                        {
                            DateTime ultimaFecha = Convert.ToDateTime(lastTime);
                            if ((DateTime.Now - ultimaFecha).TotalHours < 1)
                            {
                                MostrarMensaje("Espera un momento", "Debes esperar al menos 1 hora para solicitar otro reenvío.", TipoMensaje.Alerta);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // Obtener Email_Temporal
                        string emailTemp = "";
                        using (var cmdGet = new NpgsqlCommand("SELECT \"Email_Temporal\" FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\" = @id", conexion, trans))
                        {
                            cmdGet.Parameters.AddWithValue("@id", idUsuario);
                            emailTemp = (await cmdGet.ExecuteScalarAsync())?.ToString();
                        }

                        if (!string.IsNullOrEmpty(emailTemp))
                        {
                            await new NpgsqlCommand($"DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\" = {idUsuario}", conexion, trans).ExecuteNonQueryAsync();

                            string token = Guid.NewGuid().ToString();
                            var cmdTok = new NpgsqlCommand("INSERT INTO \"Sist_Verificaciones\" (\"Id_Usuario\", \"Token\", \"Fecha_Expiracion\") VALUES (@id, @t, NOW() + INTERVAL '2 hours')", conexion, trans);
                            cmdTok.Parameters.AddWithValue("@id", idUsuario);
                            cmdTok.Parameters.AddWithValue("@t", token);
                            await cmdTok.ExecuteNonQueryAsync();

                            string link = Url.Action("ConfirmarCambioCorreo", "Perfil", new { t = token }, Request.Scheme);
                            string html = $"<h3>Confirmación de Cambio de Correo</h3><p>Hola. Solicitaste reenviar el correo para cambiar tu dirección.</p><a href='{link}' style='padding: 10px 20px; background-color: #00334e; color: white; text-decoration: none; border-radius: 5px;'>Confirmar Nuevo Correo</a>";

                            var configuration = HttpContext.RequestServices.GetService<IConfiguration>();
                            await Funciones.EnviarCorreo(configuration, emailTemp, "Confirma tu nuevo correo", html);

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Parametros.Modulos.Usuarios, Parametros.AccionesBitacora.Editar, "Usuario solicitó reenvío. (Reenvió correo de confirmación de cambio)", HttpContext.Connection.RemoteIpAddress?.ToString(), trans);
                            await trans.CommitAsync();

                            MostrarMensaje("Enviado", "Se reenvió el correo de confirmación. Revisa tu bandeja de entrada.", TipoMensaje.Exito);
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }
    }
}