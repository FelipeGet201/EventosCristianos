using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Npgsql;
using RedAJP.Globales;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    public class ColoresController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Colores;

        public ColoresController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // =========================================================
        // VIEWMODELS Y CLASES AUXILIARES
        // =========================================================
        public class CatalogoViewModel
        {
            public List<PaletaItem> Paletas { get; set; } = new List<PaletaItem>();
        }

        public class PaletaItem
        {
            public int IdPaleta { get; set; }
            public string NombreTema { get; set; }
            public string Descripcion { get; set; }
            public List<ColorItem> Colores { get; set; } = new List<ColorItem>();
        }

        public class ColorItem
        {
            public string hex { get; set; }
            public string nombre { get; set; }
        }

        // =========================================================
        // 1. CATÁLOGO DE PALETAS (INDEX)
        // =========================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Verificamos si tiene permisos de lectura o gestión
            bool puedeVerCatalogo = User.TienePermiso(Modulo, PermisoLeer) ||
                                    User.TienePermiso(Modulo, PermisoCrear) ||
                                    User.TienePermiso(Modulo, PermisoEditar);

            // Si NO tiene permiso para ver el catálogo, lo redireccionamos a la herramienta libre
            if (!puedeVerCatalogo)
            {
                return RedirectToAction("Editor");
            }

            var modelo = new CatalogoViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
                        SELECT pt.""IdPaleta"", pt.""NombreTema"", pt.""Descripcion"", pc.""CodigoHex"", pc.""Nombre""
                        FROM ""Paletas_Tematicas"" pt
                        LEFT JOIN ""Paletas_Colores"" pc ON pt.""IdPaleta"" = pc.""IdPaleta""
                        ORDER BY pt.""IdPaleta"" DESC, pc.""Orden"" ASC";

                    var paletasDict = new Dictionary<int, PaletaItem>();

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            int idPaleta = (int)r["IdPaleta"];

                            if (!paletasDict.ContainsKey(idPaleta))
                            {
                                paletasDict[idPaleta] = new PaletaItem
                                {
                                    IdPaleta = idPaleta,
                                    NombreTema = r["NombreTema"].ToString(),
                                    Descripcion = r["Descripcion"]?.ToString() ?? ""
                                };
                            }

                            if (r["CodigoHex"] != DBNull.Value)
                            {
                                paletasDict[idPaleta].Colores.Add(new ColorItem
                                {
                                    hex = r["CodigoHex"].ToString(),
                                    nombre = r["Nombre"] != DBNull.Value ? r["Nombre"].ToString() : ""
                                });
                            }
                        }
                    }

                    modelo.Paletas = paletasDict.Values.ToList();
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo cargar el catálogo: " + ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        // =========================================================
        // 2. VISTA DEL EDITOR (CREAR / EDITAR / HERRAMIENTA)
        // =========================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Editor(int? id)
        {
            // 1. Validamos si tiene permisos administrativos sobre los colores
            bool puedeGestionar = User.TienePermiso(Modulo, PermisoCrear) || User.TienePermiso(Modulo, PermisoEditar);
            ViewBag.PuedeGestionar = puedeGestionar;

            // 2. Si no tiene permisos, PERO intenta abrir una paleta guardada (id tiene valor), lo bloqueamos.
            if (!puedeGestionar && id.HasValue)
            {
                MostrarMensaje("Sin Permisos", "No tienes autorización para ver o editar las paletas del sistema.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home"); // Lo enviamos a Home porque el catálogo también está bloqueado para él
            }

            // 3. Si es creación nueva o simplemente un usuario usando la herramienta
            if (!id.HasValue)
            {
                ViewBag.PaletaJson = "[]";
                return View(new PaletaItem());
            }

            // 4. Si es edición (y pasó las validaciones de permiso arriba)
            var modelo = new PaletaItem();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
                        SELECT pt.""IdPaleta"", pt.""NombreTema"", pt.""Descripcion"", pc.""CodigoHex"", pc.""Nombre""
                        FROM ""Paletas_Tematicas"" pt
                        LEFT JOIN ""Paletas_Colores"" pc ON pt.""IdPaleta"" = pc.""IdPaleta""
                        WHERE pt.""IdPaleta"" = @id
                        ORDER BY pc.""Orden"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id.Value);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            bool hasRows = false;
                            while (await r.ReadAsync())
                            {
                                if (!hasRows)
                                {
                                    modelo.IdPaleta = (int)r["IdPaleta"];
                                    modelo.NombreTema = r["NombreTema"].ToString();
                                    modelo.Descripcion = r["Descripcion"]?.ToString() ?? "";
                                    hasRows = true;
                                }

                                if (r["CodigoHex"] != DBNull.Value)
                                {
                                    modelo.Colores.Add(new ColorItem
                                    {
                                        hex = r["CodigoHex"].ToString(),
                                        nombre = r["Nombre"] != DBNull.Value ? r["Nombre"].ToString() : ""
                                    });
                                }
                            }

                            if (!hasRows)
                            {
                                MostrarMensaje("Error", "La paleta solicitada no existe.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }
                        }
                    }
                }

                // Pasamos los colores como JSON para que el Javascript los cargue en el lienzo
                ViewBag.PaletaJson = JsonConvert.SerializeObject(modelo.Colores);
                return View(modelo);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }
        }

        // =========================================================
        // 3. GUARDAR O ACTUALIZAR PALETA (POST)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarPaleta(int? IdPaleta, string NombreTema, string Descripcion, string ColoresJson)
        {
            bool esEdicion = IdPaleta.HasValue && IdPaleta.Value > 0;

            // BLINDAJE DE SEGURIDAD: Validamos estrictamente los permisos
            if (esEdicion && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes autorización para editar paletas.", TipoMensaje.Error);
                return RedirectToAction("Editor"); // Redirige a la herramienta libre
            }
            if (!esEdicion && !User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes autorización para guardar paletas en el catálogo.", TipoMensaje.Error);
                return RedirectToAction("Editor"); // Redirige a la herramienta libre
            }

            if (string.IsNullOrWhiteSpace(NombreTema))
            {
                MostrarMensaje("Datos incompletos", "Debes asignarle un nombre a tu temática.", TipoMensaje.Alerta);
                return RedirectToAction("Editor", new { id = IdPaleta });
            }

            List<ColorItem> listaColores = new List<ColorItem>();
            if (!string.IsNullOrEmpty(ColoresJson))
            {
                try { listaColores = JsonConvert.DeserializeObject<List<ColorItem>>(ColoresJson); }
                catch { }
            }

            if (listaColores.Count == 0)
            {
                MostrarMensaje("Lienzo Vacío", "No puedes guardar una paleta sin colores.", TipoMensaje.Alerta);
                return RedirectToAction("Editor", new { id = IdPaleta });
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
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
                            int idPaletaActual = 0;

                            if (esEdicion)
                            {
                                idPaletaActual = IdPaleta.Value;
                                string sqlUpd = @"UPDATE ""Paletas_Tematicas"" SET ""NombreTema"" = @nom, ""Descripcion"" = @desc WHERE ""IdPaleta"" = @id";
                                using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@nom", NombreTema.Trim());
                                    cmd.Parameters.AddWithValue("@desc", string.IsNullOrWhiteSpace(Descripcion) ? (object)DBNull.Value : Descripcion.Trim());
                                    cmd.Parameters.AddWithValue("@id", idPaletaActual);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                // Borrar colores viejos para insertar los nuevos
                                string sqlDel = @"DELETE FROM ""Paletas_Colores"" WHERE ""IdPaleta"" = @id";
                                using (var cmdD = new NpgsqlCommand(sqlDel, conexion, trans))
                                {
                                    cmdD.Parameters.AddWithValue("@id", idPaletaActual);
                                    await cmdD.ExecuteNonQueryAsync();
                                }
                            }
                            else
                            {
                                string sqlPaleta = @"INSERT INTO ""Paletas_Tematicas"" (""IdUsuarioCreador"", ""NombreTema"", ""Descripcion"") VALUES (@uid, @nom, @desc) RETURNING ""IdPaleta""";
                                using (var cmd = new NpgsqlCommand(sqlPaleta, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@uid", idUser);
                                    cmd.Parameters.AddWithValue("@nom", NombreTema.Trim());
                                    cmd.Parameters.AddWithValue("@desc", string.IsNullOrWhiteSpace(Descripcion) ? (object)DBNull.Value : Descripcion.Trim());
                                    idPaletaActual = (int)await cmd.ExecuteScalarAsync();
                                }
                            }

                            short orden = 1;
                            foreach (var color in listaColores)
                            {
                                string sqlColor = @"INSERT INTO ""Paletas_Colores"" (""IdPaleta"", ""CodigoHex"", ""Orden"", ""Nombre"") VALUES (@id, @hex, @ord, @nomC)";
                                using (var cmdC = new NpgsqlCommand(sqlColor, conexion, trans))
                                {
                                    cmdC.Parameters.AddWithValue("@id", idPaletaActual);
                                    cmdC.Parameters.AddWithValue("@hex", color.hex);
                                    cmdC.Parameters.AddWithValue("@ord", orden);
                                    cmdC.Parameters.AddWithValue("@nomC", string.IsNullOrWhiteSpace(color.nombre) ? (object)DBNull.Value : color.nombre.Trim());
                                    await cmdC.ExecuteNonQueryAsync();
                                }
                                orden++;
                            }

                            string accion = esEdicion ? "Actualizó" : "Creó";
                            string msj = $"{accion} la paleta '{NombreTema}' con {listaColores.Count} colores.";
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, esEdicion ? Parametros.AccionesBitacora.Editar : Parametros.AccionesBitacora.Crear, msj, ip, trans);

                            await trans.CommitAsync();

                            MostrarMensaje("¡Paleta Guardada!", $"Se ha {(esEdicion ? "actualizado" : "guardado")} la paleta de colores correctamente.", TipoMensaje.Exito);
                            return RedirectToAction("Index");
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Editor", new { id = IdPaleta });
            }
        }
    }
}