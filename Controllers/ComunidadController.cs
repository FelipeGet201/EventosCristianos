using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    public class ComunidadController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Comunidad;

        public ComunidadController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // ==========================================
        // FUNCIÓN HELPER: VALIDAR SI ES DIRECTIVA
        // ==========================================
        private async Task<bool> VerificarSiEsDirectivo(NpgsqlConnection conexion, int idUsuario)
        {
            string sql = @"
                SELECT COUNT(*) 
                FROM ""Sist_Grupos_Miembros"" 
                WHERE ""Id_Usuario"" = @idU 
                AND ""Id_Grupo"" = (
                    SELECT ""Id_Grupo_Directiva"" 
                    FROM ""Sist_Comunidad_Configuracion"" 
                    WHERE ""Id_Config"" = 1 
                    LIMIT 1
                )";

            using (var cmd = new NpgsqlCommand(sql, conexion))
            {
                cmd.Parameters.AddWithValue("@idU", idUsuario);
                var res = await cmd.ExecuteScalarAsync();
                return res != null && res != DBNull.Value && Convert.ToInt64(res) > 0;
            }
        }

        // ==========================================
        // CONFIGURACIÓN DE LA COMUNIDAD (ADMINS)
        // ==========================================
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Configuracion()
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para configurar la comunidad.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var grupos = new List<SelectListItem>();
            string idGrupoActual = "";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var res = await new NpgsqlCommand("SELECT \"Id_Grupo_Directiva\" FROM \"Sist_Comunidad_Configuracion\" WHERE \"Id_Config\" = 1", conexion).ExecuteScalarAsync();
                    if (res != DBNull.Value && res != null) idGrupoActual = res.ToString();

                    string sqlGrupos = "SELECT \"Id_Grupo\", \"Nombre_Grupo\" FROM \"Sist_Grupos_Whatsapp\" ORDER BY \"Nombre_Grupo\"";
                    using (var cmd = new NpgsqlCommand(sqlGrupos, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            grupos.Add(new SelectListItem { Value = r["Id_Grupo"].ToString(), Text = r["Nombre_Grupo"].ToString() });
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.Grupos = grupos;
            ViewBag.IdGrupoActual = idGrupoActual;
            return View();
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarConfiguracion(int? Id_Grupo_Directiva)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar)) return RedirectToAction("Index");

            if (Id_Grupo_Directiva.HasValue && Id_Grupo_Directiva.Value <= 0)
            {
                MostrarMensaje("Datos inválidos", "El ID del grupo seleccionado no es válido.", TipoMensaje.Alerta);
                return RedirectToAction("Configuracion");
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"INSERT INTO ""Sist_Comunidad_Configuracion"" (""Id_Config"", ""Id_Grupo_Directiva"") 
                                   VALUES (1, @idG) ON CONFLICT (""Id_Config"") DO UPDATE SET ""Id_Grupo_Directiva"" = EXCLUDED.""Id_Grupo_Directiva""";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idG", (object)Id_Grupo_Directiva ?? DBNull.Value);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, "Actualizó la configuración de la Directiva", ip);
                }
                MostrarMensaje("Guardado", "La configuración se ha actualizado correctamente.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Index");
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Index(string filtroTipo = "")
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return RedirectToAction("Index", "Home");

            bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin);
            int idUserLogueado = int.Parse(User.FindFirst("IdUsuario").Value);
            var lista = new List<ComunidadIndexViewModel>();
            var tiposExistentes = new List<string>();
            bool esDirectivo = false;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    esDirectivo = await VerificarSiEsDirectivo(conexion, idUserLogueado);

                    string sqlTipos = "SELECT DISTINCT \"Tipo_Mensaje\" FROM \"Sist_Comunidad_Mensajes\" WHERE \"Estado\" != 'CAN' AND \"Fecha_Expiracion\" >= NOW()";
                    using (var cmdT = new NpgsqlCommand(sqlTipos, conexion))
                    using (var rT = await cmdT.ExecuteReaderAsync())
                    {
                        while (await rT.ReadAsync()) tiposExistentes.Add(rT["Tipo_Mensaje"].ToString());
                    }

                    // SE MODIFICA LA LÓGICA DEL WHERE PARA PERMITIR VER LOS MENSAJES ESPECÍFICOS
                    string sql = @"
                        SELECT m.*, u.""NombreCompleto"",
                               (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Leidos"" l WHERE l.""Id_Mensaje"" = m.""Id_Mensaje"" AND l.""Id_Usuario"" = @idUser) as ""Leido"",
                               (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Reacciones"" r WHERE r.""Id_Mensaje"" = m.""Id_Mensaje"") as ""TotalReacciones"",
                               (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Reacciones"" r WHERE r.""Id_Mensaje"" = m.""Id_Mensaje"" AND r.""Id_Usuario"" = @idUser) as ""MiReaccion"",
                               (SELECT COUNT(*) FROM ""Sist_Comunidad_Oraciones_Atendidas"" oa WHERE oa.""Id_Mensaje"" = m.""Id_Mensaje"" AND oa.""Id_Usuario"" = @idUser) as ""YoOro""
                        FROM ""Sist_Comunidad_Mensajes"" m
                        LEFT JOIN ""Sist_Usuarios"" u ON m.""Id_Usuario_Autor"" = u.""Id_Usuario""
                        WHERE m.""Fecha_Expiracion"" >= NOW() 
                          AND m.""Estado"" != 'CAN'
                          AND (
                              @esDir = TRUE 
                              OR @esAdmin = TRUE 
                              OR m.""Id_Usuario_Autor"" = @idUser 
                              OR (m.""Estado"" = 'APR' AND m.""Destinatario"" = 'Todos')
                              OR (m.""Estado"" = 'APR' AND m.""Destinatario"" = 'Especificos' AND EXISTS (
                                  SELECT 1 FROM ""Sist_Comunidad_Mensajes_Destinatarios"" md 
                                  WHERE md.""Id_Mensaje"" = m.""Id_Mensaje"" AND md.""Id_Usuario"" = @idUser
                              ))
                          )";

                    if (!string.IsNullOrEmpty(filtroTipo) && filtroTipo != "Todos") sql += " AND m.\"Tipo_Mensaje\" = @filtro";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@esDir", esDirectivo);
                        cmd.Parameters.AddWithValue("@esAdmin", esAdmin);
                        cmd.Parameters.AddWithValue("@idUser", idUserLogueado);
                        if (!string.IsNullOrEmpty(filtroTipo) && filtroTipo != "Todos") cmd.Parameters.AddWithValue("@filtro", filtroTipo);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                bool anonimo = (bool)r["Es_Anonimo"];
                                bool esInst = (bool)r["Es_Institucional"];
                                string nombreAutor = anonimo ? "Anónimo" : (esInst ? "Directiva AJP" : r["NombreCompleto"]?.ToString() ?? "Desconocido");
                                bool yoOro = Convert.ToInt32(r["YoOro"]) > 0;
                                bool oracionGlobal = (bool)r["Oracion_Atendida"];
                                int autorId = r["Id_Usuario_Autor"] != DBNull.Value ? (int)r["Id_Usuario_Autor"] : 0;
                                bool esMio = autorId == idUserLogueado;

                                lista.Add(new ComunidadIndexViewModel
                                {
                                    Id_Mensaje = (int)r["Id_Mensaje"],
                                    Tipo_Mensaje = r["Tipo_Mensaje"].ToString(),
                                    Destinatario = r["Destinatario"].ToString(),
                                    Estado = r["Estado"].ToString(),
                                    Contenido = r["Contenido"].ToString(),
                                    Fecha_Creacion = (DateTime)r["Fecha_Creacion"],
                                    Fecha_Expiracion = (DateTime)r["Fecha_Expiracion"],
                                    Editado = (bool)r["Editado"],
                                    Oracion_Atendida = esMio ? oracionGlobal : yoOro,
                                    Es_Anonimo = anonimo,
                                    NombreAutor = nombreAutor,
                                    EsMio = esMio,
                                    EsLeido = Convert.ToInt32(r["Leido"]) > 0,
                                    TotalReacciones = Convert.ToInt32(r["TotalReacciones"]),
                                    UsuarioReacciono = Convert.ToInt32(r["MiReaccion"]) > 0
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            var listaOrdenada = lista.OrderBy(x => x.NombreAutor == "Directiva AJP" ? 0 : (x.Tipo_Mensaje == "Oracion" ? 1 : 2)).ThenByDescending(x => x.Fecha_Creacion).ToList();
            ViewBag.PuedeCrear = User.TienePermiso(Modulo, PermisoCrear);
            ViewBag.EsAdmin = esAdmin;
            ViewBag.EsDirectivo = esDirectivo;
            ViewBag.TiposExistentes = tiposExistentes;
            ViewBag.FiltroActual = string.IsNullOrEmpty(filtroTipo) ? "Todos" : filtroTipo;

            return View(listaOrdenada);
        }

        // ==========================================
        // NUEVO MENSAJE
        // ==========================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Nuevo()
        {
            if (!User.TienePermiso(Modulo, PermisoCrear)) return RedirectToAction("Index");

            var modelo = new ComunidadFormViewModel { Fecha_Expiracion = DateTime.Now.AddMonths(2) };
            var tipos = new List<TipoMensajeItem>();
            var usuariosSelect = new List<SelectListItem>();
            bool esDirectivo = false;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    int idUserLogueado = int.Parse(User.FindFirst("IdUsuario").Value);
                    esDirectivo = await VerificarSiEsDirectivo(conexion, idUserLogueado);

                    // Cargar Tipos de Mensaje
                    string sql = @"SELECT ""Clave"", ""Nombre"", ""Icono"", ""Solo_Directiva"" FROM ""Sist_Comunidad_Tipos_Mensaje"" ORDER BY ""Id_Tipo"" ASC";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                            tipos.Add(new TipoMensajeItem { Clave = r["Clave"].ToString(), Nombre = r["Nombre"].ToString(), Icono = r["Icono"].ToString(), Solo_Directiva = (bool)r["Solo_Directiva"] });
                    }

                    // Cargar usuarios para Select2 si es directivo
                    if (esDirectivo)
                    {
                        string sqlU = @"SELECT ""Id_Usuario"", ""NombreCompleto"", ""Nombre_Usuario"" FROM ""Sist_Usuarios"" WHERE ""Activo"" = TRUE ORDER BY ""NombreCompleto""";
                        using (var cmdU = new NpgsqlCommand(sqlU, conexion))
                        using (var rU = await cmdU.ExecuteReaderAsync())
                        {
                            while (await rU.ReadAsync())
                            {
                                usuariosSelect.Add(new SelectListItem
                                {
                                    Value = rU["Id_Usuario"].ToString(),
                                    Text = $"{rU["NombreCompleto"]} ({rU["Nombre_Usuario"]})"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.TiposMensaje = tipos;
            ViewBag.UsuariosSelect = usuariosSelect;
            ViewBag.EsDirectivo = esDirectivo;
            return View(modelo);
        }
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> ObtenerDestinatarios(int idMensaje)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return Json(new { exito = false, mensaje = "Denegado" });

            int idUserLogueado = int.Parse(User.FindFirst("IdUsuario").Value);
            var lista = new List<string>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    bool esDirectivo = await VerificarSiEsDirectivo(conexion, idUserLogueado);
                    bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin);

                    // Validar que solo un directivo o admin pueda curiosear esta lista
                    if (!esDirectivo && !esAdmin) return Json(new { exito = false, mensaje = "Denegado" });

                    string sql = @"SELECT u.""NombreCompleto"" 
                           FROM ""Sist_Comunidad_Mensajes_Destinatarios"" md
                           INNER JOIN ""Sist_Usuarios"" u ON md.""Id_Usuario"" = u.""Id_Usuario""
                           WHERE md.""Id_Mensaje"" = @idM ORDER BY u.""NombreCompleto""";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idM", idMensaje);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync()) lista.Add(r["NombreCompleto"].ToString());
                        }
                    }
                }
                return Json(new { exito = true, data = lista });
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Detalle(int id, int? idHilo = null)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return RedirectToAction("Index");

            var modelo = new ComunidadDetalleViewModel { Id_Mensaje = id };
            bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin);
            int idUserLogueado = int.Parse(User.FindFirst("IdUsuario").Value);
            bool esDirectivo = false;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    esDirectivo = await VerificarSiEsDirectivo(conexion, idUserLogueado);

                    // =====================================
                    // MARCAR COMO LEÍDO (RESTAURADO)
                    // =====================================
                    string sqlLeido = @"INSERT INTO ""Sist_Comunidad_Mensajes_Leidos"" (""Id_Mensaje"", ""Id_Usuario"") VALUES (@id, @idUser) ON CONFLICT DO NOTHING";
                    using (var cmdLeido = new NpgsqlCommand(sqlLeido, conexion))
                    {
                        cmdLeido.Parameters.AddWithValue("@id", id);
                        cmdLeido.Parameters.AddWithValue("@idUser", idUserLogueado);
                        await cmdLeido.ExecuteNonQueryAsync();
                    }

                    // =====================================
                    // 1. OBTENER DETALLE DEL MENSAJE BASE
                    // =====================================
                    string sqlMsg = @"
            SELECT m.*, u.""NombreCompleto"",
                   (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Reacciones"" r WHERE r.""Id_Mensaje"" = m.""Id_Mensaje"") as ""TotalReacciones"",
                   (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Reacciones"" r WHERE r.""Id_Mensaje"" = m.""Id_Mensaje"" AND r.""Id_Usuario"" = @idUser) as ""MiReaccion"",
                   (SELECT COUNT(*) FROM ""Sist_Comunidad_Oraciones_Atendidas"" oa WHERE oa.""Id_Mensaje"" = m.""Id_Mensaje"" AND oa.""Id_Usuario"" = @idUser) as ""YoOro"",
                   (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Destinatarios"" md WHERE md.""Id_Mensaje"" = m.""Id_Mensaje"" AND md.""Id_Usuario"" = @idUser) as ""EsDestinatario""
            FROM ""Sist_Comunidad_Mensajes"" m 
            LEFT JOIN ""Sist_Usuarios"" u ON m.""Id_Usuario_Autor"" = u.""Id_Usuario""
            WHERE m.""Id_Mensaje"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlMsg, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@idUser", idUserLogueado);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                int autorId = r["Id_Usuario_Autor"] != DBNull.Value ? (int)r["Id_Usuario_Autor"] : 0;
                                string estado = r["Estado"].ToString();
                                string destinatario = r["Destinatario"].ToString();
                                bool anonimo = (bool)r["Es_Anonimo"];
                                bool esInstMsg = (bool)r["Es_Institucional"];
                                bool esDestinatario = Convert.ToInt32(r["EsDestinatario"]) > 0;

                                bool accesoPermitido = esAdmin || esDirectivo || (autorId == idUserLogueado);
                                if (!accesoPermitido)
                                {
                                    if (estado != "APR" || (destinatario != "Todos" && !esDestinatario))
                                    {
                                        MostrarMensaje("Privado", "No tienes autorización para ver este mensaje.", TipoMensaje.Alerta);
                                        return RedirectToAction("Index");
                                    }
                                }

                                modelo.Id_Usuario_Autor = autorId;
                                modelo.Tipo_Mensaje = r["Tipo_Mensaje"].ToString();
                                modelo.Destinatario = destinatario;
                                modelo.Contenido = r["Contenido"].ToString();
                                modelo.Estado = estado;
                                modelo.Fecha_Creacion = (DateTime)r["Fecha_Creacion"];
                                modelo.Es_Anonimo = anonimo;
                                modelo.Editado = (bool)r["Editado"];

                                bool yoOro = Convert.ToInt32(r["YoOro"]) > 0;
                                bool oracionGlobal = (bool)r["Oracion_Atendida"];

                                modelo.Oracion_Atendida = (autorId == idUserLogueado) ? oracionGlobal : yoOro;
                                modelo.Motivo_Rechazo = r["Motivo_Rechazo"]?.ToString();
                                modelo.NombreAutor = anonimo ? "Anónimo" : (esInstMsg ? "Directiva AJP" : r["NombreCompleto"].ToString());
                                modelo.TotalReacciones = Convert.ToInt32(r["TotalReacciones"]);
                                modelo.UsuarioReacciono = Convert.ToInt32(r["MiReaccion"]) > 0;
                            }
                            else return RedirectToAction("Index");
                        }
                    }

                    // ==============================================================
                    // LÓGICA DE HILOS (CORRECCIÓN CRÍTICA DE PRIVACIDAD)
                    // ==============================================================

                    bool esAutor = (modelo.Id_Usuario_Autor == idUserLogueado);
                    bool puedeVerTodosLosHilos = esAutor || esDirectivo || esAdmin;

                    if (puedeVerTodosLosHilos)
                    {
                        var hilosActivos = new List<SelectListItem>();
                        string sqlHilos = @"
                            SELECT DISTINCT r.""Id_Usuario_Hilo"", u.""NombreCompleto""
                            FROM ""Sist_Comunidad_Respuestas"" r
                            INNER JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario_Hilo"" = u.""Id_Usuario""
                            WHERE r.""Id_Mensaje"" = @id AND r.""Id_Usuario_Hilo"" IS NOT NULL";

                        using (var cmdH = new NpgsqlCommand(sqlHilos, conexion))
                        {
                            cmdH.Parameters.AddWithValue("@id", id);
                            using (var rH = await cmdH.ExecuteReaderAsync())
                            {
                                while (await rH.ReadAsync())
                                {
                                    hilosActivos.Add(new SelectListItem
                                    {
                                        Value = rH["Id_Usuario_Hilo"].ToString(),
                                        Text = rH["NombreCompleto"].ToString()
                                    });
                                }
                            }
                        }

                        ViewBag.HilosDisponibles = hilosActivos;
                        ViewBag.HiloSeleccionado = idHilo;

                        if (idHilo == null && hilosActivos.Any())
                        {
                            idHilo = int.Parse(hilosActivos.First().Value);
                            ViewBag.HiloSeleccionado = idHilo;
                        }
                    }
                    else
                    {
                        // Si es un usuario normal viendo un mensaje ajeno,
                        // forzamos a que su hilo sea únicamente él mismo.
                        idHilo = idUserLogueado;
                    }

                    // =====================================
                    // OBTENER RESPUESTAS FILTRADAS
                    // =====================================
                    string sqlResp = @"
                        SELECT r.*, u.""NombreCompleto""
                        FROM ""Sist_Comunidad_Respuestas"" r
                        LEFT JOIN ""Sist_Usuarios"" u ON r.""Id_Usuario_Responde"" = u.""Id_Usuario""
                        WHERE r.""Id_Mensaje"" = @id ";

                    if (idHilo.HasValue)
                    {
                        sqlResp += @" AND r.""Id_Usuario_Hilo"" = @idH ";
                    }
                    else if (!puedeVerTodosLosHilos)
                    {
                        // Filtro de seguridad de respaldo
                        sqlResp += @" AND (r.""Id_Usuario_Hilo"" = @idUserLog OR r.""Id_Usuario_Responde"" = @idUserLog)";
                    }

                    sqlResp += @" ORDER BY r.""Fecha_Creacion"" ASC";

                    using (var cmdR = new NpgsqlCommand(sqlResp, conexion))
                    {
                        cmdR.Parameters.AddWithValue("@id", id);
                        if (idHilo.HasValue) cmdR.Parameters.AddWithValue("@idH", idHilo.Value);
                        if (!puedeVerTodosLosHilos) cmdR.Parameters.AddWithValue("@idUserLog", idUserLogueado);

                        using (var r = await cmdR.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idResponde = (int)r["Id_Usuario_Responde"];
                                bool esInstResp = (bool)r["Es_Institucional"];
                                string nombreRespuesta = "";

                                if (idResponde == modelo.Id_Usuario_Autor && modelo.Es_Anonimo)
                                    nombreRespuesta = "Anónimo";
                                else
                                    nombreRespuesta = esInstResp ? "Directiva AJP" : r["NombreCompleto"].ToString();

                                modelo.Respuestas.Add(new ComunidadRespuestaItem
                                {
                                    Id_Respuesta = (int)r["Id_Respuesta"],
                                    Contenido = r["Contenido"].ToString(),
                                    Fecha_Creacion = (DateTime)r["Fecha_Creacion"],
                                    Nombre_Responde = nombreRespuesta,
                                    Id_Usuario_Hilo = r["Id_Usuario_Hilo"] as int?,
                                    EsDeDirectiva = esInstResp
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.EsAdmin = esAdmin;
            ViewBag.EsDirectivo = esDirectivo;
            ViewBag.IdUserLogueado = idUserLogueado;
            ViewBag.PuedeEditar = User.TienePermiso(Modulo, PermisoEditar);
            return View(modelo);
        }

        // ==========================================
        // INTERACCIONES Y ESTADOS
        // ==========================================
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Responder(int idMensaje, string contenido, int? idUsuarioHilo)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear)) return RedirectToAction("Detalle", new { id = idMensaje });

            if (idMensaje <= 0 || string.IsNullOrWhiteSpace(contenido))
            {
                MostrarMensaje("Datos inválidos", "El contenido de la respuesta es obligatorio.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = idMensaje });
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int? idAutorOriginal = null;
                    string estadoMensaje = "";
                    bool esAnonimoOriginal = false;
                    string destinatario = "";
                    bool esDestinatario = false;
                    bool mensajeExiste = false;

                    using (var cmdMsg = new NpgsqlCommand(@"
        SELECT ""Id_Usuario_Autor"", ""Estado"", ""Es_Anonimo"", ""Destinatario"",
               (SELECT COUNT(*) FROM ""Sist_Comunidad_Mensajes_Destinatarios"" WHERE ""Id_Mensaje"" = @idM AND ""Id_Usuario"" = @idU) as ""EsDestinatario"" 
        FROM ""Sist_Comunidad_Mensajes"" WHERE ""Id_Mensaje"" = @idM", conexion))
                    {
                        cmdMsg.Parameters.AddWithValue("@idM", idMensaje);
                        cmdMsg.Parameters.AddWithValue("@idU", idUser);
                        using (var reader = await cmdMsg.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                mensajeExiste = true;
                                // CORRECCIÓN: Lectura segura de DBNull
                                idAutorOriginal = reader["Id_Usuario_Autor"] != DBNull.Value ? (int)reader["Id_Usuario_Autor"] : (int?)null;
                                estadoMensaje = reader["Estado"].ToString();
                                esAnonimoOriginal = reader["Es_Anonimo"] != DBNull.Value && (bool)reader["Es_Anonimo"];
                                destinatario = reader["Destinatario"].ToString();
                                esDestinatario = Convert.ToInt32(reader["EsDestinatario"]) > 0;
                            }
                        }
                    }

                    // Validar que el mensaje exista
                    if (!mensajeExiste || estadoMensaje == "CAN")
                    {
                        MostrarMensaje("Acción Denegada", "El mensaje no existe o ha sido cancelado.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    bool esDirectivo = await VerificarSiEsDirectivo(conexion, idUser);
                    bool esAdmin = User.TienePermiso(Modulo, PermisoAdmin);

                    // CORRECCIÓN: Validamos usando .HasValue para evitar excepciones
                    bool accesoPermitido = esDirectivo || esAdmin || (idAutorOriginal.HasValue && idAutorOriginal.Value == idUser);
                    if (!accesoPermitido && (estadoMensaje != "APR" || (destinatario != "Todos" && !esDestinatario)))
                    {
                        MostrarMensaje("Acceso Denegado", "No tienes permisos para participar en este hilo.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    bool esInstResp = false;

                    if (esDirectivo)
                    {
                        if (idUsuarioHilo == null) idUsuarioHilo = idAutorOriginal;
                        bool esSuMensajeAnonimo = (idAutorOriginal.HasValue && idAutorOriginal.Value == idUser) && esAnonimoOriginal;
                        if (!esSuMensajeAnonimo) esInstResp = true;
                    }
                    else if (idUsuarioHilo == null)
                    {
                        idUsuarioHilo = idUser;
                    }

                    string sql = @"INSERT INTO ""Sist_Comunidad_Respuestas"" (""Id_Mensaje"", ""Id_Usuario_Responde"", ""Id_Usuario_Hilo"", ""Es_Institucional"", ""Contenido"", ""Fecha_Creacion"") VALUES (@idM, @idU, @idH, @inst, @cont, NOW())";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idM", idMensaje);
                        cmd.Parameters.AddWithValue("@idU", idUser);
                        cmd.Parameters.AddWithValue("@idH", (object)idUsuarioHilo ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@inst", esInstResp);
                        cmd.Parameters.AddWithValue("@cont", contenido.Trim());
                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var cmdDel = new NpgsqlCommand("DELETE FROM \"Sist_Comunidad_Mensajes_Leidos\" WHERE \"Id_Mensaje\" = @idM AND \"Id_Usuario\" != @idU", conexion))
                    {
                        cmdDel.Parameters.AddWithValue("@idM", idMensaje);
                        cmdDel.Parameters.AddWithValue("@idU", idUser);
                        await cmdDel.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Detalle", new { id = idMensaje, idHilo = idUsuarioHilo });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AtenderOracion(int idMensaje)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return RedirectToAction("Index");
            if (idMensaje <= 0) return RedirectToAction("Index");

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var cmdInsert = new NpgsqlCommand("INSERT INTO \"Sist_Comunidad_Oraciones_Atendidas\" (\"Id_Mensaje\", \"Id_Usuario\") VALUES (@idM, @idU) ON CONFLICT DO NOTHING", conexion))
                    {
                        cmdInsert.Parameters.AddWithValue("@idM", idMensaje);
                        cmdInsert.Parameters.AddWithValue("@idU", idUser);
                        await cmdInsert.ExecuteNonQueryAsync();
                    }

                    using (var cmdUpdate = new NpgsqlCommand("UPDATE \"Sist_Comunidad_Mensajes\" SET \"Oracion_Atendida\" = TRUE WHERE \"Id_Mensaje\" = @idM", conexion))
                    {
                        cmdUpdate.Parameters.AddWithValue("@idM", idMensaje);
                        await cmdUpdate.ExecuteNonQueryAsync();
                    }
                }
                MostrarMensaje("Oración Tomada", "Gracias, te has sumado a orar por esta petición.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = idMensaje });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AlternarReaccion(int idMensaje, string returnUrl)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return RedirectToAction("Index");
            if (idMensaje <= 0) return RedirectToAction("Index");

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    bool yaReacciono = false;

                    using (var cmdCheck = new NpgsqlCommand("SELECT COUNT(*) FROM \"Sist_Comunidad_Mensajes_Reacciones\" WHERE \"Id_Mensaje\" = @idM AND \"Id_Usuario\" = @idU", conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idM", idMensaje);
                        cmdCheck.Parameters.AddWithValue("@idU", idUser);
                        yaReacciono = (long)await cmdCheck.ExecuteScalarAsync() > 0;
                    }

                    if (yaReacciono)
                    {
                        using (var cmdDel = new NpgsqlCommand("DELETE FROM \"Sist_Comunidad_Mensajes_Reacciones\" WHERE \"Id_Mensaje\" = @idM AND \"Id_Usuario\" = @idU", conexion))
                        {
                            cmdDel.Parameters.AddWithValue("@idM", idMensaje);
                            cmdDel.Parameters.AddWithValue("@idU", idUser);
                            await cmdDel.ExecuteNonQueryAsync();
                        }
                    }
                    else
                    {
                        using (var cmdIns = new NpgsqlCommand("INSERT INTO \"Sist_Comunidad_Mensajes_Reacciones\" (\"Id_Mensaje\", \"Id_Usuario\") VALUES (@idM, @idU)", conexion))
                        {
                            cmdIns.Parameters.AddWithValue("@idM", idMensaje);
                            cmdIns.Parameters.AddWithValue("@idU", idUser);
                            await cmdIns.ExecuteNonQueryAsync();
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            if (!string.IsNullOrEmpty(returnUrl) && returnUrl.Contains("Detalle"))
                return RedirectToAction("Detalle", new { id = idMensaje });

            return RedirectToAction("Index");
        }

        [HttpGet]
        [Authorize]
        public IActionResult DescargarPlantillaDestinatarios()
        {
            using (var libro = new ClosedXML.Excel.XLWorkbook())
            {
                var hoja = libro.Worksheets.Add("Destinatarios AJP");

                // 1. Dar formato al encabezado principal
                var rangoEncabezado = hoja.Range("A1:B1");
                rangoEncabezado.Merge();
                rangoEncabezado.Value = "Plantilla de Carga Masiva - Destinatarios";
                rangoEncabezado.Style.Font.Bold = true;
                rangoEncabezado.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
                rangoEncabezado.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.Teal;
                rangoEncabezado.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                // 2. Encabezados de las columnas (Fila 2)
                hoja.Cell(2, 1).Value = "Usuario Clave";
                hoja.Cell(2, 1).Style.Font.Bold = true;
                hoja.Cell(2, 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;

                hoja.Cell(2, 2).Value = "Instrucciones: Coloca una Clave de Usuario válida en la columna A.";
                hoja.Cell(2, 2).Style.Font.Italic = true;
                hoja.Cell(2, 2).Style.Font.FontColor = ClosedXML.Excel.XLColor.DimGray;

                // 3. Ajustar el ancho visual de las columnas
                hoja.Column(1).Width = 35;
                hoja.Column(2).Width = 100;

                // 4. Dato de Ejemplo de Referencia (Fila 3)
                hoja.Cell(3, 1).Value = "ejemplo_nick01";
                hoja.Cell(3, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.Gray;

                using (var ms = new System.IO.MemoryStream())
                {
                    libro.SaveAs(ms);
                    return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Plantilla_Usuarios_AJP.xlsx");
                }
            }
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarMensaje(ComunidadFormViewModel modelo, string tipoExpiracion, bool EsDirectivo, List<int> idsDestinatarios)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Permisos Insuficientes", "Lo sentimos, no tienes permiso para realizar esta accion.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (string.IsNullOrWhiteSpace(modelo.Contenido) || string.IsNullOrWhiteSpace(modelo.Destinatario) || string.IsNullOrWhiteSpace(modelo.Tipo_Mensaje))
            {
                MostrarMensaje("Datos incompletos", "Por favor completa todos los pasos del mensaje.", TipoMensaje.Alerta);
                return RedirectToAction("Nuevo");
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Verificamos el rol real
                    bool esDirectivoReal = await VerificarSiEsDirectivo(conexion, idUser);
                    bool publicarComoDirectiva = esDirectivoReal && EsDirectivo;

                    // 2. MATRIZ LÓGICA: Validación "Especificos" (CONSOLIDADA)
                    var idsValidos = idsDestinatarios?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();

                    if (modelo.Destinatario == "Especificos")
                    {
                        if (!publicarComoDirectiva)
                        {
                            MostrarMensaje("Acceso Denegado", "Solo la Directiva puede enviar mensajes a usuarios específicos.", TipoMensaje.Error);
                            return RedirectToAction("Index");
                        }
                        if (modelo.Tipo_Mensaje != "General")
                        {
                            MostrarMensaje("Datos Inválidos", "Los mensajes a usuarios específicos solo pueden ser de tipo 'General'.", TipoMensaje.Alerta);
                            return RedirectToAction("Nuevo");
                        }
                        if (!idsValidos.Any())
                        {
                            MostrarMensaje("Faltan Destinatarios", "Debes seleccionar al menos un usuario destinatario válido.", TipoMensaje.Alerta);
                            return RedirectToAction("Nuevo");
                        }
                    }

                    // 3. MATRIZ LÓGICA: Validación de Tipos "Solo Directiva"
                    bool soloDirectiva = false;
                    using (var cmdVal = new NpgsqlCommand("SELECT \"Solo_Directiva\" FROM \"Sist_Comunidad_Tipos_Mensaje\" WHERE \"Clave\" = @clave", conexion))
                    {
                        cmdVal.Parameters.AddWithValue("@clave", modelo.Tipo_Mensaje);
                        var res = await cmdVal.ExecuteScalarAsync();
                        if (res != null) soloDirectiva = (bool)res;
                    }

                    if (soloDirectiva && modelo.Destinatario != "Directiva")
                    {
                        MostrarMensaje("Datos Inválidos", "Este tipo de mensaje es privado y solo puede enviarse a la Directiva.", TipoMensaje.Error);
                        return RedirectToAction("Nuevo");
                    }

                    // 4. Configurar Estado Inicial, Anonimato y Rol Institucional
                    string estadoInicial = "PEN";
                    bool esInstitucional = publicarComoDirectiva;

                    if (modelo.Destinatario == "Todos")
                    {
                        estadoInicial = "PEN";
                        modelo.Es_Anonimo = !publicarComoDirectiva;
                    }
                    else if (modelo.Destinatario == "Especificos")
                    {
                        estadoInicial = "PEN"; // Exige doble validación por parte de otro directivo
                        modelo.Es_Anonimo = false;
                    }
                    else if (modelo.Destinatario == "Directiva")
                    {
                        estadoInicial = publicarComoDirectiva ? "PEN" : "APR";
                        modelo.Es_Anonimo = !publicarComoDirectiva;
                    }

                    // 5. Calcular Expiración
                    if (tipoExpiracion == "1Dia") modelo.Fecha_Expiracion = DateTime.Now.AddDays(1);
                    else if (tipoExpiracion == "1Semana") modelo.Fecha_Expiracion = DateTime.Now.AddDays(7);
                    else if (tipoExpiracion == "1Mes") modelo.Fecha_Expiracion = DateTime.Now.AddMonths(1);
                    else if (tipoExpiracion == "Fija" && modelo.Fecha_Expiracion <= DateTime.Now)
                    {
                        MostrarMensaje("Fecha inválida", "La fecha de expiración seleccionada debe ser a futuro.", TipoMensaje.Alerta);
                        return RedirectToAction("Nuevo");
                    }

                    // 6. Transacción
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlMsg = @"INSERT INTO ""Sist_Comunidad_Mensajes"" 
                             (""Id_Usuario_Autor"", ""Tipo_Mensaje"", ""Destinatario"", ""Contenido"", ""Fecha_Creacion"", ""Fecha_Expiracion"", ""Estado"", ""Es_Anonimo"", ""Es_Institucional"") 
                             VALUES (@idU, @tipo, @dest, @cont, NOW(), @fe, @est, @anon, @inst) RETURNING ""Id_Mensaje""";

                            int idMensajeNuevo;
                            using (var cmdMsg = new NpgsqlCommand(sqlMsg, conexion, trans))
                            {
                                cmdMsg.Parameters.AddWithValue("@idU", idUser);
                                cmdMsg.Parameters.AddWithValue("@tipo", modelo.Tipo_Mensaje);
                                cmdMsg.Parameters.AddWithValue("@dest", modelo.Destinatario);
                                cmdMsg.Parameters.AddWithValue("@cont", modelo.Contenido.Trim());
                                cmdMsg.Parameters.AddWithValue("@fe", modelo.Fecha_Expiracion);
                                cmdMsg.Parameters.AddWithValue("@est", estadoInicial);
                                cmdMsg.Parameters.AddWithValue("@anon", modelo.Es_Anonimo);
                                cmdMsg.Parameters.AddWithValue("@inst", esInstitucional);
                                idMensajeNuevo = (int)await cmdMsg.ExecuteScalarAsync();
                            }

                            if (modelo.Destinatario == "Especificos")
                            {
                                string sqlDest = @"INSERT INTO ""Sist_Comunidad_Mensajes_Destinatarios"" (""Id_Mensaje"", ""Id_Usuario"") VALUES (@idM, @idUD)";
                                foreach (var idDest in idsValidos)
                                {
                                    using (var cmdDest = new NpgsqlCommand(sqlDest, conexion, trans))
                                    {
                                        cmdDest.Parameters.AddWithValue("@idM", idMensajeNuevo);
                                        cmdDest.Parameters.AddWithValue("@idUD", idDest);
                                        await cmdDest.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Crear, $"Creó un nuevo Mensaje en la Comunidad #{idMensajeNuevo}", ip, trans);
                            await trans.CommitAsync();

                            MostrarMensaje("Mensaje Creado", estadoInicial == "APR" ? "Tu mensaje ha sido publicado con éxito en el muro privado." : "Tu mensaje ha sido enviado y está pendiente de aprobación.", TipoMensaje.Exito);
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
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Nuevo");
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevisarMensaje(int idMensaje, string accion, string motivoRechazo)
        {
            if (idMensaje <= 0 || (accion != "APR" && accion != "REC"))
            {
                MostrarMensaje("Error", "Acción u origen de datos no válido.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            if (accion == "REC" && string.IsNullOrWhiteSpace(motivoRechazo))
            {
                MostrarMensaje("Validación", "Debes especificar un motivo para rechazar este mensaje.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = idMensaje });
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    bool esDirectivo = await VerificarSiEsDirectivo(conexion, idUser);

                    if (!esDirectivo)
                    {
                        MostrarMensaje("Acceso Denegado", "Solo la Directiva puede moderar.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    int? autorId = null;
                    using (var cmdCheck = new NpgsqlCommand("SELECT \"Id_Usuario_Autor\" FROM \"Sist_Comunidad_Mensajes\" WHERE \"Id_Mensaje\" = @idM", conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idM", idMensaje);
                        var res = await cmdCheck.ExecuteScalarAsync();
                        if (res != null && res != DBNull.Value) autorId = (int)res;
                    }

                    if (autorId.HasValue && autorId.Value == idUser)
                    {
                        MostrarMensaje("Acción no permitida", "No puedes aprobar o rechazar tu propio mensaje. Debe hacerlo otro integrante de la directiva.", TipoMensaje.Alerta);
                        return RedirectToAction("Detalle", new { id = idMensaje });
                    }

                    string sql = "UPDATE \"Sist_Comunidad_Mensajes\" SET \"Estado\" = @est, \"Motivo_Rechazo\" = @motivo WHERE \"Id_Mensaje\" = @id";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idMensaje);
                        cmd.Parameters.AddWithValue("@est", accion);
                        cmd.Parameters.AddWithValue("@motivo", accion == "REC" ? motivoRechazo.Trim() : (object)DBNull.Value);
                        await cmd.ExecuteNonQueryAsync();
                    }
                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Moderó Mensaje #{idMensaje} como {accion}", ip);
                }
                MostrarMensaje("Moderación", "El estado del mensaje ha sido actualizado.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditarMensaje(int idMensaje, string nuevoContenido)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para editar.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = idMensaje });
            }

            if (idMensaje <= 0 || string.IsNullOrWhiteSpace(nuevoContenido))
            {
                MostrarMensaje("Datos inválidos", "El contenido editado no puede estar vacío.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = idMensaje });
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Se agregó la columna "Destinatario" a la consulta para evaluar la regla de negocio
                    string sqlCheck = @"SELECT ""Contenido"", ""Estado"", ""Fecha_Expiracion"", ""Id_Usuario_Autor"", ""Destinatario"" 
                                FROM ""Sist_Comunidad_Mensajes"" WHERE ""Id_Mensaje"" = @idM";
                    string contenidoAntiguo = "";
                    string destinatario = "";

                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idM", idMensaje);
                        using (var r = await cmdCheck.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                if ((int)r["Id_Usuario_Autor"] != idUser)
                                {
                                    MostrarMensaje("Denegado", "Solo el autor puede editar este mensaje.", TipoMensaje.Alerta);
                                    return RedirectToAction("Detalle", new { id = idMensaje });
                                }
                                if ((DateTime)r["Fecha_Expiracion"] < DateTime.Now)
                                {
                                    MostrarMensaje("Denegado", "El mensaje ya expiró.", TipoMensaje.Alerta);
                                    return RedirectToAction("Detalle", new { id = idMensaje });
                                }
                                contenidoAntiguo = r["Contenido"].ToString();
                                destinatario = r["Destinatario"].ToString();
                            }
                            else return RedirectToAction("Index");
                        }
                    }

                    // LÓGICA CORREGIDA: Si es para la Directiva se aprueba directo, si es para Todos vuelve a Pendiente
                    string nuevoEstado = (destinatario == "Directiva") ? "APR" : "PEN";

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlAudit = @"INSERT INTO ""Sist_Comunidad_Auditoria_Ediciones"" 
                                        (""Id_Mensaje"", ""Id_Usuario_Editor"", ""Contenido_Anterior"", ""Contenido_Nuevo"", ""Fecha_Edicion"") 
                                        VALUES (@idM, @idU, @cAnt, @cNue, NOW())";
                            using (var cmdAudit = new NpgsqlCommand(sqlAudit, conexion, trans))
                            {
                                cmdAudit.Parameters.AddWithValue("@idM", idMensaje);
                                cmdAudit.Parameters.AddWithValue("@idU", idUser);
                                cmdAudit.Parameters.AddWithValue("@cAnt", contenidoAntiguo);
                                cmdAudit.Parameters.AddWithValue("@cNue", nuevoContenido.Trim());
                                await cmdAudit.ExecuteNonQueryAsync();
                            }

                            // Se inyecta el estado dinámico calculado
                            string sqlUpdate = @"UPDATE ""Sist_Comunidad_Mensajes"" 
                                         SET ""Contenido"" = @cNue, ""Editado"" = TRUE, ""Estado"" = @est, ""Motivo_Rechazo"" = NULL 
                                         WHERE ""Id_Mensaje"" = @idM";
                            using (var cmdUpd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                            {
                                cmdUpd.Parameters.AddWithValue("@idM", idMensaje);
                                cmdUpd.Parameters.AddWithValue("@cNue", nuevoContenido.Trim());
                                cmdUpd.Parameters.AddWithValue("@est", nuevoEstado);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Editó su Mensaje #{idMensaje}", ip, trans);
                            await trans.CommitAsync();

                            if (nuevoEstado == "PEN")
                                MostrarMensaje("En Revisión", "Tu mensaje ha sido editado y pasará por autorización de la directiva nuevamente.", TipoMensaje.Exito);
                            else
                                MostrarMensaje("Editado", "Tu mensaje privado a la directiva ha sido actualizado exitosamente.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = idMensaje });
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarMensaje(int idMensaje)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para cancelar mensajes.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (idMensaje <= 0) return RedirectToAction("Index");

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int? autor = null;
                    using (var cmdCheck = new NpgsqlCommand("SELECT \"Id_Usuario_Autor\" FROM \"Sist_Comunidad_Mensajes\" WHERE \"Id_Mensaje\" = @idM", conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idM", idMensaje);
                        var res = await cmdCheck.ExecuteScalarAsync();
                        if (res != null && res != DBNull.Value) autor = (int)res;
                    }

                    if (!autor.HasValue || autor.Value != idUser)
                    {
                        MostrarMensaje("Denegado", "No puedes cancelar un mensaje que no es tuyo o no existe.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    using (var cmdUpd = new NpgsqlCommand("UPDATE \"Sist_Comunidad_Mensajes\" SET \"Estado\" = 'CAN' WHERE \"Id_Mensaje\" = @idM", conexion))
                    {
                        cmdUpd.Parameters.AddWithValue("@idM", idMensaje);
                        await cmdUpd.ExecuteNonQueryAsync();
                    }

                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Canceló su Mensaje #{idMensaje}", ip);
                }
                MostrarMensaje("Cancelado", "El mensaje ha sido cancelado.", TipoMensaje.Info);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReenviarMensaje(int idMensaje)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para reenviar mensajes.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = idMensaje });
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlCheck = @"SELECT ""Id_Usuario_Autor"", ""Estado"" FROM ""Sist_Comunidad_Mensajes"" WHERE ""Id_Mensaje"" = @idM";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@idM", idMensaje);
                        using (var r = await cmdCheck.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                // CORRECCIÓN: Lectura segura, convirtiendo NULL en 0
                                int autorId = r["Id_Usuario_Autor"] != DBNull.Value ? (int)r["Id_Usuario_Autor"] : 0;
                                if (autorId != idUser)
                                {
                                    MostrarMensaje("Denegado", "No puedes reenviar un mensaje que no es tuyo.", TipoMensaje.Alerta);
                                    return RedirectToAction("Index");
                                }
                                if (r["Estado"].ToString() != "REC")
                                {
                                    MostrarMensaje("Atención", "Solo puedes reenviar mensajes que han sido rechazados.", TipoMensaje.Alerta);
                                    return RedirectToAction("Detalle", new { id = idMensaje });
                                }
                            }
                        }
                    }

                    string sqlUpdate = @"UPDATE ""Sist_Comunidad_Mensajes"" SET ""Estado"" = 'PEN', ""Motivo_Rechazo"" = NULL WHERE ""Id_Mensaje"" = @idM";
                    using (var cmdUpd = new NpgsqlCommand(sqlUpdate, conexion))
                    {
                        cmdUpd.Parameters.AddWithValue("@idM", idMensaje);
                        await cmdUpd.ExecuteNonQueryAsync();
                    }

                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Reenvió a revisión su Mensaje #{idMensaje}", ip);
                }
                MostrarMensaje("Reenviado", "Tu mensaje ha sido enviado nuevamente para su revisión.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = idMensaje });
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> HistorialEdiciones(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Acceso Denegado", "Solo administradores pueden ver el historial.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = id });
            }

            var lista = new List<ComunidadAuditoriaViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT a.*, u.""NombreCompleto"" 
                                   FROM ""Sist_Comunidad_Auditoria_Ediciones"" a
                                   LEFT JOIN ""Sist_Usuarios"" u ON a.""Id_Usuario_Editor"" = u.""Id_Usuario""
                                   WHERE a.""Id_Mensaje"" = @idM ORDER BY a.""Fecha_Edicion"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idM", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                lista.Add(new ComunidadAuditoriaViewModel
                                {
                                    Id_Auditoria = (int)r["Id_Auditoria"],
                                    Contenido_Anterior = r["Contenido_Anterior"].ToString(),
                                    Contenido_Nuevo = r["Contenido_Nuevo"].ToString(),
                                    Fecha_Edicion = (DateTime)r["Fecha_Edicion"],
                                    NombreEditor = r["NombreCompleto"].ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.IdMensaje = id;
            return View(lista);
        }
    }
}