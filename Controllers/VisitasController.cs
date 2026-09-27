using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Controllers
{
    [Authorize]
    public class VisitasController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Visitas;

        public VisitasController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        public async Task<IActionResult> Index(DateTime? inicio, DateTime? fin)
        {
            if (!User.Identity.IsAuthenticated) { 
                return RedirectToAction("Index", "Login"); 
            }
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes autorización para ver las métricas y reportes de visitas.", TipoMensaje.Error);
                return RedirectToAction("Nosotros", "Home"); // Lo mandamos a una página segura
            }

            var modelo = new VisitasViewModel();
            modelo.DataHoras = new int[24];

            // 1. RANGO DE FECHAS: Se apoya en la configuración de zona horaria de la BD (México)
            DateTime hoy = DateTime.Now;
            modelo.FechaInicio = inicio ?? hoy.Date;
            modelo.FechaFin = fin ?? hoy.Date.AddHours(23).AddMinutes(59).AddSeconds(59);

            try
            {
                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    // A. MÉTRICAS GENERALES (Número REAL de visitantes/sesiones)
                    var cmdTotal = new NpgsqlCommand(@"
                    SELECT COUNT(*) FROM (
                        SELECT 1 FROM ""Sist_Visitas"" 
                        WHERE ""Fecha"" >= @i AND ""Fecha"" <= @f 
                        GROUP BY ""Ip_Address"", ""Id_Usuario"", ""Fecha""::date
                    ) as Sesiones", con);
                    cmdTotal.Parameters.AddWithValue("@i", modelo.FechaInicio);
                    cmdTotal.Parameters.AddWithValue("@f", modelo.FechaFin);
                    modelo.TotalEnRango = (long)await cmdTotal.ExecuteScalarAsync();

                    var cmdHoy = new NpgsqlCommand(@"
                    SELECT COUNT(*) FROM (
                        SELECT 1 FROM ""Sist_Visitas"" 
                        WHERE ""Fecha""::date = CURRENT_DATE 
                        GROUP BY ""Ip_Address"", ""Id_Usuario"", ""Fecha""::date
                    ) as Sesiones", con);
                    modelo.VisitasHoy = (long)await cmdHoy.ExecuteScalarAsync();

                    // B. ANÁLISIS DE DISPOSITIVOS
                    var cmdDisp = new NpgsqlCommand(@"SELECT ""Dispositivo"", COUNT(*) as Cnt 
                                              FROM ""Sist_Visitas"" 
                                              WHERE ""Fecha"" >= @i AND ""Fecha"" <= @f
                                              GROUP BY ""Dispositivo""", con);
                    cmdDisp.Parameters.AddWithValue("@i", modelo.FechaInicio);
                    cmdDisp.Parameters.AddWithValue("@f", modelo.FechaFin);

                    using (var r = await cmdDisp.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            string tipo = r["Dispositivo"].ToString();
                            int cant = Convert.ToInt32(r["Cnt"]);
                            if (tipo.Contains("PC")) modelo.PcCount += cant;
                            else modelo.MovilCount += cant;
                        }
                    }
                    modelo.DispositivoTop = modelo.MovilCount > modelo.PcCount ? "Celular" : "Computadora";

                    // C. CLASIFICACIÓN AUTOMÁTICA POR SECCIÓN (MÓDULOS)
                    string sqlModulos = @"
                        SELECT 
                            CASE 
                                WHEN ""Url_Visitada"" LIKE '/Tienda%' THEN 'Tienda'
                                WHEN ""Url_Visitada"" LIKE '/Visitas%' THEN 'Reportes'
                                WHEN ""Url_Visitada"" LIKE '/Login%' THEN 'Acceso'
                                WHEN ""Url_Visitada"" = '/' THEN 'Inicio'
                                ELSE 'Otros'
                            END as Seccion, 
                            COUNT(*) as Cnt
                        FROM ""Sist_Visitas"" 
                        WHERE ""Fecha"" >= @i AND ""Fecha"" <= @f
                        GROUP BY Seccion
                        ORDER BY Cnt DESC";

                    using (var cmdM = new NpgsqlCommand(sqlModulos, con))
                    {
                        cmdM.Parameters.AddWithValue("@i", modelo.FechaInicio);
                        cmdM.Parameters.AddWithValue("@f", modelo.FechaFin);
                        using (var r = await cmdM.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.VisitasPorModulo.Add(r["Seccion"].ToString(), Convert.ToInt32(r["Cnt"]));
                            }
                        }
                    }

                    // D. TENDENCIA POR HORAS
                    var cmdHoras = new NpgsqlCommand(@"SELECT EXTRACT(HOUR FROM ""Fecha"") as Hora, COUNT(*) as Cnt 
                                               FROM ""Sist_Visitas"" 
                                               WHERE ""Fecha"" >= @i AND ""Fecha"" <= @f
                                               GROUP BY Hora", con);
                    cmdHoras.Parameters.AddWithValue("@i", modelo.FechaInicio);
                    cmdHoras.Parameters.AddWithValue("@f", modelo.FechaFin);

                    using (var r = await cmdHoras.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            int hora = Convert.ToInt32(r["Hora"]);
                            if (hora >= 0 && hora < 24) modelo.DataHoras[hora] = Convert.ToInt32(r["Cnt"]);
                        }
                    }

                    // E. TABLA DETALLADA DE VISITAS (Preparada para AJAX)
                    string sqlList = @"
                    SELECT 
                        v.""Ip_Address"", 
                        MAX(u.""NombreCompleto"") as ""NombreCompleto"",
                        MIN(v.""Fecha"") as ""HoraInicio"",
                        MAX(v.""Dispositivo"") as ""Dispositivo"",
                        COUNT(v.""Id_Visita"") as ""TotalPaginas""
                    FROM ""Sist_Visitas"" v
                    LEFT JOIN ""Sist_Usuarios"" u ON v.""Id_Usuario"" = u.""Id_Usuario""
                    WHERE v.""Fecha"" >= @i AND v.""Fecha"" <= @f
                    GROUP BY v.""Ip_Address"", v.""Id_Usuario"", v.""Fecha""::date
                    ORDER BY ""HoraInicio"" DESC LIMIT 200";

                    using (var cmdL = new NpgsqlCommand(sqlList, con))
                    {
                        cmdL.Parameters.AddWithValue("@i", modelo.FechaInicio);
                        cmdL.Parameters.AddWithValue("@f", modelo.FechaFin);
                        using (var r = await cmdL.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.UltimasVisitas.Add(new VisitaDetalle
                                {
                                    // Mantenemos el formato para mostrar en la tabla, pero el JS extraerá solo la fecha (los primeros 10 caracteres)
                                    Fecha = Convert.ToDateTime(r["HoraInicio"]).ToString("dd/MM/yyyy HH:mm"),
                                    Ip = r["Ip_Address"].ToString(),
                                    Pagina = r["TotalPaginas"].ToString(), // Guardamos solo el número de páginas
                                    Dispositivo = r["Dispositivo"].ToString(),
                                    Usuario = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Anónimo"
                                });
                            }
                        }
                    }

                    // F. BITÁCORA DE AUDITORÍA (Solo si tiene permiso de Admin)
                    if (User.TienePermiso(Modulo, PermisoLeer))
                    {
                        string sqlBit = @"
                            SELECT b.*, u.""NombreCompleto"" 
                            FROM ""Sist_Bitacora"" b
                            LEFT JOIN ""Sist_Usuarios"" u ON b.""Id_Usuario"" = u.""Id_Usuario""
                            WHERE b.""Fecha"" >= @i AND b.""Fecha"" <= @f
                            ORDER BY b.""Fecha"" DESC LIMIT 200";

                        using (var cmdB = new NpgsqlCommand(sqlBit, con))
                        {
                            cmdB.Parameters.AddWithValue("@i", modelo.FechaInicio);
                            cmdB.Parameters.AddWithValue("@f", modelo.FechaFin);
                            using (var r = await cmdB.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    modelo.LogBitacora.Add(new BitacoraItem
                                    {
                                        Fecha = Convert.ToDateTime(r["Fecha"]).ToString("dd/MM/yyyy HH:mm"),
                                        Modulo = r["Modulo"].ToString(),
                                        Accion = r["Accion"].ToString(),
                                        Detalle = r["Detalle"].ToString(),
                                        Usuario = r["Id_Usuario"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Sistema",
                                        Ip = r["IP"].ToString()
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Análisis", ex.Message, TipoMensaje.Error);
            }

            return View(modelo);
        }

        [HttpGet]
        public async Task<IActionResult> ObtenerHistorialAjax(string ip, string fecha)
        {
            if (!User.Identity.IsAuthenticated || !User.TienePermiso(Modulo, PermisoLeer))
            {
                return Json(new { success = false, message = "Acceso denegado. No tienes permisos para consultar este historial." });
            }
            var historial = new List<object>();
            try
            {
                // Convertimos el texto "dd/MM/yyyy" a Fecha real
                DateTime date = DateTime.ParseExact(fecha, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

                using (var con = new NpgsqlConnection(_cadenaConexion))
                {
                    await con.OpenAsync();

                    string sql = @"
                SELECT ""Url_Visitada"", ""Fecha""
                FROM ""Sist_Visitas""
                WHERE ""Ip_Address"" = @ip AND ""Fecha""::date = @fecha
                ORDER BY ""Fecha"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@ip", ip);
                        cmd.Parameters.AddWithValue("@fecha", date.Date);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                historial.Add(new
                                {
                                    url = r["Url_Visitada"].ToString(),
                                    hora = Convert.ToDateTime(r["Fecha"]).ToString("HH:mm:ss") // Solo la hora exacta
                                });
                            }
                        }
                    }
                }
                return Json(new { success = true, data = historial });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}