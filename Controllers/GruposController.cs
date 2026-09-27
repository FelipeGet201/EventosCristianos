using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClosedXML.Excel;
using System.IO;
using Microsoft.AspNetCore.Http;

namespace RedAJP.Controllers
{
    public class GruposController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Grupos;

        public GruposController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        /// <summary>
        /// Lista de grupos de WhatsApp
        /// </summary>
        /// <returns>Retorna la vista con la lista de grupos.</returns>
        [Authorize]
        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<GrupoIndexViewModel>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"
                        SELECT g.""Id_Grupo"", g.""Nombre_Grupo"", g.""Descripcion"", g.""Fecha_Creacion"",
                               (SELECT COUNT(*) FROM ""Sist_Grupos_Miembros"" m WHERE m.""Id_Grupo"" = g.""Id_Grupo"") as ""Total"",
                               (SELECT COUNT(*) FROM ""Sist_Grupos_Miembros"" m WHERE m.""Id_Grupo"" = g.""Id_Grupo"" AND m.""Id_Usuario"" IS NOT NULL) as ""Vinculados""
                        FROM ""Sist_Grupos_Whatsapp"" g
                        ORDER BY g.""Fecha_Creacion"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            int total = Convert.ToInt32(r["Total"]);
                            int vinculados = Convert.ToInt32(r["Vinculados"]);
                            lista.Add(new GrupoIndexViewModel
                            {
                                Id_Grupo = (int)r["Id_Grupo"],
                                Nombre = r["Nombre_Grupo"].ToString(),
                                Descripcion = r["Descripcion"]?.ToString() ?? "",
                                FechaCreacion = (DateTime)r["Fecha_Creacion"],
                                TotalMiembros = total,
                                Vinculados = vinculados,
                                Pendientes = total - vinculados
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.PuedeCrear = User.TienePermiso(Modulo, PermisoCrear);
            return View(lista);
        }

        /// <summary>
        /// Crear nuevo grupo de WhatsApp
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public IActionResult Nuevo()
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Error", "No tienes permisos de creación en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }
            return View(new GrupoFormViewModel());
        }

        /// <summary>
        /// Guardar nuevo grupo de WhatsApp
        /// </summary>
        /// <param name="modelo">Es el modelo con los datos del grupo</param>
        /// <returns>Retorna a la vista de lista de grupos.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarGrupo(GrupoFormViewModel modelo)
        {
            if (!User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Error", "No tienes permisos de creación en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"INSERT INTO ""Sist_Grupos_Whatsapp"" (""Nombre_Grupo"", ""Descripcion"", ""Activo"", ""Fecha_Creacion"") 
                                   VALUES (@nom, @desc, TRUE, NOW()) RETURNING ""Id_Grupo""";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@nom", modelo.Nombre);
                        cmd.Parameters.AddWithValue("@desc", (object)modelo.Descripcion ?? DBNull.Value);
                        int id = (int)await cmd.ExecuteScalarAsync();

                        await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Crear, $"Creó Grupo #{id}", ip);
                    }
                }
                MostrarMensaje("Creado", "Grupo creado exitosamente.", TipoMensaje.Exito);
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return View("Nuevo", modelo);
            }
        }

        /// <summary>
        /// Actualizar grupo de usuarios
        /// </summary>
        /// <param name="modelo">Es el modelo con los datos del grupo</param>
        /// <returns>Retorna a la vista de detalle del grupo.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarGrupo(GrupoFormViewModel modelo)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"UPDATE ""Sist_Grupos_Whatsapp"" 
                           SET ""Nombre_Grupo"" = @nom, 
                               ""Descripcion"" = @desc 
                           WHERE ""Id_Grupo"" = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@nom", modelo.Nombre);
                        cmd.Parameters.AddWithValue("@desc", (object)modelo.Descripcion ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@id", modelo.Id_Grupo);

                        await cmd.ExecuteNonQueryAsync();

                        await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar,
                            $"Actualizó datos del Grupo #{modelo.Id_Grupo}", ip);
                    }
                }
                MostrarMensaje("Actualizado", "La información del grupo ha sido modificada.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { id = modelo.Id_Grupo });
        }

        /// <summary>
        /// Detalle del grupo de WhatsApp
        /// </summary>
        /// <param name="id">Es el Id del grupo</param>
        /// <returns>Retorna la vista con el detalle del grupo.</returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Detalle(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            var modelo = new GrupoDetalleViewModel { Id_Grupo = id };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Cabecera Y CONTEO DE USO EN EVENTOS
                    string sqlCab = @"
                SELECT g.*, 
                       (SELECT COUNT(*) FROM ""Eventos_Catalogo"" WHERE ""Id_Grupo_Usuarios"" = g.""Id_Grupo"") as ""UsoEventos""
                FROM ""Sist_Grupos_Whatsapp"" g 
                WHERE g.""Id_Grupo""=@id";

                    using (var cmd = new NpgsqlCommand(sqlCab, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.Nombre = r["Nombre_Grupo"].ToString();
                                modelo.Descripcion = r["Descripcion"]?.ToString();
                                modelo.EventosAsignados = Convert.ToInt32(r["UsoEventos"]);
                            }
                            else return RedirectToAction("Index");
                        }
                    }

                    // 2. Miembros
                    string sqlM = @"
                        SELECT m.*, u.""NombreCompleto"", u.""Email""
                        FROM ""Sist_Grupos_Miembros"" m
                        LEFT JOIN ""Sist_Usuarios"" u ON m.""Id_Usuario"" = u.""Id_Usuario""
                        WHERE m.""Id_Grupo"" = @id
                        ORDER BY m.""Id_Miembro"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlM, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.Miembros.Add(new MiembroDetalleItem
                                {
                                    Id_Miembro = (int)r["Id_Miembro"],
                                    TelefonoOrigen = r["Telefono_Origen"].ToString(),
                                    NombreImportado = r["Nombre_Importado"]?.ToString() ?? "Manual",
                                    Id_Usuario = r["Id_Usuario"] as int?,
                                    NombreSistema = r["NombreCompleto"]?.ToString() ?? "Externo"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.PuedeEditar = User.TienePermiso(Modulo, PermisoEditar);
            ViewBag.PuedeBorrar = User.TienePermiso(Modulo, PermisoBorrar);
            return View(modelo);
        }

        /// <summary>
        /// Importar miembros al grupo de WhatsApp
        /// </summary>
        /// <param name="id">Es el Id del grupo</param>
        /// <returns>Retorna la vista para importar miembros.</returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Importar(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            var modelo = new ImportacionMiembrosViewModel { Id_Grupo = id };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var res = await new NpgsqlCommand($"SELECT \"Nombre_Grupo\" FROM \"Sist_Grupos_Whatsapp\" WHERE \"Id_Grupo\"={id}", conexion).ExecuteScalarAsync();
                    if (res != null) modelo.NombreGrupo = res.ToString();

                    // Cargar Usuarios Sistema
                    string sqlUsers = @"SELECT ""Id_Usuario"", ""NombreCompleto"", ""Telefono"", ""Email"", ""Email_Verificado"" 
                                      FROM ""Sist_Usuarios"" 
                                      WHERE ""Activo"" = TRUE AND ""Telefono"" IS NOT NULL AND LENGTH(""Telefono"") >= 10
                                      ORDER BY ""NombreCompleto"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlUsers, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            modelo.ListaUsuariosSistema.Add(new UsuarioComboItem
                            {
                                Id = (int)r["Id_Usuario"],
                                Nombre = r["NombreCompleto"].ToString(),
                                Telefono = r["Telefono"].ToString(),
                                Email = r["Email"]?.ToString(),
                                Verificado = r["Email_Verificado"] != DBNull.Value && (bool)r["Email_Verificado"]
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(modelo);
        }

        /// <summary>
        /// Procesar la importación de miembros al grupo de WhatsApp
        /// </summary>
        /// <param name="modelo">Es el modelo con los miembros a importar</param>
        /// <returns>Retorna a la vista de detalle del grupo.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarImportacion(ImportacionMiembrosViewModel modelo)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            if (modelo.MiembrosNuevos == null || !modelo.MiembrosNuevos.Any())
            {
                MostrarMensaje("Atención", "No has agregado ningún miembro a la lista.", TipoMensaje.Alerta);
                return RedirectToAction("Importar", new { id = modelo.Id_Grupo });
            }

            int agregados = 0;
            int idUserLogueado = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            // NUEVO: Hash para evitar duplicados en la lista enviada
            var telefonosProcesadosLote = new HashSet<string>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlCheckDup = @"SELECT COUNT(*) FROM ""Sist_Grupos_Miembros"" WHERE ""Id_Grupo""=@idG AND ""Telefono_Origen"" LIKE '%' || @tel";
                            string sqlFindUser = @"SELECT ""Id_Usuario"" FROM ""Sist_Usuarios"" WHERE ""Telefono"" LIKE '%' || @tel LIMIT 1";
                            string sqlIns = @"INSERT INTO ""Sist_Grupos_Miembros"" (""Id_Grupo"", ""Telefono_Origen"", ""Id_Usuario"", ""Nombre_Importado"") 
                                      VALUES (@idG, @tel, @uid, @nom)";

                            foreach (var miembro in modelo.MiembrosNuevos)
                            {
                                string telLimpio = Funciones.LimpiarTelefono(miembro.Telefono);
                                if (string.IsNullOrEmpty(telLimpio) || telLimpio.Length < 10) continue;

                                // NUEVO: Validar si el teléfono ya viene duplicado en esta misma lista manual
                                if (!telefonosProcesadosLote.Add(telLimpio)) continue;

                                // Validar si el teléfono ya existe en la Base de Datos para este grupo
                                using (var cmdCheck = new NpgsqlCommand(sqlCheckDup, conexion, trans))
                                {
                                    cmdCheck.Parameters.AddWithValue("@idG", modelo.Id_Grupo);
                                    cmdCheck.Parameters.AddWithValue("@tel", telLimpio);
                                    if ((long)await cmdCheck.ExecuteScalarAsync() > 0) continue;
                                }

                                int? idUsuarioFinal = miembro.IdUsuario;
                                string nombreFinal = miembro.Nombre;

                                if (idUsuarioFinal == null || idUsuarioFinal == 0)
                                {
                                    using (var cmdFind = new NpgsqlCommand(sqlFindUser, conexion, trans))
                                    {
                                        cmdFind.Parameters.AddWithValue("@tel", telLimpio);
                                        var res = await cmdFind.ExecuteScalarAsync();
                                        if (res != null) idUsuarioFinal = (int)res;
                                    }
                                }

                                using (var cmdIns = new NpgsqlCommand(sqlIns, conexion, trans))
                                {
                                    cmdIns.Parameters.AddWithValue("@idG", modelo.Id_Grupo);
                                    cmdIns.Parameters.AddWithValue("@tel", telLimpio);
                                    cmdIns.Parameters.AddWithValue("@uid", (object)idUsuarioFinal ?? DBNull.Value);
                                    cmdIns.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(nombreFinal) ? "Invitado" : nombreFinal);
                                    await cmdIns.ExecuteNonQueryAsync();
                                    agregados++;
                                }
                            }

                            if (agregados > 0)
                            {
                                await Funciones.RegistrarBitacora(conexion, idUserLogueado, Modulo, Parametros.AccionesBitacora.Editar,
                                    $"Agregó {agregados} miembros al Grupo #{modelo.Id_Grupo}", ip, trans);
                            }

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }

                if (agregados > 0) MostrarMensaje("Éxito", $"Se agregaron {agregados} miembros correctamente.", TipoMensaje.Exito);
                else MostrarMensaje("Aviso", "No se agregaron miembros nuevos (posiblemente teléfonos inválidos o duplicados).", TipoMensaje.Info);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = modelo.Id_Grupo });
        }


        /// <summary>
        /// Procesar la importación masiva desde un archivo Excel
        /// </summary>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarImportacionExcel(int Id_Grupo, IFormFile ArchivoExcel, string ColumnaTelefono, string ColumnaNombre, int FilaInicio, bool ReemplazarDatos, bool SoloExistentes)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permisos de edición en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            if (ArchivoExcel == null || ArchivoExcel.Length == 0)
            {
                MostrarMensaje("Atención", "No se adjuntó ningún archivo válido.", TipoMensaje.Alerta);
                return RedirectToAction("Importar", new { id = Id_Grupo });
            }

            if (string.IsNullOrEmpty(ColumnaTelefono) || FilaInicio < 1)
            {
                MostrarMensaje("Atención", "Debe especificar la columna de teléfono y la fila de inicio.", TipoMensaje.Alerta);
                return RedirectToAction("Importar", new { id = Id_Grupo });
            }

            int agregados = 0;
            int idUserLogueado = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            var telefonosProcesadosExcel = new HashSet<string>();

            try
            {
                using var stream = new MemoryStream();
                await ArchivoExcel.CopyToAsync(stream);
                using var workbook = new XLWorkbook(stream);
                var ws = workbook.Worksheet(1);
                var rowCount = ws.LastRowUsed()?.RowNumber() ?? 0;

                if (rowCount < FilaInicio)
                {
                    MostrarMensaje("Aviso", "El archivo no contiene datos a partir de la fila de inicio.", TipoMensaje.Info);
                    return RedirectToAction("Importar", new { id = Id_Grupo });
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            if (ReemplazarDatos)
                            {
                                await new NpgsqlCommand($"DELETE FROM \"Sist_Grupos_Miembros\" WHERE \"Id_Grupo\"={Id_Grupo}", conexion, trans).ExecuteNonQueryAsync();
                            }

                            // Extraemos TODOS los teléfonos actuales de la base de datos
                            var telefonosEnBd = new HashSet<string>();

                            if (!ReemplazarDatos)
                            {
                                string sqlExistentes = @"SELECT ""Telefono_Origen"" FROM ""Sist_Grupos_Miembros"" WHERE ""Id_Grupo""=@idG";
                                using (var cmdEx = new NpgsqlCommand(sqlExistentes, conexion, trans))
                                {
                                    cmdEx.Parameters.AddWithValue("@idG", Id_Grupo);
                                    using (var r = await cmdEx.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            string telBd = r["Telefono_Origen"].ToString();
                                            string telBdLimpio = Funciones.LimpiarTelefono(telBd);

                                            if (!string.IsNullOrEmpty(telBdLimpio))
                                                telefonosEnBd.Add(telBdLimpio);

                                            // Agregamos también el crudo por precaución absoluta
                                            telefonosEnBd.Add(telBd);
                                        }
                                    }
                                }
                            }

                            string sqlFindUser = @"SELECT ""Id_Usuario"" FROM ""Sist_Usuarios"" WHERE ""Telefono"" LIKE '%' || @tel LIMIT 1";
                            string sqlIns = @"INSERT INTO ""Sist_Grupos_Miembros"" (""Id_Grupo"", ""Telefono_Origen"", ""Id_Usuario"", ""Nombre_Importado"") 
                                      VALUES (@idG, @tel, @uid, @nom)";

                            for (int r = FilaInicio; r <= rowCount; r++)
                            {
                                string telRaw = ws.Cell(r, ColumnaTelefono.ToUpper()).GetString();
                                string nombreExcel = string.IsNullOrEmpty(ColumnaNombre) ? "" : ws.Cell(r, ColumnaNombre.ToUpper()).GetString();

                                string telLimpio = Funciones.LimpiarTelefono(telRaw);
                                if (string.IsNullOrEmpty(telLimpio) || telLimpio.Length < 10) continue;

                                // Evitar repetidos dentro del mismo Excel
                                if (!telefonosProcesadosExcel.Add(telLimpio)) continue;

                                // Evitar duplicados exactos contra la Base de Datos
                                if (telefonosEnBd.Contains(telLimpio)) continue;

                                int? idUsuarioFinal = null;

                                using (var cmdFind = new NpgsqlCommand(sqlFindUser, conexion, trans))
                                {
                                    cmdFind.Parameters.AddWithValue("@tel", telLimpio);
                                    var res = await cmdFind.ExecuteScalarAsync();
                                    if (res != null) idUsuarioFinal = (int)res;
                                }

                                if (SoloExistentes && idUsuarioFinal == null) continue;

                                using (var cmdIns = new NpgsqlCommand(sqlIns, conexion, trans))
                                {
                                    cmdIns.Parameters.AddWithValue("@idG", Id_Grupo);
                                    cmdIns.Parameters.AddWithValue("@tel", telLimpio);
                                    cmdIns.Parameters.AddWithValue("@uid", (object)idUsuarioFinal ?? DBNull.Value);
                                    cmdIns.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(nombreExcel) ? "Importado Excel" : nombreExcel);

                                    await cmdIns.ExecuteNonQueryAsync();
                                    agregados++;

                                    // Agregamos a la lista BD para sincronizar el estado actual
                                    telefonosEnBd.Add(telLimpio);
                                }
                            }

                            if (ReemplazarDatos || agregados > 0)
                            {
                                string accionStr = ReemplazarDatos ?
                                    $"Reemplazó lista por {agregados} miembros desde Excel en Grupo #{Id_Grupo}" :
                                    $"Importó {agregados} miembros desde Excel al Grupo #{Id_Grupo}";

                                await Funciones.RegistrarBitacora(conexion, idUserLogueado, Modulo, Parametros.AccionesBitacora.Editar, accionStr, ip, trans);
                            }

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }

                if (agregados > 0) MostrarMensaje("Éxito", $"Se procesaron {agregados} miembros correctamente desde el archivo Excel.", TipoMensaje.Exito);
                else MostrarMensaje("Aviso", "No se agregaron miembros nuevos (posiblemente teléfonos repetidos, inválidos o no cumplen criterios).", TipoMensaje.Info);
            }
            catch (Exception ex) { MostrarMensaje("Error", "Ocurrió un error leyendo el Excel: " + ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = Id_Grupo });
        }

        /// <summary>
        /// Eliminar miembro del grupo de WhatsApp
        /// </summary>
        /// <param name="idMiembro">Es el Id del miembro a eliminar</param>
        /// <param name="idGrupo">Es el Id del grupo</param>
        /// <returns>Retorna a la vista de detalle del grupo.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarMiembro(int idMiembro, int idGrupo)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permisos de borrado en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await new NpgsqlCommand($"DELETE FROM \"Sist_Grupos_Miembros\" WHERE \"Id_Miembro\"={idMiembro}", conexion).ExecuteNonQueryAsync();
                }
                MostrarMensaje("Eliminado", "Miembro quitado del grupo.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = idGrupo });
        }

        /// <summary>
        /// Eliminación masiva de miembros del grupo de WhatsApp
        /// </summary>
        /// <param name="idsSeleccionados">Es la lista de Ids de miembros a eliminar</param>
        /// <param name="idGrupo">Es el Id del grupo</param>
        /// <returns>Retorna a la vista de detalle del grupo.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarMiembrosMasivo(List<int> idsSeleccionados, int idGrupo)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permisos de borrado en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (idsSeleccionados == null || !idsSeleccionados.Any())
            {
                MostrarMensaje("Aviso", "No seleccionaste ningún miembro para eliminar.", TipoMensaje.Alerta);
                return RedirectToAction("Detalle", new { id = idGrupo });
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // BORRADO MASIVO OPTIMIZADO (WHERE IN / ANY)
                            string sql = @"DELETE FROM ""Sist_Grupos_Miembros"" WHERE ""Id_Miembro"" = ANY(@ids)";

                            using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@ids", idsSeleccionados);
                                int afectados = await cmd.ExecuteNonQueryAsync();

                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar,
                                    $"Eliminación masiva de {afectados} miembros del Grupo #{idGrupo}", ip, trans);
                            }

                            await trans.CommitAsync();
                            MostrarMensaje("Eliminados", $"Se eliminaron {idsSeleccionados.Count} miembros correctamente.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { id = idGrupo });
        }

        /// <summary>
        /// Eliminar grupo de WhatsApp
        /// </summary>
        /// <param name="id">Es el Id del grupo a eliminar</param>
        /// <returns>Retorna a la vista de lista de grupos.</returns>
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarGrupo(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Error", "No tienes permisos de borrado en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }

            int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDACIÓN DE USO (Integridad Referencial)
                    string sqlCheck = @"SELECT COUNT(*) FROM ""Eventos_Catalogo"" WHERE ""Id_Grupo_Usuarios"" = @id";
                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                    {
                        cmdCheck.Parameters.AddWithValue("@id", id);
                        long uso = (long)await cmdCheck.ExecuteScalarAsync();

                        if (uso > 0)
                        {
                            MostrarMensaje("No se puede eliminar", $"Este grupo está asignado a {uso} evento(s). Debes desvincularlo de los eventos primero.", TipoMensaje.Alerta);
                            return RedirectToAction("Detalle", new { id = id });
                        }
                    }

                    // 2. BORRADO
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // Borrado en cascada manual
                            await new NpgsqlCommand($"DELETE FROM \"Sist_Grupos_Miembros\" WHERE \"Id_Grupo\"={id}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($"DELETE FROM \"Sist_Grupos_Whatsapp\" WHERE \"Id_Grupo\"={id}", conexion, trans).ExecuteNonQueryAsync();

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar,
                                $"Eliminó Grupo #{id} completo", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
                MostrarMensaje("Eliminado", "Grupo eliminado.", TipoMensaje.Exito);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return RedirectToAction("Index");
        }
    }
}