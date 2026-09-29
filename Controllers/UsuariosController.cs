using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales;
using BCrypt.Net;

namespace RedAJP.Controllers
{
    [Authorize]
    public class UsuariosController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Usuarios;
        private readonly IConfiguration _config;

        public UsuariosController(IConfiguration configuration)
        {
            _config = configuration;
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permiso de lectura en esta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home"); 
            }

            var lista = new List<EditaUsuario>(); 
            var roles = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Cargar Roles
                    using (var cmdRoles = new NpgsqlCommand("SELECT \"Id_Rol\", \"Nombre\" FROM \"Sist_Roles\" WHERE \"Activo\"=TRUE", conexion))
                    using (var r = await cmdRoles.ExecuteReaderAsync())
                    {
                        while (r.Read()) roles.Add(new { Id = (int)r["Id_Rol"], Nombre = r["Nombre"].ToString() });
                    }

                    // Cargar Usuarios + Conteo (EXCLUYENDO 'Registro')
                    // Esto permite borrar usuarios que solo se registraron pero no hicieron nada más
                    string sql = @"
                        SELECT u.*, r.""Nombre"" as ""Rol"",
                               (SELECT COUNT(*) FROM ""Sist_Bitacora"" b 
                                WHERE b.""Id_Usuario"" = u.""Id_Usuario"" 
                                AND b.""Modulo"" != 'Registro') as ""ConteoBitacora""
                        FROM ""Sist_Usuarios"" u
                        INNER JOIN ""Sist_Roles"" r ON u.""Id_Rol"" = r.""Id_Rol""
                        ORDER BY u.""NombreCompleto"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (reader.Read())
                        {
                            long historial = (long)reader["ConteoBitacora"];

                            lista.Add(new EditaUsuario
                            {
                                Id_Usuario = (int)reader["Id_Usuario"],
                                NombreCompleto = reader["NombreCompleto"].ToString(),
                                Email = reader["Email"].ToString(),
                                Nombre_Usuario = reader["Nombre_Usuario"].ToString(),
                                Activo = (bool)reader["Activo"],
                                Email_Verificado = (bool)reader["Email_Verificado"],
                                Telefono = reader["Telefono"]?.ToString() ?? "",
                                Id_Rol = (int)reader["Id_Rol"],
                                Nombre_Rol = reader["Rol"].ToString(),
                                TieneHistorial = (historial > 0)
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { 
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            ViewBag.Roles = roles;
            return View(lista);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Editar(EditaUsuario modelo)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edición en esta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. OBTENER EL ROL ACTUAL (Para comparar si hubo cambio de rol)
                            int idRolActual = 0;
                            string sqlCheck = @"SELECT ""Id_Rol"" FROM ""Sist_Usuarios"" WHERE ""Id_Usuario""=@id";
                            using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion, transaccion))
                            {
                                cmdCheck.Parameters.AddWithValue("@id", modelo.Id_Usuario);
                                var res = await cmdCheck.ExecuteScalarAsync();
                                if (res != null) idRolActual = Convert.ToInt32(res);
                            }

                            // 2. PREPARAR CAMBIO DE ROL
                            var sSetRol = "";
                            bool cambioRol = false;

                            if (modelo.Id_Rol > 0)
                            {
                                if (modelo.Id_Rol != idRolActual)
                                {
                                    // Se actualiza Id_Rol y se regenera el Sello_Seguridad para invalidar sesiones previas si cambió de rol
                                    sSetRol = ", \"Id_Rol\"=@rol, \"Sello_Seguridad\" = gen_random_uuid()";
                                    cambioRol = true;
                                }
                                else
                                {
                                    sSetRol = ", \"Id_Rol\"=@rol";
                                }
                            }

                            string sql = $@"UPDATE ""Sist_Usuarios"" 
                                   SET ""NombreCompleto""=@nom, 
                                       ""Email""=@em, 
                                       ""Nombre_Usuario""=@usr 
                                       {sSetRol} 
                                   WHERE ""Id_Usuario""=@id";

                            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@nom", modelo.NombreCompleto);
                                cmd.Parameters.AddWithValue("@em", modelo.Email);
                                cmd.Parameters.AddWithValue("@usr", modelo.Nombre_Usuario);
                                cmd.Parameters.AddWithValue("@id", modelo.Id_Usuario);

                                if (modelo.Id_Rol > 0)
                                {
                                    cmd.Parameters.AddWithValue("@rol", modelo.Id_Rol);
                                }

                                await cmd.ExecuteNonQueryAsync();
                            }

                            // 3. BITÁCORA
                            string msg = $"Editó perfil de ID: {modelo.Id_Usuario} ({modelo.Nombre_Usuario})";
                            if (cambioRol) msg += $". CAMBIO DE ROL (de {idRolActual} a {modelo.Id_Rol}): Sesión invalidada.";

                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar,
                                msg, ip, transaccion);

                            await transaccion.CommitAsync();
                            MostrarMensaje("Usuario Actualizado", "Los cambios se guardaron correctamente.", TipoMensaje.Exito);
                        }
                        catch { await transaccion.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CambiarPassword(int idUserPass, string nuevaPass)
        {
            // 1. SOLO ADMIN PUEDE ENTRAR
            if (!User.TienePermiso(Modulo, PermisoAdmin))
            {
                MostrarMensaje("Acceso Denegado", "Solo los Administradores pueden cambiar contraseñas.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            // 2. VALIDAR REQUISITOS DE CONTRASEÑA
            if (!Funciones.ValidarPasswordBasico(nuevaPass, out string msgError))
            {
                // Si no cumple (muy corta, sin números, etc.), mostramos error y no hacemos nada
                MostrarMensaje("Contraseña Débil", msgError, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                // 3. ENCRIPTAR (BCrypt)
                string hash = BCrypt.Net.BCrypt.HashPassword(nuevaPass);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 4. ACTUALIZAR EN BD + REGENERAR SELLO
                            string sqlUpdate = @"UPDATE ""Sist_Usuarios"" 
                                         SET ""PasswordHash"" = @p, 
                                             ""Sello_Seguridad"" = gen_random_uuid() 
                                         WHERE ""Id_Usuario"" = @id";

                            var cmd = new NpgsqlCommand(sqlUpdate, conexion, transaccion);
                            cmd.Parameters.AddWithValue("@p", hash);
                            cmd.Parameters.AddWithValue("@id", idUserPass);

                            int filas = await cmd.ExecuteNonQueryAsync();

                            if (filas > 0)
                            {
                                // 5. REGISTRAR BITÁCORA
                                await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo,
                                    Parametros.AccionesBitacora.CambiaContraseña,
                                    $"Cambió contraseña al usuario ID {idUserPass}. Sesiones invalidadas.",
                                    ip, transaccion);

                                await transaccion.CommitAsync();
                                MostrarMensaje("Contraseña Actualizada", "El cambio se aplicó correctamente. El usuario deberá iniciar sesión de nuevo.", TipoMensaje.Exito);
                            }
                            else
                            {
                                await transaccion.RollbackAsync();
                                MostrarMensaje("Error", "No se encontró el usuario especificado.", TipoMensaje.Error);
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
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleEstado(int id, bool estadoActual)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edicion en esta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            bool nuevoEstado = !estadoActual;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            var cmd = new NpgsqlCommand("UPDATE \"Sist_Usuarios\" SET \"Activo\"=@st WHERE \"Id_Usuario\"=@id", conexion, transaccion);
                            cmd.Parameters.AddWithValue("@st", nuevoEstado);
                            cmd.Parameters.AddWithValue("@id", id);
                            await cmd.ExecuteNonQueryAsync();

                            string accion = nuevoEstado ? "Reactivar" : "Desactivar";
                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar, $"Cambió estado usuario ID {id} a {nuevoEstado}", ip, transaccion);

                            await transaccion.CommitAsync();
                        }
                        catch { await transaccion.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { 
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int idEliminar)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permiso de borrado en esta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDACIÓN PREVIA (EXCLUYENDO REGISTRO)
                    string sqlCheck = $"SELECT COUNT(*) FROM \"Sist_Bitacora\" WHERE \"Id_Usuario\" = @id AND \"Modulo\" != '{Parametros.Modulos.Registro}'";
                    using (var cmdC = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdC.Parameters.AddWithValue("@id", idEliminar);
                        if ((long)await cmdC.ExecuteScalarAsync() > 0)
                        {
                            MostrarMensaje("Imposible Eliminar", "El usuario tiene historial operativo y no puede ser eliminado.", TipoMensaje.Alerta);
                            return RedirectToAction("Index");
                        }
                    }

                    // 2. RECUPERAR DATOS DEL USUARIO (PARA EL ÚLTIMO REGISTRO EN BITÁCORA)
                    string datosUsuario = "";
                    using (var cmdGet = new NpgsqlCommand("SELECT * FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\"=@id", conexion))
                    {
                        cmdGet.Parameters.AddWithValue("@id", idEliminar);
                        using (var r = await cmdGet.ExecuteReaderAsync())
                        {
                            if (r.Read())
                            {
                                datosUsuario = $"ID:{idEliminar}, Usr:{r["Nombre_Usuario"]}, Nom:{r["NombreCompleto"]}, Email:{r["Email"]}, Rol:{r["Id_Rol"]}";
                            }
                        }
                    }

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // A. REGISTRAR EN BITÁCORA (CON EL ID DEL USUARIO A BORRAR)
                            // Esto dejará un registro "huérfano" (sin padre en Sist_Usuarios) si la BD lo permite, 
                            // o fallará si hay FK restrictiva. Como pediste usar SU ID, asumo que Sist_Bitacora no tiene FK o es flexible.
                            await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Borrar,
                                $"Usuario eliminado del sistema. Datos previos: {datosUsuario}", ip, transaccion);

                            // B. BORRAR DEPENDENCIAS (Verificaciones)
                            var cmdVer = new NpgsqlCommand("DELETE FROM \"Sist_Verificaciones\" WHERE \"Id_Usuario\"=@id", conexion, transaccion);
                            cmdVer.Parameters.AddWithValue("@id", idEliminar);
                            await cmdVer.ExecuteNonQueryAsync();

                            // C. BORRAR USUARIO
                            var cmdDel = new NpgsqlCommand("DELETE FROM \"Sist_Usuarios\" WHERE \"Id_Usuario\"=@id", conexion, transaccion);
                            cmdDel.Parameters.AddWithValue("@id", idEliminar);
                            await cmdDel.ExecuteNonQueryAsync();

                            await transaccion.CommitAsync();
                            MostrarMensaje("Usuario Eliminado", "El usuario fue eliminado correctamente y respaldado en la bitácora.", TipoMensaje.Exito);
                        }
                        catch { await transaccion.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { 
                MostrarMensaje("Error del Sistema", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> ConsultarIntentosCorreo(int idUsuario)
        {
            int totalIntentos = 0;
            string fechaUltimo = "";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Filtramos por las acciones de registro o reenvío guardadas en tu bitácora
                    string sql = @"
                SELECT COUNT(*) as Total, MAX(""Fecha"") as UltimaFecha 
                FROM ""Sist_Bitacora"" 
                WHERE ""Id_Usuario"" = @id 
                AND (""Accion"" = @accReenvio OR ""Accion"" = @accRegistro)";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idUsuario);
                        cmd.Parameters.AddWithValue("@accReenvio", Parametros.AccionesBitacora.ReenvioCorreoRegistro.Valor);
                        cmd.Parameters.AddWithValue("@accRegistro", Parametros.AccionesBitacora.RegistroUsuario.Valor);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                totalIntentos = reader["Total"] != DBNull.Value ? Convert.ToInt32(reader["Total"]) : 0;
                                if (reader["UltimaFecha"] != DBNull.Value)
                                {
                                    fechaUltimo = Convert.ToDateTime(reader["UltimaFecha"]).ToString("dd/MM/yyyy hh:mm tt");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // En caso de error, puedes loguearlo, pero devolvemos 0 para la interfaz
                Console.WriteLine(ex.Message);
            }

            return Json(new { totalIntentos, fechaUltimo });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReenviarVerificacion(int idUsuario)
        {
            if (!User.TienePermiso(Parametros.Modulos.Usuarios, Parametros.Permisos.Editar)) { 
                MostrarMensaje("Acceso Denegado", "No tienes permiso para realizar esta acción.", TipoMensaje.Alerta);
                return RedirectToAction("SinPermiso", "Home"); 
            }

            try
            {
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                string urlBase = Url.Action("Verificar", "Registro", null, Request.Scheme);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Obtener los datos del usuario que necesita el correo
                    string email = "";
                    string nombre = "";

                    string sqlDatos = @"SELECT ""Email"", ""NombreCompleto"" FROM ""Sist_Usuarios"" WHERE ""Id_Usuario"" = @id AND ""Email_Verificado"" = FALSE";
                    using (var cmd = new NpgsqlCommand(sqlDatos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idUsuario);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                email = reader["Email"].ToString();
                                nombre = reader["NombreCompleto"].ToString();
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(email))
                    {
                        MostrarMensaje("Atención", "No se encontró el usuario o ya está verificado.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    // 2. Iniciar transacción para generar los tokens de manera segura
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 3. Reutilizar la función centralizada
                            bool enviado = await Funciones.EnviarCorreoVerificacion(_config, conexion, transaccion, idUsuario, nombre, email, ip, urlBase);

                            if (enviado)
                            {
                                // Registramos la acción exitosa en la bitácora
                                await Funciones.RegistrarBitacora(conexion, idUsuario, Parametros.Modulos.Usuarios,
                                    Parametros.AccionesBitacora.ReenvioCorreoRegistro,
                                    $"Reenvío manual de correo de verificación a {email}", ip, transaccion);

                                await transaccion.CommitAsync();
                                MostrarMensaje("¡Correo Enviado!", $"Se envió un nuevo enlace de activación a {email}.", TipoMensaje.Exito);
                            }
                            else
                            {
                                await transaccion.RollbackAsync();
                                MostrarMensaje("Error", "No pudimos enviar el correo de verificación. Intenta más tarde.", TipoMensaje.Error);
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
                MostrarMensaje("Error del Sistema", "Ocurrió un problema: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }
    }
}