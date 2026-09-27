using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedAJP.Controllers
{
    public class ReunionesController : GlobalController
    {
        private readonly IConfiguration _configuration;
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Reuniones;

        public ReunionesController(IConfiguration configuration)
        {
            _configuration = configuration;
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // =========================================================
        // 1. INDEX: LISTADO PÚBLICO 
        // =========================================================
        public async Task<IActionResult> Index(int? municipioId)
        {
            var listaIglesiasFiltradas = new List<Iglesia>();
            var listaMapa = new List<object>();
            var listaMunicipios = new List<Municipio>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // A. Cargar Municipios
                    string sqlMun = @"
                        SELECT DISTINCT m.id, m.nombre 
                        FROM iciar_municipios m
                        INNER JOIN iciar_iglesias i ON m.id = i.municipio_id 
                        ORDER BY m.nombre";
                    using (var cmd = new NpgsqlCommand(sqlMun, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            listaMunicipios.Add(new Municipio
                            {
                                Id = Convert.ToInt32(reader["id"]),
                                Nombre = reader["nombre"].ToString()
                            });
                        }
                    }

                    // 1. Obtener el ID del usuario actual
                    int idUsuarioActual = 0;
                    int nIglesiaActual = 0;
                    if (User.Identity.IsAuthenticated)
                    {
                        int.TryParse(User.FindFirst("IdUsuario")?.Value, out idUsuarioActual);

                        string sqlUsuario = @"
                            SELECT u.""Id_Iglesia_Asignada""
                            FROM ""Sist_Usuarios"" u
                            WHERE u.""Id_Usuario"" = @IdUsuario";
                        using (var cmdUsuario = new NpgsqlCommand(sqlUsuario, conexion))
                        {
                            cmdUsuario.Parameters.AddWithValue("@IdUsuario", idUsuarioActual);
                            var resultado = await cmdUsuario.ExecuteScalarAsync();

                            if (resultado != null && resultado != DBNull.Value)
                            {
                                nIglesiaActual = Convert.ToInt32(resultado);
                            }
                        }
                    }

                    string filtro = (municipioId.HasValue && municipioId.Value > 0) ? "WHERE i.municipio_id = @mid" : "";

                    // 2. Consulta actualizada
                    string sqlIglesias = $@"
                    SELECT i.*, m.nombre as nombre_municipio,
                           CASE 
                               WHEN EXISTS (SELECT 1 FROM iciar_iglesias_web WHERE iglesia_id = i.id AND estado = 'PUB' AND activa = TRUE) 
                                AND EXISTS (SELECT 1 FROM iciar_iglesias_administradores WHERE iglesia_id = i.id)
                               THEN 1 ELSE 0 
                           END AS pagina_activa_y_administrada,
                           CASE 
                               WHEN EXISTS (SELECT 1 FROM iciar_iglesias_administradores WHERE iglesia_id = i.id AND usuario_id = @uid)
                               THEN 1 ELSE 0
                           END AS soy_administrador
                    FROM iciar_iglesias i
                    JOIN iciar_municipios m ON i.municipio_id = m.id
                    {filtro}
                    ORDER BY m.nombre, i.nombre";

                    using (var cmd = new NpgsqlCommand(sqlIglesias, conexion))
                    {
                        if (municipioId.HasValue && municipioId.Value > 0) cmd.Parameters.AddWithValue("@mid", municipioId.Value);
                        cmd.Parameters.AddWithValue("@uid", idUsuarioActual);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                listaIglesiasFiltradas.Add(new Iglesia
                                {
                                    Id = Convert.ToInt32(reader["id"]),
                                    EsMiIglesia = (nIglesiaActual == Convert.ToInt32(reader["id"]) ? true : false),
                                    Nombre = reader["nombre"].ToString(),
                                    Slug = reader["slug"].ToString(),
                                    Municipio = reader["nombre_municipio"].ToString(),
                                    Localidad = reader["localidad"]?.ToString(),
                                    Colonia = reader["colonia"]?.ToString(),
                                    Calle = reader["calle"]?.ToString(),
                                    Numero = reader["numero"]?.ToString(),
                                    Referencia = reader["referencia"]?.ToString(),
                                    Horarios = reader["horarios"]?.ToString(),
                                    MapaUrl = reader["mapa_url"]?.ToString(),
                                    FacebookUrl = reader["facebook_url"]?.ToString(),
                                    Latitud = reader["latitud"] != DBNull.Value ? Convert.ToDecimal(reader["latitud"]) : null,
                                    Longitud = reader["longitud"] != DBNull.Value ? Convert.ToDecimal(reader["longitud"]) : null,

                                    Sector = reader["sector"] != DBNull.Value ? Convert.ToInt32(reader["sector"]) : 0,
                                    Zona = reader["zona"] != DBNull.Value ? Convert.ToInt32(reader["zona"]) : 0,

                                    TienePaginaWeb = Convert.ToInt32(reader["pagina_activa_y_administrada"]) == 1,
                                    SoyAdministrador = Convert.ToInt32(reader["soy_administrador"]) == 1
                                });
                            }
                        }
                    }

                    // 3. Verificar si el usuario tiene borradores en riesgo
                    if (idUsuarioActual > 0)
                    {
                        string sqlRiesgo = @"
                            SELECT COUNT(1) 
                            FROM iciar_iglesias_administradores a
                            WHERE a.usuario_id = @uid
                            AND NOT EXISTS (
                                SELECT 1 FROM iciar_iglesias_web w 
                                WHERE w.iglesia_id = a.iglesia_id AND w.estado = 'PUB' AND w.activa = TRUE
                            )";
                        using (var cmdRiesgo = new NpgsqlCommand(sqlRiesgo, conexion))
                        {
                            cmdRiesgo.Parameters.AddWithValue("@uid", idUsuarioActual);
                            ViewBag.TieneBorradoresEnRiesgo = Convert.ToInt32(await cmdRiesgo.ExecuteScalarAsync()) > 0;
                        }
                    }

                    // C. Cargar TODOS los puntos para el mapa
                    string sqlMapa = "SELECT nombre, latitud, longitud FROM iciar_iglesias WHERE latitud IS NOT NULL AND longitud IS NOT NULL";
                    using (var cmd = new NpgsqlCommand(sqlMapa, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            listaMapa.Add(new
                            {
                                Nombre = reader["nombre"].ToString(),
                                Lat = reader["latitud"],
                                Lng = reader["longitud"]
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            ViewBag.Municipios = listaMunicipios;
            ViewBag.MunicipioSeleccionado = municipioId;
            ViewBag.JsonMapa = JsonSerializer.Serialize(listaMapa);

            return View(listaIglesiasFiltradas);
        }

        // =========================================================
        // 2. FORMULARIO: CREAR Y EDITAR 
        // =========================================================

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Formulario(int? id)
        {
            bool esEdicion = id.HasValue && id.Value > 0;

            if (!User.TienePermiso(Modulo, esEdicion ? PermisoEditar : PermisoCrear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para " + (esEdicion ? "Editar" : "Registrar") + " iglesias.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var model = new IglesiaRegistroViewModel();

            if (esEdicion)
            {
                try
                {
                    using (var conexion = new NpgsqlConnection(_cadenaConexion))
                    {
                        await conexion.OpenAsync();
                        string sql = "SELECT * FROM iciar_iglesias WHERE id = @id";
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id.Value);
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    model.Id = Convert.ToInt32(reader["id"]);
                                    model.Nombre = reader["nombre"].ToString();
                                    model.MunicipioId = Convert.ToInt32(reader["municipio_id"]);
                                    model.Localidad = reader["localidad"].ToString();
                                    model.Colonia = reader["colonia"].ToString();
                                    model.Calle = reader["calle"].ToString();
                                    model.Numero = reader["numero"].ToString();
                                    model.Referencia = reader["referencia"] != DBNull.Value ? reader["referencia"].ToString() : null;
                                    model.Horarios = reader["horarios"]?.ToString();
                                    model.MapaUrl = reader["mapa_url"] != DBNull.Value ? reader["mapa_url"].ToString() : null;
                                    model.FacebookUrl = reader["facebook_url"] != DBNull.Value ? reader["facebook_url"].ToString() : null;
                                    model.Latitud = reader["latitud"] != DBNull.Value ? Convert.ToDecimal(reader["latitud"]) : null;
                                    model.Longitud = reader["longitud"] != DBNull.Value ? Convert.ToDecimal(reader["longitud"]) : null;

                                    model.Sector = reader["sector"] != DBNull.Value ? Convert.ToInt32(reader["sector"]) : 0;
                                    model.Zona = reader["zona"] != DBNull.Value ? Convert.ToInt32(reader["zona"]) : 0;
                                }
                                else
                                {
                                    MostrarMensaje("Error", "La iglesia no existe.", TipoMensaje.Error);
                                    return RedirectToAction("Index");
                                }
                            }
                        }

                        // LEER ADMINISTRADORES
                        string sqlAdmins = "SELECT usuario_id FROM iciar_iglesias_administradores WHERE iglesia_id = @id";
                        using (var cmdAdmins = new NpgsqlCommand(sqlAdmins, conexion))
                        {
                            cmdAdmins.Parameters.AddWithValue("@id", id.Value);
                            using (var readerAdmins = await cmdAdmins.ExecuteReaderAsync())
                            {
                                model.AdministradoresIds = new List<int>();
                                while (await readerAdmins.ReadAsync())
                                {
                                    model.AdministradoresIds.Add(Convert.ToInt32(readerAdmins["usuario_id"]));
                                }
                            }
                            model.ActivarPaginaWeb = model.AdministradoresIds.Any();
                        }
                    }
                }
                catch (Exception ex)
                {
                    MostrarMensaje("Error DB", ex.Message, TipoMensaje.Error);
                    return RedirectToAction("Index");
                }
            }

            await CargarMunicipiosParaViewBag();
            await CargarUsuariosParaViewBag();
            ViewData["EsEdicion"] = esEdicion;
            return View(model);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Formulario(IglesiaRegistroViewModel model)
        {
            bool esEdicion = model.Id > 0;
            bool hayError = false;

            if (!User.TienePermiso(Modulo, esEdicion ? Parametros.Permisos.Editar : Parametros.Permisos.Crear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos suficientes.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            // VALIDACIONES DE CAMPOS
            if (string.IsNullOrEmpty(model.Nombre)) { ViewData["ErrorNombre"] = "El nombre de la congregación es obligatorio"; hayError = true; }
            if (model.MunicipioId <= 0) { ViewData["ErrorMunicipio"] = "Selecciona un municipio"; hayError = true; }
            if (string.IsNullOrEmpty(model.Localidad)) { ViewData["ErrorLocalidad"] = "La localidad es obligatoria"; hayError = true; }
            if (string.IsNullOrEmpty(model.Calle)) { ViewData["ErrorCalle"] = "La calle es obligatoria"; hayError = true; }
            if (string.IsNullOrEmpty(model.Numero)) { ViewData["ErrorNumero"] = "El número es obligatorio"; hayError = true; }
            if (string.IsNullOrEmpty(model.Colonia)) { ViewData["ErrorColonia"] = "La colonia es obligatoria"; hayError = true; }
            if (string.IsNullOrEmpty(model.Horarios)) { ViewData["ErrorHorarios"] = "Los horarios son obligatorios"; hayError = true; }

            if (model.ActivarPaginaWeb)
            {
                if (model.AdministradoresIds == null || !model.AdministradoresIds.Any())
                {
                    ViewData["ErrorAdmins"] = "Debes seleccionar al menos un administrador para activar la página.";
                    hayError = true;
                }
            }

            if (string.IsNullOrWhiteSpace(model.MapaUrl))
            {
                ViewData["ErrorMapa"] = "El enlace de Google Maps es obligatorio";
                hayError = true;
            }
            else if (!Uri.TryCreate(model.MapaUrl, UriKind.Absolute, out Uri uriResult)
                     || (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
            {
                ViewData["ErrorMapa"] = "El enlace no es válido (debe iniciar con http:// o https://)";
                hayError = true;
            }

            if (model.Latitud == null)
            {
                ViewData["ErrorLatitud"] = "La latitud es obligatoria";
                hayError = true;
            }
            if (model.Longitud == null)
            {
                ViewData["ErrorLongitud"] = "La longitud es obligatoria";
                hayError = true;
            }

            if (hayError)
            {
                string mensajeError = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault() ?? "Por favor revisa los campos marcados en rojo.";
                MostrarMensaje("Atención", mensajeError, TipoMensaje.Alerta);
                await CargarMunicipiosParaViewBag();
                await CargarUsuariosParaViewBag();
                ViewData["EsEdicion"] = esEdicion;
                return View(model);
            }

            // PROCESO DE GUARDADO
            try
            {
                int idUsuario = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
                string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
                StringBuilder detalleBitacora = new StringBuilder();

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            int idAfectado = model.Id;

                            if (esEdicion)
                            {
                                string sqlOriginal = "SELECT * FROM iciar_iglesias WHERE id = @id";
                                using (var cmdOld = new NpgsqlCommand(sqlOriginal, conexion, transaccion))
                                {
                                    cmdOld.Parameters.AddWithValue("@id", model.Id);
                                    using (var reader = await cmdOld.ExecuteReaderAsync())
                                    {
                                        if (await reader.ReadAsync())
                                        {
                                            detalleBitacora.Append($"Edición Iglesia #{model.Id}. ");
                                            string v1, v2;

                                            v1 = reader["nombre"].ToString(); v2 = model.Nombre;
                                            if (v1 != v2) detalleBitacora.Append($"[Nombre: '{v1}' > '{v2}'] ");

                                            v1 = reader["localidad"].ToString(); v2 = model.Localidad;
                                            if (v1 != v2) detalleBitacora.Append($"[Localidad: '{v1}' > '{v2}'] ");

                                            decimal? latOld = reader["latitud"] != DBNull.Value ? Convert.ToDecimal(reader["latitud"]) : null;
                                            if (latOld != model.Latitud) detalleBitacora.Append("[Cambio de Ubicación GPS] ");

                                            // Bitácora Sector y Zona
                                            int secOld = reader["sector"] != DBNull.Value ? Convert.ToInt32(reader["sector"]) : 0;
                                            if (secOld != model.Sector) detalleBitacora.Append($"[Sector: {secOld} > {model.Sector}] ");

                                            int zonOld = reader["zona"] != DBNull.Value ? Convert.ToInt32(reader["zona"]) : 0;
                                            if (zonOld != model.Zona) detalleBitacora.Append($"[Zona: {zonOld} > {model.Zona}] ");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                detalleBitacora.Append($"Crear nueva iglesia: {model.Nombre} en {model.Localidad}");
                            }

                            string sql;
                            if (esEdicion)
                            {
                                sql = @"UPDATE iciar_iglesias SET 
                                        nombre=@nom, municipio_id=@mun, localidad=@loc, colonia=@col, 
                                        calle=@cal, numero=@num, referencia=@ref, horarios=@hor, 
                                        mapa_url=@map, facebook_url=@face,
                                        latitud=@lat, longitud=@lon, sector=@sec, zona=@zon 
                                        WHERE id=@id";
                            }
                            else
                            {
                                sql = @"INSERT INTO iciar_iglesias 
                                        (nombre, municipio_id, localidad, colonia, calle, numero, referencia, horarios, mapa_url, facebook_url, latitud, longitud, sector, zona)
                                        VALUES 
                                        (@nom, @mun, @loc, @col, @cal, @num, @ref, @hor, @map, @face, @lat, @lon, @sec, @zon)
                                        RETURNING id";
                            }

                            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@nom", model.Nombre);
                                cmd.Parameters.AddWithValue("@mun", model.MunicipioId);
                                cmd.Parameters.AddWithValue("@loc", model.Localidad);
                                cmd.Parameters.AddWithValue("@col", model.Colonia);
                                cmd.Parameters.AddWithValue("@cal", model.Calle);
                                cmd.Parameters.AddWithValue("@num", model.Numero);
                                cmd.Parameters.AddWithValue("@ref", (object)model.Referencia ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@hor", (object)model.Horarios ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@map", (object)model.MapaUrl ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@face", (object)model.FacebookUrl ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@lat", (object)model.Latitud ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@lon", (object)model.Longitud ?? DBNull.Value);

                                // NUEVOS PARÁMETROS
                                cmd.Parameters.AddWithValue("@sec", model.Sector);
                                cmd.Parameters.AddWithValue("@zon", model.Zona);

                                if (esEdicion)
                                {
                                    cmd.Parameters.AddWithValue("@id", model.Id);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                                else
                                {
                                    idAfectado = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                                }
                            }

                            // GUARDAR ADMINISTRADORES
                            using (var cmdDelAdmins = new NpgsqlCommand("DELETE FROM iciar_iglesias_administradores WHERE iglesia_id = @iglesiaId", conexion, transaccion))
                            {
                                cmdDelAdmins.Parameters.AddWithValue("@iglesiaId", idAfectado);
                                await cmdDelAdmins.ExecuteNonQueryAsync();
                            }

                            if (model.ActivarPaginaWeb && model.AdministradoresIds != null)
                            {
                                string sqlInsertAdmin = "INSERT INTO iciar_iglesias_administradores (iglesia_id, usuario_id) VALUES (@iglesiaId, @usuarioId)";
                                foreach (var adminId in model.AdministradoresIds)
                                {
                                    using (var cmdAdmin = new NpgsqlCommand(sqlInsertAdmin, conexion, transaccion))
                                    {
                                        cmdAdmin.Parameters.AddWithValue("@iglesiaId", idAfectado);
                                        cmdAdmin.Parameters.AddWithValue("@usuarioId", adminId);
                                        await cmdAdmin.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            string detalleFinal = detalleBitacora.ToString();
                            if (esEdicion && detalleFinal.Length < 30) detalleFinal += "Sin cambios detectados.";

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo,
                                esEdicion ? Parametros.AccionesBitacora.Editar : Parametros.AccionesBitacora.Crear,
                                detalleFinal, ipUsuario, transaccion);

                            await transaccion.CommitAsync();
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }

                MostrarMensaje("Éxito", "Operación realizada correctamente.", TipoMensaje.Exito);
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Error al procesar: " + ex.Message, TipoMensaje.Error);
                await CargarMunicipiosParaViewBag();
                ViewData["EsEdicion"] = esEdicion;
                return View(model);
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> SolicitarPaginaWeb(int iglesiaId)
        {
            try
            {
                int idUsuario = int.Parse(User.FindFirst("IdUsuario")?.Value ?? "0");
                if (idUsuario <= 0) return Json(new { exito = false, mensaje = "Tu sesión no es válida o ha expirado." });

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlCheck = @"
                            SELECT CASE 
                                WHEN EXISTS (SELECT 1 FROM iciar_iglesias_web WHERE iglesia_id = @iglesiaId AND estado = 'PUB' AND activa = TRUE)
                                 AND EXISTS (SELECT 1 FROM iciar_iglesias_administradores WHERE iglesia_id = @iglesiaId)
                                THEN 1 ELSE 0 
                            END";

                            using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion, transaccion))
                            {
                                cmdCheck.Parameters.AddWithValue("@iglesiaId", iglesiaId);
                                int esIntocable = Convert.ToInt32(await cmdCheck.ExecuteScalarAsync());

                                if (esIntocable == 1)
                                {
                                    return Json(new { exito = false, mensaje = "Esta congregación ya cuenta con una página web pública y administradores activos. No es posible reclamarla." });
                                }
                            }

                            string sqlDeleteAdmins = "DELETE FROM iciar_iglesias_administradores WHERE iglesia_id = @iglesiaId";
                            using (var cmdDel = new NpgsqlCommand(sqlDeleteAdmins, conexion, transaccion))
                            {
                                cmdDel.Parameters.AddWithValue("@iglesiaId", iglesiaId);
                                await cmdDel.ExecuteNonQueryAsync();
                            }

                            string sqlInsert = "INSERT INTO iciar_iglesias_administradores (iglesia_id, usuario_id) VALUES (@iglesiaId, @usuarioId)";
                            using (var cmdInsert = new NpgsqlCommand(sqlInsert, conexion, transaccion))
                            {
                                cmdInsert.Parameters.AddWithValue("@iglesiaId", iglesiaId);
                                cmdInsert.Parameters.AddWithValue("@usuarioId", idUsuario);
                                await cmdInsert.ExecuteNonQueryAsync();
                            }

                            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Crear, $"Reclamó y activó administración web de la Iglesia #{iglesiaId}", ipUsuario, transaccion);

                            await transaccion.CommitAsync();

                            try
                            {
                                string nombreIglesia = "";
                                string nombreUsuario = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Un miembro de la red";

                                using (var cmdName = new NpgsqlCommand("SELECT nombre FROM iciar_iglesias WHERE id = @id", conexion))
                                {
                                    cmdName.Parameters.AddWithValue("@id", iglesiaId);
                                    nombreIglesia = (await cmdName.ExecuteScalarAsync())?.ToString() ?? $"Iglesia #{iglesiaId}";
                                }

                                string urlAdmin = Url.Action("Index", "Reuniones", null, Request.Scheme);

                                string htmlSolicitud = $@"
                                <div style='font-family: Arial, Helvetica, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
                                    <div style='background-color: #6f42c1; padding: 20px; text-align: center; color: #ffffff;'>
                                        <h2 style='margin: 0; font-size: 22px; font-weight: 600;'>🚀 Nueva Página Solicitada</h2>
                                    </div>
                                    <div style='padding: 30px; background-color: #ffffff; color: #333333;'>
                                        <p style='font-size: 16px; margin-top: 0;'>Hola, <strong>Equipo Administrador</strong>:</p>
                                        <p style='font-size: 16px; line-height: 1.6;'>Un usuario acaba de reclamar la administración de una página web en el directorio. Ahora tiene acceso al constructor visual para comenzar a diseñarla.</p>

                                        <div style='background-color: #f8f9fa; border-left: 5px solid #6f42c1; padding: 18px; margin: 25px 0; border-radius: 4px;'>
                                            <ul style='margin: 0; padding-left: 20px; line-height: 1.8; font-size: 15px;'>
                                                <li><strong>Iglesia:</strong> {nombreIglesia}</li>
                                                <li><strong>ID Iglesia:</strong> #{iglesiaId}</li>
                                                <li><strong>Reclamado por:</strong> {nombreUsuario} (ID: {idUsuario})</li>
                                                <li><strong>Fecha:</strong> {DateTime.Now.ToString("dd/MM/yyyy HH:mm")}</li>
                                            </ul>
                                        </div>
            
                                        <p style='font-size: 14px; color: #666;'><em>*Esta página aún está en fase de borrador y no es visible al público general. Recibirás otra alerta cuando la publiquen.</em></p>
            
                                        <div style='text-align: center; margin-top: 30px; margin-bottom: 10px;'>
                                            <a href='{urlAdmin}' style='background-color: #343a40; color: #ffffff; padding: 14px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; font-size: 15px; display: inline-block;'>
                                                Ver Directorio
                                            </a>
                                        </div>
                                    </div>
                                </div>";

                                await Funciones.EnviarAlertaPorBaseDatos(_configuration, "WEB_IGLESIA_SOLICITADA", $"Web Reclamada: {nombreIglesia}", htmlSolicitud);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error al enviar alerta de web solicitada: {ex.Message}");
                            }
                        }
                        catch (Exception)
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }

                return Json(new { exito = true, mensaje = "¡Felicidades! Se te ha asignado la página. Puedes comenzar a diseñarla desde tu Panel." });
            }
            catch (Exception ex)
            {
                return Json(new { exito = false, mensaje = "Ocurrió un error: " + ex.Message });
            }
        }

        // =========================================================
        // 3. AUXILIARES
        // =========================================================
        private async Task CargarMunicipiosParaViewBag()
        {
            var lista = new List<Municipio>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand("SELECT id, nombre FROM iciar_municipios ORDER BY nombre", conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            lista.Add(new Municipio
                            {
                                Id = Convert.ToInt32(reader["id"]),
                                Nombre = reader["nombre"].ToString()
                            });
                        }
                    }
                }
            }
            catch { }
            ViewBag.Municipios = lista;
        }

        private async Task CargarUsuariosParaViewBag()
        {
            var listaUsuarios = new List<dynamic>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = "SELECT \"Id_Usuario\" as Id, \"NombreCompleto\" as Nombre FROM \"Sist_Usuarios\" WHERE \"Activo\" = TRUE ORDER BY \"NombreCompleto\"";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            listaUsuarios.Add(new
                            {
                                Id = Convert.ToInt32(reader["Id"]),
                                Nombre = reader["Nombre"].ToString()
                            });
                        }
                    }
                }
            }
            catch { }
            ViewBag.UsuariosDisponibles = listaUsuarios;
        }
    }
}