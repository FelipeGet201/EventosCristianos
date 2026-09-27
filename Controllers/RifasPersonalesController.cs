using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using RedAJP.Servicios;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Stripe.Terminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Controllers
{
    [Authorize]
    public class RifasPersonalesController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IConfiguration _configuration;
        private readonly Cloudinary _cloudinary;
        private Parametros.Modulo Modulo = Parametros.Modulos.RifasPersonales;
        private readonly IWebHostEnvironment _env;

        // Credenciales Twilio
        private readonly string _twilioAccountSid;
        private readonly string _twilioAuthToken;
        private readonly string _twilioWhatsAppNumber;

        public RifasPersonalesController(IConfiguration configuration, IWebHostEnvironment env)
        {
            _configuration = configuration;
            _cadenaConexion = _configuration.GetConnectionString("MiConexion");
            _env = env;
            // Configuración Cloudinary
            CloudinaryDotNet.Account account = new CloudinaryDotNet.Account(
                _configuration["Cloudinary:CloudName"],
                _configuration["Cloudinary:ApiKey"],
                _configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;

            // Configuración Twilio
            _twilioAccountSid = _configuration["Twilio:AccountSid"];
            _twilioAuthToken = _configuration["Twilio:AuthToken"];
            _twilioWhatsAppNumber = _configuration["Twilio:WhatsAppNumber"];

            if (!string.IsNullOrEmpty(_twilioAccountSid) && !string.IsNullOrEmpty(_twilioAuthToken))
            {
                TwilioClient.Init(_twilioAccountSid, _twilioAuthToken);
            }
        }

        // ========================================================================
        // HELPER PRIVADO: BLINDAJE DE SEGURIDAD POR ROL
        // ========================================================================
        private async Task<(bool Existe, bool EsCreador, bool EsVendedor, string Titulo, string Estado, DateTime fechasorteo, decimal CostoXBoleto)> ValidarAccesoRifa(int idRifa, int idUsuarioActual, NpgsqlConnection conexion)
        {
            bool existe = false, esCreador = false, esVendedor = false;
            string titulo = "", estado = "", imagenUrl = "";
            DateTime fechasorteo = new DateTime(1900,1,1);
            decimal CostoBoleto = 0;

            // 1. Validar si existe y si es el creador (Se agregó "ImagenUrl" a la consulta)
            string sqlRifa = @"SELECT ""IdUsuarioCreador"", ""Titulo"", ""Estado"", ""ImagenUrl"", ""FechaSorteo"", ""CostoBoleto"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id";
            using (var cmd = new NpgsqlCommand(sqlRifa, conexion))
            {
                cmd.Parameters.AddWithValue("@id", idRifa);
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        existe = true;
                        titulo = r["Titulo"].ToString();
                        estado = r["Estado"].ToString();
                        imagenUrl = r["ImagenUrl"]?.ToString();
                        CostoBoleto = (decimal)r["CostoBoleto"];
                        esCreador = (int)r["IdUsuarioCreador"] == idUsuarioActual;
                        if (!r.IsDBNull(r.GetOrdinal("FechaSorteo")))
                        {
                            DateTime fecha = Convert.ToDateTime(r["FechaSorteo"]);
                            fechasorteo = fecha;
                        }
                    }
                }
            }

            // 2. Si no es el creador, validar si al menos tiene boletos asignados (Vendedor)
            if (existe && !esCreador)
            {
                string sqlBoletos = @"SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @id AND ""IdUsuarioAsignado"" = @uid";
                using (var cmdBol = new NpgsqlCommand(sqlBoletos, conexion))
                {
                    cmdBol.Parameters.AddWithValue("@id", idRifa);
                    cmdBol.Parameters.AddWithValue("@uid", idUsuarioActual);
                    long conteo = (long)await cmdBol.ExecuteScalarAsync();
                    esVendedor = conteo > 0;
                }
            }

            // Configurar variables globales para el Sub-Layout
            if (existe)
            {
                ViewBag.IdRifaActiva = idRifa;
                ViewBag.TituloRifa = titulo;
                ViewBag.EsCreador = esCreador;
                ViewBag.ImagenUrl = imagenUrl; // Se envía la imagen al Layout
            }

            return (existe, esCreador, esVendedor, titulo, estado, fechasorteo, CostoBoleto);
        }

        // ========================================================================
        // 1. LISTADO DE RIFAS (INDEX)
        // ========================================================================
        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para visualizar el listado de rifas.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var lista = new List<RifaPersonalViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
                        SELECT DISTINCT r.* FROM ""RifasPersonales_Rifas"" r
                        LEFT JOIN ""RifasPersonales_Boletos"" b ON r.""IdRifa"" = b.""IdRifa""
                        WHERE r.""IdUsuarioCreador"" = @uid OR b.""IdUsuarioAsignado"" = @uid
                        ORDER BY r.""FechaCreacion"" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                lista.Add(new RifaPersonalViewModel
                                {
                                    IdRifa = (int)r["IdRifa"],
                                    IdUsuarioCreador = (int)r["IdUsuarioCreador"],
                                    Titulo = r["Titulo"].ToString(),
                                    Descripcion = r["Descripcion"].ToString(),
                                    CostoBoleto = (decimal)r["CostoBoleto"],
                                    MetaBoletos = (int)r["MetaBoletos"],
                                    FechaSorteo = (DateTime)r["FechaSorteo"],
                                    Estado = r["Estado"].ToString(),
                                    ImagenUrl = r["ImagenUrl"]?.ToString()
                                });
                            }
                        }
                    }

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Leer, "Gestor Personal: Consultó listado de rifas.", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error del Sistema", "Ocurrió un error al cargar tus rifas: " + ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        // ========================================================================
        // 2. CREAR / EDITAR (GET) - SOLO CREADOR
        // ========================================================================
        public async Task<IActionResult> CrearEditar(string token)
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifa = 0;

            if (!string.IsNullOrEmpty(token))
            {
                idRifa = Funciones.DesencriptarId(token);
                if (idRifa <= 0)
                {
                    MostrarMensaje("Enlace Inválido", "El identificador de la rifa no es válido o está corrupto.", TipoMensaje.Error);
                    return RedirectToAction("Index");
                }

                if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
                {
                    MostrarMensaje("Acceso Denegado", "No tienes permisos para editar rifas.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }
            }
            else
            {
                if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
                {
                    MostrarMensaje("Acceso Denegado", "No tienes permisos para crear nuevas rifas.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }
            }

            var modelo = new RifaPersonalViewModel
            {
                FechaSorteo = DateTime.Now.AddDays(15),
                MetaBoletos = 100,
                CostoBoleto = 50,
                InversionPremio = 0 // Inicializado en 0 por defecto
            };

            var usuariosPlataforma = new List<SelectListItem>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Cargar lista de usuarios para asignar planillas (Solo Activos)
                    string sqlUsr = @"SELECT ""Id_Usuario"", ""NombreCompleto"" FROM ""Sist_Usuarios"" WHERE ""Activo"" = TRUE AND ""Id_Usuario"" != @uid ORDER BY ""NombreCompleto"" ASC";
                    using (var cmdUsr = new NpgsqlCommand(sqlUsr, conexion))
                    {
                        cmdUsr.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmdUsr.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                usuariosPlataforma.Add(new SelectListItem { Value = r["Id_Usuario"].ToString(), Text = r["NombreCompleto"].ToString() });
                            }
                        }
                    }

                    // Si es edición, cargar datos y validar
                    if (idRifa > 0)
                    {
                        var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);

                        if (!acceso.Existe)
                        {
                            MostrarMensaje("No Encontrada", "La rifa que intentas editar no existe.", TipoMensaje.Error);
                            return RedirectToAction("Index");
                        }
                        if (!acceso.EsCreador)
                        {
                            MostrarMensaje("Acceso Restringido", "Solo el administrador de la rifa puede editar su configuración.", TipoMensaje.Alerta);
                            return RedirectToAction("Index");
                        }
                        if (acceso.Estado != "Activa")
                        {
                            MostrarMensaje("Bloqueado", "No se puede editar una rifa que ya finalizó o se canceló.", TipoMensaje.Alerta);
                            return RedirectToAction("PanelAdministracion", new { token = Funciones.EncriptarId(idRifa) });
                        }

                        string sql = @"SELECT * FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id";
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", idRifa);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.IdRifa = (int)r["IdRifa"];
                                    modelo.IdUsuarioCreador = (int)r["IdUsuarioCreador"];
                                    modelo.Titulo = r["Titulo"].ToString();
                                    modelo.Descripcion = r["Descripcion"].ToString();
                                    modelo.CostoBoleto = (decimal)r["CostoBoleto"];
                                    modelo.MetaBoletos = (int)r["MetaBoletos"];
                                    modelo.FechaSorteo = (DateTime)r["FechaSorteo"];
                                    modelo.ImagenUrl = r["ImagenUrl"]?.ToString();
                                    modelo.Estado = r["Estado"].ToString();
                                    modelo.Banco = r["Banco"]?.ToString();
                                    modelo.CuentaClabe = r["CuentaClabe"]?.ToString();
                                    modelo.NumeroCuenta = r["NumeroCuenta"]?.ToString();
                                    modelo.NumeroTarjeta = r["NumeroTarjeta"]?.ToString();
                                    modelo.TitularCuenta = r["TitularCuenta"]?.ToString();
                                    modelo.EnlaceGrupoWhatsapp = r["EnlaceGrupoWhatsapp"]?.ToString();
                                    modelo.QrGrupoWhatsappUrl = r["QrGrupoWhatsappUrl"]?.ToString();
                                    modelo.InversionPremio = r["InversionPremio"] != DBNull.Value ? (decimal)r["InversionPremio"] : 0m;
                                }
                            }
                        }
                    }

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Leer, idRifa > 0 ? $"Gestor Personal: Ingresó a editar rifa {idRifa}" : "Gestor Personal: Ingresó a crear nueva rifa.", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un problema al cargar la vista: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            ViewBag.Usuarios = usuariosPlataforma;
            return View(modelo);
        }

        // ========================================================================
        // 3. GUARDAR (POST) - SOLO CREADOR
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(RifaPersonalViewModel form, IFormFile fotoPortadaNueva, string tokenRifa)
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int idRifa = 0;

            if (!string.IsNullOrEmpty(tokenRifa))
            {
                idRifa = Funciones.DesencriptarId(tokenRifa);
                form.IdRifa = idRifa;
            }

            if (idRifa > 0 && !User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para modificar rifas.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }
            else if (idRifa == 0 && !User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para crear rifas.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(form.EnlaceGrupoWhatsapp))
                {
                    bool esUrlValida = Uri.TryCreate(form.EnlaceGrupoWhatsapp, UriKind.Absolute, out Uri uriResult)
                                       && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                    if (!esUrlValida || !form.EnlaceGrupoWhatsapp.Contains("chat.whatsapp.com"))
                    {
                        throw new Exception("El enlace del grupo de WhatsApp no es válido. Debe ser una URL oficial (ej. https://chat.whatsapp.com/...)");
                    }
                }
                if (form.CostoBoleto <= 0) throw new Exception("El costo del boleto debe ser mayor a 0.");
                if (form.MetaBoletos <= 0) throw new Exception("La cantidad de boletos debe ser mayor a 0.");
                if (form.FechaSorteo <= DateTime.Now) throw new Exception("La fecha del sorteo debe ser en el futuro.");
                if (form.InversionPremio < 0) throw new Exception("El costo del premio no puede ser negativo.");

                bool tieneDatosBancarios = !string.IsNullOrWhiteSpace(form.Banco) || !string.IsNullOrWhiteSpace(form.TitularCuenta) || !string.IsNullOrWhiteSpace(form.CuentaClabe) || !string.IsNullOrWhiteSpace(form.NumeroCuenta) || !string.IsNullOrWhiteSpace(form.NumeroTarjeta);

                if (tieneDatosBancarios && string.IsNullOrWhiteSpace(form.CuentaClabe) && string.IsNullOrWhiteSpace(form.NumeroCuenta) && string.IsNullOrWhiteSpace(form.NumeroTarjeta))
                {
                    throw new Exception("Debes ingresar al menos la Cuenta CLABE, Número de Cuenta o Número de Tarjeta para guardar la información bancaria.");
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    int metaActualEnBD = 0;
                    string imgUrlFinal = form.ImagenUrl;

                    // Variables para gestionar el QR de Whatsapp
                    string enlaceWhatsappActualEnBD = null;
                    string qrWhatsappActualEnBD = null;

                    if (idRifa > 0)
                    {
                        var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                        if (!acceso.EsCreador) throw new Exception("No tienes permisos de administrador sobre esta rifa.");
                        if (acceso.Estado != "Activa") throw new Exception("No puedes editar una rifa inactiva o finalizada.");

                        decimal costoActualEnBD = 0;

                        string sqlVal = @"SELECT ""MetaBoletos"", ""CostoBoleto"", ""EnlaceGrupoWhatsapp"", ""QrGrupoWhatsappUrl"" 
                                  FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa""=@idRifa";
                        using (var cmdVal = new NpgsqlCommand(sqlVal, conexion))
                        {
                            cmdVal.Parameters.AddWithValue("@idRifa", idRifa);
                            using (var r = await cmdVal.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    metaActualEnBD = (int)r["MetaBoletos"];
                                    costoActualEnBD = (decimal)r["CostoBoleto"];
                                    enlaceWhatsappActualEnBD = r["EnlaceGrupoWhatsapp"]?.ToString();
                                    qrWhatsappActualEnBD = r["QrGrupoWhatsappUrl"]?.ToString();
                                }
                            }
                        }

                        if (form.MetaBoletos < metaActualEnBD)
                            throw new Exception("No puedes reducir la cantidad de boletos, solo aumentarla.");

                        if (form.CostoBoleto != costoActualEnBD)
                        {
                            using (var cmdVentas = new NpgsqlCommand("SELECT COUNT(*) FROM \"RifasPersonales_Boletos\" WHERE \"IdRifa\"=@idRifa AND \"Estado\"='Vendido'", conexion))
                            {
                                cmdVentas.Parameters.AddWithValue("@idRifa", idRifa);
                                long ventasRegistradas = (long)await cmdVentas.ExecuteScalarAsync();
                                if (ventasRegistradas > 0)
                                    throw new Exception("No puedes modificar el costo del boleto porque ya existen ventas registradas. Esto rompería la contabilidad.");
                            }
                        }
                    }

                    string nuevoQrWhatsappUrl = idRifa > 0 ? qrWhatsappActualEnBD : null;

                    if (form.EnlaceGrupoWhatsapp != enlaceWhatsappActualEnBD)
                    {
                        if (!string.IsNullOrWhiteSpace(form.EnlaceGrupoWhatsapp))
                        {
                            var qrService = new GeneradorQRService();
                            string base64Qr = string.Empty;

                            string pathLogo = Path.Combine(_env.WebRootPath, "Images", "ws-camera-icon.jpg");

                            if (System.IO.File.Exists(pathLogo))
                            {
                                byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(pathLogo);
                                using (var stream = new MemoryStream(fileBytes))
                                {
                                    IFormFile logoFalso = new FormFile(stream, 0, stream.Length, "Logo", Path.GetFileName(pathLogo))
                                    {
                                        Headers = new HeaderDictionary(),
                                        ContentType = "image/jpeg"
                                    };

                                    base64Qr = qrService.GenerarImagenQR(form.EnlaceGrupoWhatsapp, "#198754", "#FFFFFF", logoFalso, FormaOjos.Redondeado, FormaModulos.Redondeado);
                                }
                            }
                            else
                            {
                                base64Qr = qrService.GenerarImagenQR(form.EnlaceGrupoWhatsapp, "#198754", "#FFFFFF", null, FormaOjos.Redondeado, FormaModulos.Redondeado);
                            }

                            nuevoQrWhatsappUrl = await SubirQrBase64Cloudinary(base64Qr, $"{sAmbiente}/RifasPersonales/QRsWhatsapp/{idUsuario}");

                            if (!string.IsNullOrEmpty(qrWhatsappActualEnBD))
                            {
                                await DestruirImagenCloudinary(qrWhatsappActualEnBD);
                            }
                        }
                        else
                        {
                            nuevoQrWhatsappUrl = null;
                            if (!string.IsNullOrEmpty(qrWhatsappActualEnBD))
                            {
                                await DestruirImagenCloudinary(qrWhatsappActualEnBD);
                            }
                        }
                    }

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            if (fotoPortadaNueva != null)
                            {
                                imgUrlFinal = await SubirImagenCloudinary(fotoPortadaNueva, $"{sAmbiente}/RifasPersonales/Portadas/{idUsuario}");
                            }

                            if (idRifa == 0) // NUEVA RIFA
                            {
                                string sql = @"INSERT INTO ""RifasPersonales_Rifas"" 
                        (""IdUsuarioCreador"", ""Titulo"", ""Descripcion"", ""CostoBoleto"", ""MetaBoletos"", ""FechaSorteo"", ""ImagenUrl"", ""Banco"", ""CuentaClabe"", ""NumeroCuenta"", ""NumeroTarjeta"", ""TitularCuenta"", ""EnlaceGrupoWhatsapp"", ""QrGrupoWhatsappUrl"", ""InversionPremio"")
                        VALUES (@uid, @tit, @desc, @costo, @meta, @sorteo, @img, @banco, @clabe, @numcta, @numtar, @titular, @enlaceWp, @qrUrl, @inversion)
                        RETURNING ""IdRifa""";

                                using (var cmd = new NpgsqlCommand(sql, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", (object)form.Descripcion ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@costo", form.CostoBoleto);
                                    cmd.Parameters.AddWithValue("@meta", form.MetaBoletos);
                                    cmd.Parameters.AddWithValue("@sorteo", form.FechaSorteo);
                                    cmd.Parameters.AddWithValue("@img", (object)imgUrlFinal ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@banco", (object)form.Banco ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@clabe", (object)form.CuentaClabe ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numcta", (object)form.NumeroCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numtar", (object)form.NumeroTarjeta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@titular", (object)form.TitularCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@enlaceWp", (object)form.EnlaceGrupoWhatsapp ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@qrUrl", (object)nuevoQrWhatsappUrl ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@inversion", form.InversionPremio); // NUEVO CAMPO

                                    form.IdRifa = (int)await cmd.ExecuteScalarAsync();
                                }

                                string sqlGen = $@"INSERT INTO ""RifasPersonales_Boletos"" (""IdRifa"", ""Numero"", ""Estado"", ""IdUsuarioAsignado"", ""NombrePromotorExterno"")
                                           SELECT @idR, s.i, 'Disponible', NULL, NULL
                                           FROM generate_series(1, @meta) AS s(i)";
                                using (var cmd = new NpgsqlCommand(sqlGen, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@idR", form.IdRifa);
                                    cmd.Parameters.AddWithValue("@meta", form.MetaBoletos);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }
                            else // EDICIÓN
                            {
                                string sqlUpd = @"UPDATE ""RifasPersonales_Rifas"" SET 
                          ""Titulo""=@tit, ""Descripcion""=@desc, ""CostoBoleto""=@costo, 
                          ""MetaBoletos""=@meta, ""FechaSorteo""=@sorteo, ""ImagenUrl""=COALESCE(@img, ""ImagenUrl""),
                          ""Banco""=@banco, ""CuentaClabe""=@clabe, ""NumeroCuenta""=@numcta, ""NumeroTarjeta""=@numtar, ""TitularCuenta""=@titular,
                          ""EnlaceGrupoWhatsapp""=@enlaceWp, ""QrGrupoWhatsappUrl""=@qrUrl, ""InversionPremio""=@inversion
                          WHERE ""IdRifa""=@id";
                                using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", (object)form.Descripcion ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@costo", form.CostoBoleto);
                                    cmd.Parameters.AddWithValue("@meta", form.MetaBoletos);
                                    cmd.Parameters.AddWithValue("@sorteo", form.FechaSorteo);
                                    cmd.Parameters.AddWithValue("@img", (object)imgUrlFinal ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@banco", (object)form.Banco ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@clabe", (object)form.CuentaClabe ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numcta", (object)form.NumeroCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@numtar", (object)form.NumeroTarjeta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@titular", (object)form.TitularCuenta ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@enlaceWp", (object)form.EnlaceGrupoWhatsapp ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@qrUrl", (object)nuevoQrWhatsappUrl ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@inversion", form.InversionPremio); // NUEVO CAMPO
                                    cmd.Parameters.AddWithValue("@id", form.IdRifa);

                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            var accion = metaActualEnBD == 0 ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar;
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, accion, $"Gestor Personal: Rifa {form.IdRifa} guardada.", ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Éxito", "Configuración de la rifa guardada correctamente.", TipoMensaje.Exito);
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }
                return RedirectToAction("PanelAdministracion", new { token = Funciones.EncriptarId(form.IdRifa) });
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error al Guardar", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }
        }

        private async Task<string> SubirQrBase64Cloudinary(string base64Image, string folderPath)
        {
            // Cloudinary permite subir cadenas Base64 (Data URIs) directamente
            var uploadParams = new ImageUploadParams()
            {
                File = new FileDescription(base64Image),
                Folder = folderPath,
                UseFilename = false,
                UniqueFilename = true
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);
            return uploadResult.SecureUrl.ToString();
        }

        // ========================================================================
        // 4. TABLERO DE CONTROL - SOLO CREADOR
        // ========================================================================
        public async Task<IActionResult> PanelAdministracion(string token)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de lectura en este módulo.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(token);
            if (idRifa <= 0) return RedirectToAction("Index");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new TableroRifaPersonalViewModel
            {
                Boletos = new List<BoletoPersonalViewModel>(),
                Promotores = new List<PromotorPersonalStat>()
            };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.Existe) return RedirectToAction("Index");
                    if (!acceso.EsCreador) return RedirectToAction("PuntoDeVenta", new { token = token });

                    // 1. Cargar Datos Básicos de la Rifa
                    string sqlRifa = @"SELECT * FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlRifa, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.InfoRifa = new RifaPersonalViewModel
                                {
                                    IdRifa = (int)r["IdRifa"],
                                    Titulo = r["Titulo"].ToString(),
                                    CostoBoleto = (decimal)r["CostoBoleto"],
                                    MetaBoletos = (int)r["MetaBoletos"],
                                    FechaSorteo = (DateTime)r["FechaSorteo"],
                                    Estado = r["Estado"].ToString(),
                                    InversionPremio = r["InversionPremio"] != DBNull.Value ? (decimal)r["InversionPremio"] : 0m
                                };
                            }
                        }
                    }

                    // 2. Cargar Estadísticas Detalladas de Boletos
                    int boletosAsignados = 0;
                    int boletosSinAsignar = 0;
                    int boletosVendidos = 0;

                    string sqlStats = @"SELECT ""Estado"", ""IdUsuarioAsignado"", ""NombrePromotorExterno"" FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @id";
                    using (var cmd = new NpgsqlCommand(sqlStats, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.TotalBoletos++;
                                string estado = r["Estado"].ToString();
                                bool estaAsignado = r["IdUsuarioAsignado"] != DBNull.Value || r["NombrePromotorExterno"] != DBNull.Value;

                                if (estado == "Vendido")
                                {
                                    boletosVendidos++;
                                    boletosAsignados++;
                                }
                                else if (estaAsignado)
                                {
                                    boletosAsignados++;
                                }
                                else
                                {
                                    boletosSinAsignar++;
                                }
                            }
                        }
                    }

                    modelo.BoletosVendidos = boletosVendidos;
                    ViewBag.BoletosAsignados = boletosAsignados;
                    ViewBag.BoletosSinAsignar = boletosSinAsignar;

                    // Dinero total si se venden TODOS los boletos (Expectativa)
                    // Pero para cobranza real usamos (BoletosVendidos * Costo)
                    modelo.RecaudacionTotal = modelo.BoletosVendidos * modelo.InfoRifa.CostoBoleto;

                    // 3. Cargar Finanzas y Vendedores
                    string sqlPromotores = @"
                        SELECT 
                            b.""IdUsuarioAsignado"", 
                            b.""NombrePromotorExterno"",
                            u.""NombreCompleto"",
                            COUNT(b.""IdBoleto"") as TotalAsignados,
                            SUM(CASE WHEN b.""Estado"" = 'Vendido' THEN 1 ELSE 0 END) as Vendidos,
                            (SELECT COALESCE(SUM(p.""Monto""), 0) FROM ""RifasPersonales_Pagos"" p 
                             WHERE p.""IdRifa"" = b.""IdRifa"" AND p.""Estado"" = 'Aprobado'
                             AND ((p.""IdUsuarioVendedor"" = b.""IdUsuarioAsignado"" AND b.""IdUsuarioAsignado"" IS NOT NULL) OR (p.""NombrePromotorExterno"" = b.""NombrePromotorExterno"" AND b.""NombrePromotorExterno"" IS NOT NULL))
                            ) as TotalPagado
                        FROM ""RifasPersonales_Boletos"" b
                        LEFT JOIN ""Sist_Usuarios"" u ON b.""IdUsuarioAsignado"" = u.""Id_Usuario""
                        WHERE b.""IdRifa"" = @id AND (b.""IdUsuarioAsignado"" IS NOT NULL OR b.""NombrePromotorExterno"" IS NOT NULL OR b.""Estado"" = 'Vendido')
                        GROUP BY b.""IdRifa"", b.""IdUsuarioAsignado"", b.""NombrePromotorExterno"", u.""NombreCompleto""";

                    using (var cmd = new NpgsqlCommand(sqlPromotores, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int? idAsignado = r["IdUsuarioAsignado"] != DBNull.Value ? (int)r["IdUsuarioAsignado"] : null;
                                string nombreExterno = r["NombrePromotorExterno"]?.ToString();
                                string nombre = r["NombreCompleto"]?.ToString();
                                int totalAsignados = Convert.ToInt32(r["TotalAsignados"]);
                                int vendidos = Convert.ToInt32(r["Vendidos"]);
                                decimal pagado = Convert.ToDecimal(r["TotalPagado"]);

                                if (idAsignado == idUsuario) nombre = "Yo";
                                else if (idAsignado == null && !string.IsNullOrEmpty(nombreExterno)) nombre = nombreExterno + " (Ext)";
                                else if (idAsignado == null && string.IsNullOrEmpty(nombreExterno)) nombre = "Sin Asignar";

                                var promotorExistente = modelo.Promotores.FirstOrDefault(p => p.Nombre == nombre);
                                if (promotorExistente != null)
                                {
                                    promotorExistente.Asignados += totalAsignados;
                                    promotorExistente.Vendidos += vendidos;
                                    promotorExistente.DeudaTotal += (vendidos * modelo.InfoRifa.CostoBoleto);
                                }
                                else
                                {
                                    modelo.Promotores.Add(new PromotorPersonalStat
                                    {
                                        Nombre = nombre,
                                        Asignados = totalAsignados,
                                        Vendidos = vendidos,
                                        DeudaTotal = (vendidos * modelo.InfoRifa.CostoBoleto),
                                        TotalPagado = pagado
                                    });
                                }
                            }
                        }
                    }

                    // Ajustar las deudas reales de cada vendedor
                    foreach (var promotor in modelo.Promotores)
                    {
                        promotor.DeudaTotal = promotor.DeudaTotal - promotor.TotalPagado;
                        if (promotor.DeudaTotal < 0) promotor.DeudaTotal = 0;
                    }

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Leer, $"Gestor Personal: Visualizó Tablero Analítico de Rifa {idRifa}", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Error al cargar las analíticas: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarVentaMasiva(string tokenRifa, List<int> IdsBoletos, List<string> NombresCompradores, List<string> TelefonosCompradores, List<string> Comentarios, List<bool> MarcadoresPersonales)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para acceder al módulo.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (IdsBoletos == null || !IdsBoletos.Any())
            {
                MostrarMensaje("Error", "No se recibieron boletos para procesar.", TipoMensaje.Error);
                return RedirectToAction("PuntoDeVenta", new { token = tokenRifa });
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            int ventasExitosas = 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);

                    // Validamos que sea creador o al menos vendedor
                    if (!acceso.EsCreador && !acceso.EsVendedor)
                        throw new Exception("No tienes boletos asignados en esta rifa.");

                    if (acceso.Estado != "Activa")
                        throw new Exception("La rifa ya no está activa, no se pueden registrar ventas.");

                    // Qué boletos puede tocar este usuario
                    string condicionFiltro = acceso.EsCreador
                        ? @"(""IdUsuarioAsignado"" = @uid OR (""IdUsuarioAsignado"" IS NULL AND ""NombrePromotorExterno"" IS NOT NULL))"
                        : @"(""IdUsuarioAsignado"" = @uid)";

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        for (int i = 0; i < IdsBoletos.Count; i++)
                        {
                            int idBoletoActual = IdsBoletos[i];
                            string nombreActual = NombresCompradores.Count > i ? NombresCompradores[i] : "Anónimo";
                            string comentActual = Comentarios.Count > i ? Comentarios[i] : null;
                            string telCrudo = TelefonosCompradores.Count > i ? TelefonosCompradores[i] : null;
                            string telValidado = LimpiarYValidarTelefono(telCrudo);

                            // Extraemos el valor del marcador para este boleto específico
                            bool marcadorActual = MarcadoresPersonales != null && MarcadoresPersonales.Count > i ? MarcadoresPersonales[i] : false;

                            string sqlUpd = $@"UPDATE ""RifasPersonales_Boletos"" 
                                               SET ""Estado"" = 'Vendido', 
                                                   ""NombreComprador"" = @nom, 
                                                   ""TelefonoComprador"" = @tel, 
                                                   ""Comentarios"" = @com, 
                                                   ""FechaVenta"" = CURRENT_TIMESTAMP,
                                                   ""MarcadorPersonal"" = @marc
                                               WHERE ""IdBoleto"" = @idBol 
                                                 AND ""Estado"" != 'Vendido' 
                                                 AND {condicionFiltro}";

                            using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(nombreActual) ? "Anónimo" : nombreActual);
                                cmd.Parameters.AddWithValue("@tel", (object)telValidado ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@com", (object)comentActual ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@marc", marcadorActual);
                                cmd.Parameters.AddWithValue("@idBol", idBoletoActual);
                                cmd.Parameters.AddWithValue("@uid", idUsuario);

                                int afectadas = await cmd.ExecuteNonQueryAsync();
                                if (afectadas > 0)
                                {
                                    ventasExitosas++;
                                }
                            }
                        }

                        if (ventasExitosas > 0)
                        {
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Gestor Personal: Venta Masiva de {ventasExitosas} boletos en Rifa {idRifa}", ip, trans);

                            await trans.CommitAsync();

                            //Marca en la vista para deshacerse de los datos en memoria de capturas pendientes en su navegador
                            TempData["VentaExitosa"] = true;

                            MostrarMensaje("Éxito", $"Se registraron {ventasExitosas} boletos correctamente.", TipoMensaje.Exito);
                        }
                        else
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Sin Cambios", "No se procesó ninguna venta.", TipoMensaje.Info);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("PuntoDeVenta", new { token = tokenRifa });
        }

        // ========================================================================
        // 5. ASIGNACIONES (GET) - SOLO CREADOR
        // ========================================================================
        public async Task<IActionResult> Asignaciones(string token)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de lectura.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(token);
            if (idRifa <= 0)
            {
                MostrarMensaje("Enlace Inválido", "El identificador de la rifa no es válido.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new TableroRifaPersonalViewModel();
            var usuariosPlataforma = new List<SelectListItem>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.Existe)
                    {
                        MostrarMensaje("No Encontrada", "La rifa no existe.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }
                    if (!acceso.EsCreador)
                    {
                        MostrarMensaje("Acceso Restringido", "Solo el administrador puede ver y gestionar las asignaciones. Te redirigimos a tus boletos.", TipoMensaje.Info);
                        return RedirectToAction("PuntoDeVenta", new { token = token });
                    }

                    // OBTENER COSTO DEL BOLETO PARA CALCULAR DEUDAS
                    decimal costoBoleto = 0;
                    using (var cmdCosto = new NpgsqlCommand(@"SELECT ""CostoBoleto"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id", conexion))
                    {
                        cmdCosto.Parameters.AddWithValue("@id", idRifa);
                        costoBoleto = Convert.ToDecimal(await cmdCosto.ExecuteScalarAsync());
                    }

                    modelo.InfoRifa = new RifaPersonalViewModel { IdRifa = idRifa, Titulo = acceso.Titulo, Estado = acceso.Estado, CostoBoleto = costoBoleto };

                    string sqlUsr = @"SELECT ""Id_Usuario"", ""NombreCompleto"" FROM ""Sist_Usuarios"" WHERE ""Activo"" = TRUE AND ""Id_Usuario"" != @uid ORDER BY ""NombreCompleto"" ASC";
                    using (var cmdUsr = new NpgsqlCommand(sqlUsr, conexion))
                    {
                        cmdUsr.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmdUsr.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                usuariosPlataforma.Add(new SelectListItem { Value = r["Id_Usuario"].ToString(), Text = r["NombreCompleto"].ToString() });
                            }
                        }
                    }
                    ViewBag.Usuarios = usuariosPlataforma;

                    string sqlLibres = @"SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" 
                                 WHERE ""IdRifa"" = @id AND ""Estado"" IN ('Disponible', 'Asignado') 
                                 AND ""IdUsuarioAsignado"" IS NULL AND ""NombrePromotorExterno"" IS NULL";
                    using (var cmd = new NpgsqlCommand(sqlLibres, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        modelo.BoletosLibresParaAsignar = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    }

                    string sqlPromotores = @"
                        SELECT 
                            b.""IdUsuarioAsignado"", 
                            b.""NombrePromotorExterno"",
                            u.""NombreCompleto"",
                            COUNT(b.""IdBoleto"") as TotalAsignados,
                            SUM(CASE WHEN b.""Estado"" = 'Vendido' THEN 1 ELSE 0 END) as Vendidos,
                            SUM(CASE WHEN b.""Estado"" IN ('Disponible', 'Asignado') THEN 1 ELSE 0 END) as Disponibles,
                            (SELECT COALESCE(SUM(p.""Monto""), 0) FROM ""RifasPersonales_Pagos"" p 
                             WHERE p.""IdRifa"" = b.""IdRifa"" 
                             AND p.""Estado"" = 'Aprobado'
                             AND (
                                 (p.""IdUsuarioVendedor"" = b.""IdUsuarioAsignado"" AND b.""IdUsuarioAsignado"" IS NOT NULL)
                                 OR 
                                 (p.""NombrePromotorExterno"" = b.""NombrePromotorExterno"" AND b.""NombrePromotorExterno"" IS NOT NULL)
                             )
                            ) as TotalPagado
                        FROM ""RifasPersonales_Boletos"" b
                        LEFT JOIN ""Sist_Usuarios"" u ON b.""IdUsuarioAsignado"" = u.""Id_Usuario""
                        WHERE b.""IdRifa"" = @id AND (b.""IdUsuarioAsignado"" IS NOT NULL OR b.""NombrePromotorExterno"" IS NOT NULL)
                        GROUP BY b.""IdRifa"", b.""IdUsuarioAsignado"", b.""NombrePromotorExterno"", u.""NombreCompleto""
                        ORDER BY Vendidos DESC";

                    using (var cmd = new NpgsqlCommand(sqlPromotores, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int? idAsignado = r["IdUsuarioAsignado"] != DBNull.Value ? (int)r["IdUsuarioAsignado"] : null;
                                string nombreExterno = r["NombrePromotorExterno"]?.ToString();
                                string nombre = r["NombreCompleto"]?.ToString();
                                int totalAsignados = Convert.ToInt32(r["TotalAsignados"]);
                                decimal totalPagado = Convert.ToDecimal(r["TotalPagado"]);

                                if (idAsignado == idUsuario)
                                {
                                    nombre = "Yo";
                                }
                                else if (idAsignado == null && !string.IsNullOrEmpty(nombreExterno))
                                {
                                    nombre = nombreExterno + " (Externo)";
                                }

                                // [DENTRO DE LA FUNCIÓN Asignaciones()]
                                var promotorExistente = modelo.Promotores.FirstOrDefault(p => p.Nombre == nombre);
                                if (promotorExistente != null)
                                {
                                    promotorExistente.Asignados += totalAsignados;
                                    promotorExistente.Vendidos += Convert.ToInt32(r["Vendidos"]);
                                    promotorExistente.Disponibles += Convert.ToInt32(r["Disponibles"]);
                                    promotorExistente.DeudaTotal += (Convert.ToInt32(r["Vendidos"]) * costoBoleto);
                                }
                                else
                                {
                                    modelo.Promotores.Add(new PromotorPersonalStat
                                    {
                                        IdUsuario = idAsignado,
                                        NombrePromotorExterno = nombreExterno,
                                        Nombre = nombre,
                                        Asignados = totalAsignados,
                                        Vendidos = Convert.ToInt32(r["Vendidos"]),
                                        Disponibles = Convert.ToInt32(r["Disponibles"]),
                                        DeudaTotal = (Convert.ToInt32(r["Vendidos"]) * costoBoleto),
                                        TotalPagado = totalPagado
                                    });
                                }
                            }
                        }
                    }

                    // CARGAR PAGOS PENDIENTES DE REVISIÓN
                    var pagosPendientes = new List<PagoVendedorViewModel>();
                    string sqlPendientes = @"
                        SELECT p.*, u.""NombreCompleto"" 
                        FROM ""RifasPersonales_Pagos"" p 
                        LEFT JOIN ""Sist_Usuarios"" u ON p.""IdUsuarioVendedor"" = u.""Id_Usuario""
                        WHERE p.""IdRifa"" = @id AND p.""Estado"" = 'Pendiente'";
                    using (var cmdP = new NpgsqlCommand(sqlPendientes, conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmdP.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                pagosPendientes.Add(new PagoVendedorViewModel
                                {
                                    IdPago = (int)r["IdPago"],
                                    IdUsuarioVendedor = r["IdUsuarioVendedor"] as int?,
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString() ?? r["NombreCompleto"]?.ToString(),
                                    Monto = (decimal)r["Monto"],
                                    ComprobanteUrl = r["ComprobanteUrl"]?.ToString(),
                                    FechaPago = (DateTime)r["FechaPago"]
                                });
                            }
                        }
                    }
                    ViewBag.PagosPendientes = pagosPendientes;

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Leer, $"Gestor Personal: Visualizó Asignaciones de Rifa {idRifa}", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error al cargar los promotores: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }
        // ========================================================================
        // 6.1. REGISTRAR ABONO MANUAL (LIQUIDACIÓN DIRECTA) - SOLO CREADOR
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarAbonoManual(string tokenRifa, int? IdUsuarioVendedor, string NombrePromotorExterno, decimal MontoAbono, string NotaAbono)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para registrar pagos.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0) return RedirectToAction("Index");

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            if (string.IsNullOrWhiteSpace(NombrePromotorExterno)) NombrePromotorExterno = null;

            try
            {
                if (MontoAbono <= 0) throw new Exception("El monto debe ser mayor a cero.");
                if (IdUsuarioVendedor == null && string.IsNullOrEmpty(NombrePromotorExterno)) throw new Exception("Vendedor no identificado.");

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);

                    // =======================================================
                    // VALIDACIÓN: QUE NO ABONE MÁS DE LO QUE DEBE
                    // =======================================================
                    string sqlValidacionDeuda = @"
                        SELECT 
                            ((SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @idr AND ""Estado"" = 'Vendido' AND ((""IdUsuarioAsignado"" = @idu AND @idu IS NOT NULL) OR (""IdUsuarioAsignado"" IS NULL AND ""NombrePromotorExterno"" = @nomExt))) 
                            * (SELECT ""CostoBoleto"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @idr))
                            - 
                            COALESCE((SELECT SUM(""Monto"") FROM ""RifasPersonales_Pagos"" WHERE ""IdRifa"" = @idr AND ""Estado"" IN ('Aprobado', 'Pendiente') AND ((""IdUsuarioVendedor"" = @idu AND @idu IS NOT NULL) OR (""IdUsuarioVendedor"" IS NULL AND ""NombrePromotorExterno"" = @nomExt))), 0) 
                        AS DeudaRestante";

                    using (var cmdVal = new NpgsqlCommand(sqlValidacionDeuda, conexion))
                    {
                        cmdVal.Parameters.AddWithValue("@idr", idRifa);
                        cmdVal.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                        cmdVal.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);

                        decimal deudaRestanteVal = Convert.ToDecimal(await cmdVal.ExecuteScalarAsync());

                        // Tolerancia de 1 centavo por redondeos de BD
                        if (MontoAbono > (deudaRestanteVal + 0.01m))
                        {
                            throw new Exception($"El monto a cobrar (${MontoAbono:N2}) supera el saldo pendiente real del vendedor (${deudaRestanteVal:N2}). Recuerda que los pagos en estado 'Pendiente' ya se consideran descontados temporalmente.");
                        }
                    }

                    if (!acceso.EsCreador) throw new Exception("Solo el administrador de la rifa puede registrar pagos manuales.");

                    string sqlInsert = @"INSERT INTO ""RifasPersonales_Pagos"" 
                                       (""IdRifa"", ""IdUsuarioVendedor"", ""NombrePromotorExterno"", ""Monto"", ""MetodoPago"", ""Estado"", ""Origen"", ""Nota"", ""FechaPago"") 
                                       VALUES (@idr, @idu, @nomExt, @monto, @metodo, 'Aprobado', 'Administrador', @nota, CURRENT_TIMESTAMP)";

                    using (var cmd = new NpgsqlCommand(sqlInsert, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idr", idRifa);
                        cmd.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@monto", MontoAbono);
                        cmd.Parameters.AddWithValue("@metodo", "Efectivo");
                        cmd.Parameters.AddWithValue("@nota", (object)NotaAbono ?? DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    string vendedorStr = IdUsuarioVendedor.HasValue ? $"Usuario {IdUsuarioVendedor}" : $"Externo {NombrePromotorExterno}";
                    await Funciones.RegistrarBitacora(conexion, idUsuarioActual, Modulo, Parametros.AccionesBitacora.Editar, $"Gestor Personal: Registró abono de ${MontoAbono} a {vendedorStr} en Rifa {idRifa}", ip);

                    MostrarMensaje("Pago Registrado", $"Se aplicó el abono de ${MontoAbono:N2} exitosamente al saldo del vendedor.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error al Cobrar", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Asignaciones", new { token = tokenRifa });
        }

        // ========================================================================
        // 6. ASIGNAR BOLETOS A VENDEDOR (POST)
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AsignarBoletos(string tokenRifa, int? IdUsuarioDestino, string NombrePromotorExterno, int CantidadBoletos)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para asignar boletos.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0)
            {
                MostrarMensaje("Enlace Inválido", "Token de rifa inválido.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                if (CantidadBoletos <= 0) throw new Exception("La cantidad a asignar debe ser mayor a cero.");

                // EXCLUSIVIDAD MUTUA Y LIMPIEZA DE STRINGS
                if (IdUsuarioDestino.HasValue && IdUsuarioDestino.Value > 0)
                {
                    // Si eligió usuario registrado, forzamos externo a NULL ignorando lo que traiga el POST
                    NombrePromotorExterno = null;
                }
                else if (!string.IsNullOrWhiteSpace(NombrePromotorExterno))
                {
                    // Si es externo, lo limpiamos de espacios basura y forzamos ID a NULL
                    NombrePromotorExterno = NombrePromotorExterno.Trim();
                    IdUsuarioDestino = null;
                    if (NombrePromotorExterno.Length > 100) throw new Exception("El nombre del promotor externo es demasiado largo.");
                }
                else
                {
                    throw new Exception("Debes seleccionar un usuario del sistema o escribir un nombre de promotor externo.");
                }

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);
                    if (!acceso.EsCreador) throw new Exception("Solo el administrador puede repartir planillas.");
                    if (acceso.Estado != "Activa") throw new Exception("No se pueden asignar boletos en una rifa inactiva.");

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        string sqlAsignar = @"
                    UPDATE ""RifasPersonales_Boletos""
                    SET ""IdUsuarioAsignado"" = @idDest, 
                        ""NombrePromotorExterno"" = @nomExt,
                        ""Estado"" = 'Asignado',
                        ""FechaAsignacion"" = CURRENT_TIMESTAMP
                    WHERE ""IdBoleto"" IN (
                        SELECT ""IdBoleto"" FROM ""RifasPersonales_Boletos""
                        WHERE ""IdRifa"" = @idRifa AND ""Estado"" IN ('Disponible', 'Asignado') 
                        AND ""IdUsuarioAsignado"" IS NULL AND ""NombrePromotorExterno"" IS NULL
                        ORDER BY ""Numero"" ASC
                        LIMIT @cantidad
                        FOR UPDATE SKIP LOCKED
                    )";

                        using (var cmd = new NpgsqlCommand(sqlAsignar, conexion, trans))
                        {
                            cmd.Parameters.AddWithValue("@idDest", (object)IdUsuarioDestino ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@idRifa", idRifa);
                            cmd.Parameters.AddWithValue("@cantidad", CantidadBoletos);

                            int afectados = await cmd.ExecuteNonQueryAsync();
                            if (afectados == 0) throw new Exception("No hay suficientes boletos libres en la base para asignar esa cantidad.");

                            if (afectados < CantidadBoletos)
                                MostrarMensaje("Aviso de Disponibilidad", $"Solo se pudieron asignar {afectados} boletos porque no había más disponibles en la base.", TipoMensaje.Info);
                            else
                                MostrarMensaje("Planilla Asignada", $"Se han asignado {afectados} boletos exitosamente.", TipoMensaje.Exito);
                        }

                        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                        string destStr = IdUsuarioDestino.HasValue ? $"User {IdUsuarioDestino}" : $"Externo '{NombrePromotorExterno}'";
                        await Funciones.RegistrarBitacora(conexion, idUsuarioActual, Modulo, Parametros.AccionesBitacora.Editar, $"Gestor Personal: Asignó {CantidadBoletos} boletos en Rifa {idRifa} a {destStr}", ip, trans);

                        await trans.CommitAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error en Asignación", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Asignaciones", new { token = tokenRifa });
        }

        // ========================================================================
        // 6.3 VINCULAR PROMOTOR EXTERNO A CUENTA REGISTRADA (POST) - BLINDADO
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VincularPromotorExterno(string tokenRifa, string NombrePromotorExterno, int IdUsuarioDestino)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para realizar esta acción.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0 || string.IsNullOrWhiteSpace(NombrePromotorExterno) || IdUsuarioDestino <= 0)
            {
                MostrarMensaje("Error", "Datos incompletos para realizar la vinculación.", TipoMensaje.Error);
                return RedirectToAction("Asignaciones", new { token = tokenRifa });
            }

            // 🛡️ PARCHE 3: Limpiar espacios que puedan dar falsos negativos
            NombrePromotorExterno = NombrePromotorExterno.Trim();
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);
                    if (!acceso.EsCreador) throw new Exception("Solo el administrador de la rifa puede vincular cuentas.");
                    if (acceso.Estado != "Activa") throw new Exception("No se pueden hacer cambios estructurales en una rifa inactiva.");

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        // 1. Transferir los Boletos
                        string sqlBol = @"UPDATE ""RifasPersonales_Boletos"" 
                                  SET ""IdUsuarioAsignado"" = @idDest, ""NombrePromotorExterno"" = NULL 
                                  WHERE ""IdRifa"" = @idRifa AND ""NombrePromotorExterno"" = @nomExt AND ""IdUsuarioAsignado"" IS NULL";

                        int boletosVinculados = 0;
                        using (var cmdBol = new NpgsqlCommand(sqlBol, conexion, trans))
                        {
                            cmdBol.Parameters.AddWithValue("@idDest", IdUsuarioDestino);
                            cmdBol.Parameters.AddWithValue("@idRifa", idRifa);
                            cmdBol.Parameters.AddWithValue("@nomExt", NombrePromotorExterno);
                            boletosVinculados = await cmdBol.ExecuteNonQueryAsync();
                        }

                        // 🛡️ PARCHE 2: Si no modificó nada, algo anda mal (el promotor no existe)
                        if (boletosVinculados == 0)
                        {
                            throw new Exception("No se encontraron boletos asociados al nombre de este promotor externo.");
                        }

                        // 2. Transferir los Pagos/Abonos registrados (Puede ser 0 si aún no ha hecho pagos, eso es normal)
                        string sqlPagos = @"UPDATE ""RifasPersonales_Pagos"" 
                                    SET ""IdUsuarioVendedor"" = @idDest, ""NombrePromotorExterno"" = NULL 
                                    WHERE ""IdRifa"" = @idRifa AND ""NombrePromotorExterno"" = @nomExt AND ""IdUsuarioVendedor"" IS NULL";
                        using (var cmdPagos = new NpgsqlCommand(sqlPagos, conexion, trans))
                        {
                            cmdPagos.Parameters.AddWithValue("@idDest", IdUsuarioDestino);
                            cmdPagos.Parameters.AddWithValue("@idRifa", idRifa);
                            cmdPagos.Parameters.AddWithValue("@nomExt", NombrePromotorExterno);
                            await cmdPagos.ExecuteNonQueryAsync();
                        }

                        string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                        await Funciones.RegistrarBitacora(conexion, idUsuarioActual, Modulo, Parametros.AccionesBitacora.Editar, $"Gestor Personal: Vinculó promotor externo '{NombrePromotorExterno}' ({boletosVinculados} boletos) al usuario ID {IdUsuarioDestino} en Rifa {idRifa}", ip, trans);

                        await trans.CommitAsync();
                        MostrarMensaje("Vinculación Exitosa", $"Se han transferido {boletosVinculados} boletos y sus pagos correspondientes de '{NombrePromotorExterno}' a la cuenta registrada.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Vinculación", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Asignaciones", new { token = tokenRifa });
        }

        [HttpGet]
        public async Task<IActionResult> ImprimirPlanilla(string tokenRifa, int? idDestino, string externo, string nombreMostrar)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de lectura.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0) return BadRequest("Token de rifa inválido.");

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            var boletos = new List<BoletoPersonalViewModel>();
            string tituloRifa = "";
            DateTime FechaSorteo;
            string imagenUrl = "";
            string descripcionRifa = "";
            string qrGrupoWhatsappUrl = ""; // Nueva variable para el QR
            decimal CostoBoleto = 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);
                    if (!acceso.Existe) return NotFound("La rifa no existe.");
                    FechaSorteo = acceso.fechasorteo;
                    tituloRifa = acceso.Titulo;
                    CostoBoleto = acceso.CostoXBoleto;
                    imagenUrl = ViewBag.ImagenUrl;

                    // Ajuste aquí: Traemos Descripcion y el QR usando ExecuteReaderAsync
                    using (var cmdInfo = new NpgsqlCommand(@"SELECT ""Descripcion"", ""QrGrupoWhatsappUrl"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id", conexion))
                    {
                        cmdInfo.Parameters.AddWithValue("@id", idRifa);
                        using (var reader = await cmdInfo.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                descripcionRifa = reader["Descripcion"]?.ToString();
                                qrGrupoWhatsappUrl = reader["QrGrupoWhatsappUrl"] != DBNull.Value ? reader["QrGrupoWhatsappUrl"].ToString() : null;
                            }
                        }
                    }

                    // Si no es el creador, bloqueamos cualquier intento de ver planillas ajenas
                    if (!acceso.EsCreador)
                    {
                        idDestino = null; // Forzamos a null para que no pueda fisgonear
                        externo = null;   // Forzamos a null para bloquear externos
                    }

                    // Consulta ajustada para TRAER LOS DATOS DEL COMPRADOR (Nombre, Teléfono, Comentarios)
                    string sql = @"
        SELECT ""IdBoleto"", ""IdRifa"", ""Numero"", ""Estado"", 
               ""NombrePromotorExterno"", ""NombreComprador"", 
               ""TelefonoComprador"", ""Comentarios"", ""FechaAsignacion""
        FROM ""RifasPersonales_Boletos""
        WHERE ""IdRifa"" = @idRifa ";

                    if (idDestino.HasValue)
                    {
                        sql += @" AND ""IdUsuarioAsignado"" = @idDestino";
                    }
                    else if (!string.IsNullOrEmpty(externo))
                    {
                        sql += @" AND ""NombrePromotorExterno"" = @externo AND ""IdUsuarioAsignado"" IS NULL";
                    }
                    else
                    {
                        sql += @" AND ""IdUsuarioAsignado"" = @idUsuarioActual";
                    }

                    sql += @" ORDER BY ""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idRifa", idRifa);
                        if (idDestino.HasValue) cmd.Parameters.AddWithValue("@idDestino", idDestino.Value);
                        if (!string.IsNullOrEmpty(externo)) cmd.Parameters.AddWithValue("@externo", externo);
                        cmd.Parameters.AddWithValue("@idUsuarioActual", idUsuarioActual);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                boletos.Add(new BoletoPersonalViewModel
                                {
                                    IdBoleto = (int)r["IdBoleto"],
                                    IdRifa = (int)r["IdRifa"],
                                    Numero = (int)r["Numero"],
                                    Estado = r["Estado"].ToString(),
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    NombreComprador = r["NombreComprador"]?.ToString(),
                                    TelefonoComprador = r["TelefonoComprador"]?.ToString(),
                                    Comentarios = r["Comentarios"]?.ToString(),
                                    FechaAsignacion = r["FechaAsignacion"] != DBNull.Value ? (DateTime)r["FechaAsignacion"] : (DateTime?)null
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Content("Error al generar la planilla: " + ex.Message);
            }

            if (!boletos.Any())
            {
                return Content("No hay boletos asignados para generar la planilla.");
            }

            // Lógica de agrupación (misma fecha y consecutivos)
            var gruposContinuos = new List<List<BoletoPersonalViewModel>>();
            var grupoActual = new List<BoletoPersonalViewModel> { boletos[0] };
            gruposContinuos.Add(grupoActual);

            for (int i = 1; i < boletos.Count; i++)
            {
                var actual = boletos[i];
                var anterior = boletos[i - 1];

                bool mismaFecha = actual.FechaAsignacion?.Date == anterior.FechaAsignacion?.Date;
                bool esConsecutivo = actual.Numero == anterior.Numero + 1;

                if (mismaFecha && esConsecutivo)
                {
                    grupoActual.Add(actual);
                }
                else
                {
                    grupoActual = new List<BoletoPersonalViewModel> { actual };
                    gruposContinuos.Add(grupoActual);
                }
            }

            ViewBag.TituloRifa = tituloRifa;
            ViewBag.NombreMostrar = string.IsNullOrEmpty(nombreMostrar) ? "Vendedor" : nombreMostrar;
            ViewBag.ImagenUrl = imagenUrl;
            ViewBag.DescripcionRifa = descripcionRifa;
            ViewBag.TokenRifaStr = tokenRifa;
            ViewBag.IdDestino = idDestino;
            ViewBag.Externo = externo;
            ViewBag.CostoBoleto = CostoBoleto;
            ViewBag.FechaSorteo = FechaSorteo.ToString("dd/MMMM/yyyy", new System.Globalization.CultureInfo("es-ES"));
            ViewBag.QrGrupoWhatsappUrl = qrGrupoWhatsappUrl; // Asignamos el QR al ViewBag

            return View(gruposContinuos);
        }
        // ========================================================================
        // 6.2 IMPRIMIR PLANILLA (FORMATO LISTA)
        // ========================================================================
        [HttpGet]
        public async Task<IActionResult> ImprimirPlanillaLista(string tokenRifa, int? idDestino, string externo, string nombreMostrar)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer)) return RedirectToAction("Index", "Home");

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0) return BadRequest("Token de rifa inválido.");

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            var boletos = new List<BoletoPersonalViewModel>();
            string tituloRifa = "", imagenUrl = "", descripcionRifa = "";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);
                    if (!acceso.Existe) return NotFound("La rifa no existe.");

                    tituloRifa = acceso.Titulo;
                    imagenUrl = ViewBag.ImagenUrl;

                    using (var cmdDesc = new NpgsqlCommand(@"SELECT ""Descripcion"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id", conexion))
                    {
                        cmdDesc.Parameters.AddWithValue("@id", idRifa);
                        var desc = await cmdDesc.ExecuteScalarAsync();
                        descripcionRifa = desc?.ToString();
                    }

                    // Si no es el creador, bloqueamos cualquier intento de ver planillas ajenas
                    if (!acceso.EsCreador)
                    {
                        idDestino = null; // Forzamos a null para que no pueda fisgonear
                        externo = null;   // Forzamos a null para bloquear externos
                    }

                    // Consulta ajustada para TRAER LOS DATOS DEL COMPRADOR (Nombre, Teléfono, Comentarios)
                    string sql = @"
                SELECT ""IdBoleto"", ""IdRifa"", ""Numero"", ""Estado"", 
                       ""NombrePromotorExterno"", ""NombreComprador"", 
                       ""TelefonoComprador"", ""Comentarios"", ""FechaAsignacion""
                FROM ""RifasPersonales_Boletos""
                WHERE ""IdRifa"" = @idRifa ";

                    if (idDestino.HasValue)
                    {
                        sql += @" AND ""IdUsuarioAsignado"" = @idDestino";
                    }
                    else if (!string.IsNullOrEmpty(externo))
                    {
                        sql += @" AND ""NombrePromotorExterno"" = @externo AND ""IdUsuarioAsignado"" IS NULL";
                    }
                    else
                    {
                        sql += @" AND ""IdUsuarioAsignado"" = @idUsuarioActual";
                    }

                    sql += @" ORDER BY ""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idRifa", idRifa);
                        if (idDestino.HasValue) cmd.Parameters.AddWithValue("@idDestino", idDestino.Value);
                        if (!string.IsNullOrEmpty(externo)) cmd.Parameters.AddWithValue("@externo", externo);
                        cmd.Parameters.AddWithValue("@idUsuarioActual", idUsuarioActual);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                boletos.Add(new BoletoPersonalViewModel
                                {
                                    IdBoleto = (int)r["IdBoleto"],
                                    IdRifa = (int)r["IdRifa"],
                                    Numero = (int)r["Numero"],
                                    Estado = r["Estado"].ToString(),
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    NombreComprador = r["NombreComprador"]?.ToString(),
                                    TelefonoComprador = r["TelefonoComprador"]?.ToString(),
                                    Comentarios = r["Comentarios"]?.ToString(),
                                    FechaAsignacion = r["FechaAsignacion"] != DBNull.Value ? (DateTime)r["FechaAsignacion"] : (DateTime?)null
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { return Content("Error: " + ex.Message); }

            if (!boletos.Any()) return Content("No hay boletos asignados.");

            // Lógica de agrupación
            var gruposContinuos = new List<List<BoletoPersonalViewModel>>();
            var grupoActual = new List<BoletoPersonalViewModel> { boletos[0] };
            gruposContinuos.Add(grupoActual);

            for (int i = 1; i < boletos.Count; i++)
            {
                var actual = boletos[i];
                var anterior = boletos[i - 1];

                bool mismaFecha = actual.FechaAsignacion?.Date == anterior.FechaAsignacion?.Date;
                bool esConsecutivo = actual.Numero == anterior.Numero + 1;

                if (mismaFecha && esConsecutivo) grupoActual.Add(actual);
                else { grupoActual = new List<BoletoPersonalViewModel> { actual }; gruposContinuos.Add(grupoActual); }
            }

            ViewBag.TituloRifa = tituloRifa;
            ViewBag.NombreMostrar = string.IsNullOrEmpty(nombreMostrar) ? "Vendedor" : nombreMostrar;
            ViewBag.ImagenUrl = imagenUrl;
            ViewBag.DescripcionRifa = descripcionRifa;

            // Variables para el botón de regreso
            ViewBag.TokenRifaStr = tokenRifa;
            ViewBag.IdDestino = idDestino;
            ViewBag.Externo = externo;

            return View(gruposContinuos);
        }

        public async Task<IActionResult> PuntoDeVenta(string token)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de lectura para ver boletos.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(token);
            if (idRifa <= 0)
            {
                MostrarMensaje("Enlace Inválido", "Identificador de rifa corrupto.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new TableroRifaPersonalViewModel { Boletos = new List<BoletoPersonalViewModel>() };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.Existe)
                    {
                        MostrarMensaje("No Encontrada", "La rifa seleccionada no existe.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }
                    if (!acceso.EsCreador && !acceso.EsVendedor)
                    {
                        MostrarMensaje("Acceso Denegado", "No tienes boletos asignados en esta rifa.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    modelo.InfoRifa = new RifaPersonalViewModel { IdRifa = idRifa, Titulo = acceso.Titulo, Estado = acceso.Estado };

                    // Creador -> Ve sus boletos Y los de sus promotores externos (NULL pero con Nombre). NADIE ve los huérfanos puros.
                    // Promotor -> Ve SOLO sus boletos
                    string condicionFiltro = acceso.EsCreador
                        ? @"(b.""IdUsuarioAsignado"" = @uid OR (b.""IdUsuarioAsignado"" IS NULL AND b.""NombrePromotorExterno"" IS NOT NULL))"
                        : @"b.""IdUsuarioAsignado"" = @uid";

                    string sqlBols = $@"
                    SELECT b.*, u.""NombreCompleto"" as Promotor, b.""FechaAsignacion"" 
                    FROM ""RifasPersonales_Boletos"" b
                    LEFT JOIN ""Sist_Usuarios"" u ON b.""IdUsuarioAsignado"" = u.""Id_Usuario""
                    WHERE b.""IdRifa"" = @id AND {condicionFiltro}
                    ORDER BY b.""FechaAsignacion"" DESC, b.""Numero"" ASC";

                    using (var cmd = new NpgsqlCommand(sqlBols, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        cmd.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                // Extraemos el MarcadorPersonal
                                bool marcador = r.GetColumnSchema().Any(c => c.ColumnName == "MarcadorPersonal")
                                                && r["MarcadorPersonal"] != DBNull.Value
                                                && (bool)r["MarcadorPersonal"];

                                modelo.Boletos.Add(new BoletoPersonalViewModel
                                {
                                    IdBoleto = (int)r["IdBoleto"],
                                    IdRifa = (int)r["IdRifa"],
                                    Numero = (int)r["Numero"],
                                    Estado = r["Estado"].ToString(),
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    NombreComprador = r["NombreComprador"]?.ToString(),
                                    TelefonoComprador = r["TelefonoComprador"]?.ToString(),
                                    Comentarios = r["Comentarios"]?.ToString(),
                                    FechaAsignacion = r["FechaAsignacion"] != DBNull.Value ? (DateTime)r["FechaAsignacion"] : (DateTime?)null,
                                    MarcadorPersonal = marcador // Asignamos la variable
                                });
                            }
                        }
                    }

                    // 1. CARGAR DATOS BANCARIOS, COSTO, FECHA Y WHATSAPP 
                    string sqlRifaInfo = @"SELECT ""CostoBoleto"", ""Banco"", ""CuentaClabe"", ""NumeroCuenta"", ""NumeroTarjeta"", ""TitularCuenta"", ""EnlaceGrupoWhatsapp"", ""QrGrupoWhatsappUrl"", ""FechaSorteo"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id";
                    using (var cmdInfo = new NpgsqlCommand(sqlRifaInfo, conexion))
                    {
                        cmdInfo.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmdInfo.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.InfoRifa.CostoBoleto = Convert.ToDecimal(r["CostoBoleto"]);
                                modelo.InfoRifa.Banco = r["Banco"]?.ToString();
                                modelo.InfoRifa.CuentaClabe = r["CuentaClabe"]?.ToString();
                                modelo.InfoRifa.NumeroCuenta = r["NumeroCuenta"]?.ToString();
                                modelo.InfoRifa.NumeroTarjeta = r["NumeroTarjeta"]?.ToString();
                                modelo.InfoRifa.TitularCuenta = r["TitularCuenta"]?.ToString();
                                modelo.InfoRifa.EnlaceGrupoWhatsapp = r["EnlaceGrupoWhatsapp"]?.ToString();
                                modelo.InfoRifa.QrGrupoWhatsappUrl = r["QrGrupoWhatsappUrl"]?.ToString();
                                modelo.InfoRifa.FechaSorteo = r["FechaSorteo"] != DBNull.Value ? Convert.ToDateTime(r["FechaSorteo"]) : DateTime.MinValue;
                            }
                        }
                    }

                    // 2. CARGAR FINANZAS POR GRUPO PARA ESTA PANTALLA
                    string condicionFiltroFinanzas = acceso.EsCreador
                        ? @"(b.""IdUsuarioAsignado"" = @uid OR (b.""IdUsuarioAsignado"" IS NULL AND b.""NombrePromotorExterno"" IS NOT NULL))"
                        : @"(b.""IdUsuarioAsignado"" = @uid)";

                    string sqlFinanzas = $@"
                        SELECT 
                            b.""IdUsuarioAsignado"", b.""NombrePromotorExterno"",
                            SUM(CASE WHEN b.""Estado"" = 'Vendido' THEN 1 ELSE 0 END) as Vendidos,
                            (SELECT COALESCE(SUM(p.""Monto""), 0) FROM ""RifasPersonales_Pagos"" p 
                             WHERE p.""IdRifa"" = b.""IdRifa"" AND p.""Estado"" = 'Aprobado'
                             AND ((p.""IdUsuarioVendedor"" = b.""IdUsuarioAsignado"" AND b.""IdUsuarioAsignado"" IS NOT NULL) OR (p.""NombrePromotorExterno"" = b.""NombrePromotorExterno"" AND b.""NombrePromotorExterno"" IS NOT NULL))
                            ) as TotalPagado
                        FROM ""RifasPersonales_Boletos"" b
                        WHERE b.""IdRifa"" = @id AND {condicionFiltroFinanzas}
                        GROUP BY b.""IdRifa"", b.""IdUsuarioAsignado"", b.""NombrePromotorExterno""";

                    using (var cmdFin = new NpgsqlCommand(sqlFinanzas, conexion))
                    {
                        cmdFin.Parameters.AddWithValue("@id", idRifa);
                        cmdFin.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmdFin.ExecuteReaderAsync())
                        {
                            string miNombre = User.FindFirst("NombreCompleto")?.Value ?? User.Identity?.Name ?? "Mi Cuenta";
                            while (await r.ReadAsync())
                            {
                                int? idAsignado = r["IdUsuarioAsignado"] != DBNull.Value ? (int)r["IdUsuarioAsignado"] : null;
                                string nombreExterno = r["NombrePromotorExterno"]?.ToString();
                                string nombreKey = (idAsignado == idUsuario) ? miNombre : nombreExterno;

                                modelo.Promotores.Add(new PromotorPersonalStat
                                {
                                    IdUsuario = idAsignado,
                                    NombrePromotorExterno = nombreExterno,
                                    Nombre = nombreKey,
                                    Vendidos = Convert.ToInt32(r["Vendidos"]),
                                    DeudaTotal = (Convert.ToInt32(r["Vendidos"]) * modelo.InfoRifa.CostoBoleto),
                                    TotalPagado = Convert.ToDecimal(r["TotalPagado"])
                                });
                            }
                        }
                    }

                    // CARGAR PAGOS RECHAZADOS
                    var pagosRechazados = new List<PagoVendedorViewModel>();
                    string condicionRechazados = acceso.EsCreador
                        ? @"(""IdUsuarioVendedor"" = @uid OR ""IdUsuarioVendedor"" IS NULL)"
                        : @"(""IdUsuarioVendedor"" = @uid)";

                    string sqlRech = $@"
                        SELECT ""IdPago"", ""IdUsuarioVendedor"", ""NombrePromotorExterno"", ""Monto"", ""ComprobanteUrl"", ""Nota"", ""FechaPago""
                        FROM ""RifasPersonales_Pagos""
                        WHERE ""IdRifa"" = @id AND ""Estado"" = 'Rechazado' AND {condicionRechazados}";

                    using (var cmdRech = new NpgsqlCommand(sqlRech, conexion))
                    {
                        cmdRech.Parameters.AddWithValue("@id", idRifa); 
                        cmdRech.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmdRech.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                pagosRechazados.Add(new PagoVendedorViewModel
                                {
                                    IdPago = (int)r["IdPago"],
                                    IdUsuarioVendedor = r["IdUsuarioVendedor"] as int?,
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    Monto = (decimal)r["Monto"],
                                    ComprobanteUrl = r["ComprobanteUrl"]?.ToString(),
                                    Nota = r["Nota"]?.ToString(),
                                    FechaPago = (DateTime)r["FechaPago"]
                                });
                            }
                        }
                    }
                    ViewBag.PagosRechazados = pagosRechazados;

                    // CARGAR PAGOS PENDIENTES PARA ESTE USUARIO/VENDEDOR
                    var pagosPendientes = new List<PagoVendedorViewModel>();
                    string sqlPendientes = $@"
                    SELECT ""IdPago"", ""IdUsuarioVendedor"", ""NombrePromotorExterno"", ""Monto"", ""ComprobanteUrl"", ""FechaPago""
                    FROM ""RifasPersonales_Pagos""
                    WHERE ""IdRifa"" = @id AND ""Estado"" = 'Pendiente' AND {condicionRechazados}";

                    using (var cmdPend = new NpgsqlCommand(sqlPendientes, conexion))
                    {
                        cmdPend.Parameters.AddWithValue("@id", idRifa); 
                        cmdPend.Parameters.AddWithValue("@uid", idUsuario);
                        using (var r = await cmdPend.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                pagosPendientes.Add(new PagoVendedorViewModel
                                {
                                    IdPago = (int)r["IdPago"],
                                    IdUsuarioVendedor = r["IdUsuarioVendedor"] as int?,
                                    NombrePromotorExterno = r["NombrePromotorExterno"]?.ToString(),
                                    Monto = (decimal)r["Monto"],
                                    ComprobanteUrl = r["ComprobanteUrl"]?.ToString(),
                                    FechaPago = (DateTime)r["FechaPago"]
                                });
                            }
                        }
                    }
                    ViewBag.PagosPendientes = pagosPendientes;

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Leer, $"Gestor Personal: Accedió al Punto de Venta de Rifa {idRifa}", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Error al cargar los boletos: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarPagoRechazado(string tokenRifa, int IdPago)
        {
            // CAMBIO: Ahora solo pide permiso de Leer
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer)) return RedirectToAction("Index", "Home");

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0) return RedirectToAction("Index");

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);

                    // CAMBIO: Validamos rol contextual
                    if (!acceso.EsCreador && !acceso.EsVendedor) throw new Exception("No tienes acceso a esta rifa.");

                    string comprobanteUrl = null;

                    string condicionVendedor = acceso.EsCreador ? "" : @" AND ""IdUsuarioVendedor"" = @uid";

                    string sqlSel = $@"SELECT ""ComprobanteUrl"" FROM ""RifasPersonales_Pagos"" 
                                      WHERE ""IdPago"" = @idp AND ""IdRifa"" = @idr AND ""Estado"" = 'Rechazado'{condicionVendedor}";

                    using (var cmdSel = new NpgsqlCommand(sqlSel, conexion))
                    {
                        cmdSel.Parameters.AddWithValue("@idp", IdPago);
                        cmdSel.Parameters.AddWithValue("@idr", idRifa);
                        if (!acceso.EsCreador) cmdSel.Parameters.AddWithValue("@uid", idUsuarioActual);

                        var objUrl = await cmdSel.ExecuteScalarAsync();
                        if (objUrl != null && objUrl != DBNull.Value)
                        {
                            comprobanteUrl = objUrl.ToString();
                        }
                        else
                        {
                            throw new Exception("El comprobante no existe, no tienes permisos para borrarlo o no está en estado Rechazado.");
                        }
                    }

                    string sqlDel = $@"DELETE FROM ""RifasPersonales_Pagos"" 
                                      WHERE ""IdPago"" = @idp AND ""IdRifa"" = @idr AND ""Estado"" = 'Rechazado'{condicionVendedor}";
                    using (var cmd = new NpgsqlCommand(sqlDel, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idp", IdPago);
                        cmd.Parameters.AddWithValue("@idr", idRifa);
                        if (!acceso.EsCreador) cmd.Parameters.AddWithValue("@uid", idUsuarioActual);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    if (!string.IsNullOrEmpty(comprobanteUrl))
                    {
                        await DestruirImagenCloudinary(comprobanteUrl);
                    }

                    MostrarMensaje("Registro Eliminado", "El intento de pago rechazado ha sido borrado de tu historial y el archivo fue eliminado.", TipoMensaje.Info);
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("PuntoDeVenta", new { token = tokenRifa });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReportarPagoComprobante(string tokenRifa, int? IdUsuarioVendedor, string NombrePromotorExterno, decimal MontoPago, IFormFile comprobanteArchivo, int? IdPagoPrevio)
        {
            // CAMBIO: Agregamos protección base de lectura
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer)) return RedirectToAction("Index", "Home");

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            if (idRifa <= 0) return RedirectToAction("Index");

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                if (MontoPago <= 0) throw new Exception("El monto debe ser mayor a cero.");
                if (comprobanteArchivo == null) throw new Exception("Debes adjuntar la imagen de tu comprobante de transferencia.");

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);

                    // CAMBIO: Validamos rol contextual
                    if (!acceso.EsCreador && !acceso.EsVendedor) throw new Exception("No tienes acceso a esta rifa.");

                    if (!acceso.EsCreador)
                    {
                        IdUsuarioVendedor = idUsuarioActual;
                        NombrePromotorExterno = null;
                    }

                    string sqlValidacionDeuda = @"
                        SELECT 
                            ((SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @idr AND ""Estado"" = 'Vendido' AND ((""IdUsuarioAsignado"" = @idu AND @idu IS NOT NULL) OR (""IdUsuarioAsignado"" IS NULL AND ""NombrePromotorExterno"" = @nomExt))) 
                            * (SELECT ""CostoBoleto"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @idr))
                            - 
                            COALESCE((SELECT SUM(""Monto"") FROM ""RifasPersonales_Pagos"" WHERE ""IdRifa"" = @idr AND ""Estado"" IN ('Aprobado', 'Pendiente') AND ""IdPago"" != COALESCE(@idpPrevio, 0) AND ((""IdUsuarioVendedor"" = @idu AND @idu IS NOT NULL) OR (""IdUsuarioVendedor"" IS NULL AND ""NombrePromotorExterno"" = @nomExt))), 0) 
                        AS DeudaRestante";

                    using (var cmdVal = new NpgsqlCommand(sqlValidacionDeuda, conexion))
                    {
                        cmdVal.Parameters.AddWithValue("@idr", idRifa);
                        cmdVal.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                        cmdVal.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);
                        cmdVal.Parameters.AddWithValue("@idpPrevio", (object)IdPagoPrevio ?? DBNull.Value);

                        decimal deudaRestanteVal = Convert.ToDecimal(await cmdVal.ExecuteScalarAsync());

                        if (MontoPago > (deudaRestanteVal + 0.01m))
                        {
                            throw new Exception($"El monto ingresado (${MontoPago:N2}) supera tu saldo pendiente real (${deudaRestanteVal:N2}). Recuerda que los pagos 'Pendientes' de revisión ya se están descontando.");
                        }
                    }

                    string imgUrl = await SubirImagenCloudinary(comprobanteArchivo, $"{sAmbiente}/RifasPersonales/Pagos/{idUsuarioActual}");

                    try
                    {
                        if (IdPagoPrevio.HasValue && IdPagoPrevio.Value > 0)
                        {
                            string condicionPrevio = acceso.EsCreador ? "" : @" AND ""IdUsuarioVendedor"" = @uid";
                            string oldUrl = null;

                            using (var cmdSel = new NpgsqlCommand($@"SELECT ""ComprobanteUrl"" FROM ""RifasPersonales_Pagos"" WHERE ""IdPago"" = @idp AND ""IdRifa"" = @idr{condicionPrevio}", conexion))
                            {
                                cmdSel.Parameters.AddWithValue("@idp", IdPagoPrevio.Value);
                                cmdSel.Parameters.AddWithValue("@idr", idRifa);
                                if (!acceso.EsCreador) cmdSel.Parameters.AddWithValue("@uid", idUsuarioActual);

                                var objUrl = await cmdSel.ExecuteScalarAsync();
                                if (objUrl == null)
                                {
                                    throw new Exception("El registro previo no existe o no tienes permisos sobre él.");
                                }
                                if (objUrl != DBNull.Value) oldUrl = objUrl.ToString();
                            }

                            string sqlUpdate = @"UPDATE ""RifasPersonales_Pagos"" 
                                               SET ""Monto"" = @monto, ""ComprobanteUrl"" = @img, ""Estado"" = 'Pendiente', ""Nota"" = NULL, ""FechaPago"" = CURRENT_TIMESTAMP 
                                               WHERE ""IdPago"" = @idp";
                            using (var cmd = new NpgsqlCommand(sqlUpdate, conexion))
                            {
                                cmd.Parameters.AddWithValue("@monto", MontoPago);
                                cmd.Parameters.AddWithValue("@img", imgUrl);
                                cmd.Parameters.AddWithValue("@idp", IdPagoPrevio.Value);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            if (!string.IsNullOrEmpty(oldUrl))
                            {
                                await DestruirImagenCloudinary(oldUrl);
                            }
                        }
                        else
                        {
                            string sqlInsert = @"INSERT INTO ""RifasPersonales_Pagos"" 
                                               (""IdRifa"", ""IdUsuarioVendedor"", ""NombrePromotorExterno"", ""Monto"", ""MetodoPago"", ""ComprobanteUrl"", ""Estado"", ""Origen"", ""FechaPago"") 
                                               VALUES (@idr, @idu, @nomExt, @monto, 'Transferencia', @img, 'Pendiente', 'Vendedor', CURRENT_TIMESTAMP)";

                            using (var cmd = new NpgsqlCommand(sqlInsert, conexion))
                            {
                                cmd.Parameters.AddWithValue("@idr", idRifa);
                                cmd.Parameters.AddWithValue("@idu", (object)IdUsuarioVendedor ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@nomExt", (object)NombrePromotorExterno ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@monto", MontoPago);
                                cmd.Parameters.AddWithValue("@img", imgUrl);
                                await cmd.ExecuteNonQueryAsync();
                            }
                        }
                    }
                    catch (Exception dbEx)
                    {
                        await DestruirImagenCloudinary(imgUrl);
                        throw new Exception("Error al guardar en la base de datos. Se canceló la subida del archivo.", dbEx);
                    }

                    MostrarMensaje("Comprobante Enviado", "Tu pago ha sido reportado y está en espera de que el administrador lo valide.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }
            return RedirectToAction("PuntoDeVenta", new { token = tokenRifa });
        }

        // ========================================================================
        // VALIDAR PAGO PENDIENTE (ADMINISTRADOR)
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidarPagoTransferencia(string tokenRifa, int IdPago, string AccionValidacion, string NotaRechazo)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar)) return RedirectToAction("Index", "Home");

            int idRifa = Funciones.DesencriptarId(tokenRifa); int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value); // Obtenemos el usuario

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Solo el administrador de la rifa puede aprobar o rechazar pagos
                    var acceso = await ValidarAccesoRifa(idRifa, idUsuarioActual, conexion);
                    if (!acceso.EsCreador)
                    {
                        throw new Exception("No tienes permisos de administrador para validar pagos.");
                    }

                    string nuevoEstado = AccionValidacion == "Aprobar" ? "Aprobado" : "Rechazado";

                    string sqlUpdate = @"UPDATE ""RifasPersonales_Pagos"" SET ""Estado"" = @est, ""Nota"" = @nota WHERE ""IdPago"" = @idp AND ""IdRifa"" = @idr";
                    using (var cmd = new NpgsqlCommand(sqlUpdate, conexion))
                    {
                        cmd.Parameters.AddWithValue("@est", nuevoEstado);
                        cmd.Parameters.AddWithValue("@nota", (object)NotaRechazo ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@idp", IdPago);
                        cmd.Parameters.AddWithValue("@idr", idRifa);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    MostrarMensaje("Actualizado", $"El pago fue marcado como {nuevoEstado}.", TipoMensaje.Exito);
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Asignaciones", new { token = tokenRifa });
        }

        // ========================================================================
        // HELPER: LIMPIAR Y VALIDAR TELÉFONO (MÉXICO)
        // ========================================================================
        private string LimpiarYValidarTelefono(string telefonoEntrada)
        {
            // 1. Si está totalmente vacío, es válido (el campo es opcional)
            if (string.IsNullOrWhiteSpace(telefonoEntrada))
                return null;

            // 2. Limpiamos dejando solo números y el signo '+'
            var caracteresValidos = telefonoEntrada.Where(c => char.IsDigit(c) || c == '+').ToArray();
            string telLimpio = new string(caracteresValidos);

            // 3. Si después de limpiar quedó vacío (ej. escribieron puras letras) o es muy corto: ERROR
            if (string.IsNullOrWhiteSpace(telLimpio) || telLimpio.Length < 10)
                throw new Exception($"El teléfono ingresado ('{telefonoEntrada}') no es válido. Debe contener al menos 10 dígitos.");

            // 4. Evaluamos los formatos correctos (asumiendo México +52)
            if (telLimpio.StartsWith("+52") && telLimpio.Length == 13)
            {
                return telLimpio; // Formato perfecto: +52 y 10 dígitos
            }

            if (telLimpio.StartsWith("52") && telLimpio.Length == 12)
            {
                return "+" + telLimpio; // Le faltaba el '+', se lo ponemos
            }

            if (telLimpio.Length == 10 && !telLimpio.StartsWith("+"))
            {
                return "+52" + telLimpio; // Solo metieron los 10 dígitos, agregamos la lada
            }

            // 5. Si llega a este punto, significa que sobraron dígitos o tiene un formato no reconocido
            throw new Exception($"El teléfono '{telefonoEntrada}' no tiene un formato válido. Ingresa solo los 10 dígitos de tu número.");
        }

        // ========================================================================
        // 9. RESULTADOS Y NOTIFICACIONES (GET)
        // ========================================================================
        public async Task<IActionResult> Resultados(string token)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de lectura.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(token);
            if (idRifa <= 0)
            {
                MostrarMensaje("Enlace Inválido", "Identificador de rifa inválido.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new TableroRifaPersonalViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.Existe)
                    {
                        MostrarMensaje("No Encontrada", "La rifa no existe.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }
                    if (!acceso.EsCreador && !acceso.EsVendedor)
                    {
                        MostrarMensaje("Acceso Denegado", "No tienes acceso a esta rifa.", TipoMensaje.Error);
                        return RedirectToAction("Index");
                    }

                    modelo.InfoRifa = new RifaPersonalViewModel
                    {
                        IdRifa = idRifa,
                        Titulo = acceso.Titulo,
                        Estado = acceso.Estado
                    };

                    ViewBag.EsCreador = acceso.EsCreador;

                    string sqlInfo = @"
    SELECT ""FechaSorteo"", ""EnlaceGrupoWhatsapp"", ""QrGrupoWhatsappUrl"" 
    FROM ""RifasPersonales_Rifas"" 
    WHERE ""IdRifa"" = @idRifa";

                    using (var cmdInfo = new NpgsqlCommand(sqlInfo, conexion))
                    {
                        cmdInfo.Parameters.AddWithValue("@idRifa", idRifa);
                        using (var reader = await cmdInfo.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                modelo.InfoRifa.FechaSorteo = Convert.ToDateTime(reader["FechaSorteo"]);
                                modelo.InfoRifa.EnlaceGrupoWhatsapp = reader["EnlaceGrupoWhatsapp"] != DBNull.Value ? reader["EnlaceGrupoWhatsapp"].ToString() : null;
                                modelo.InfoRifa.QrGrupoWhatsappUrl = reader["QrGrupoWhatsappUrl"] != DBNull.Value ? reader["QrGrupoWhatsappUrl"].ToString() : null;
                            }
                        }
                    }

                    if (modelo.InfoRifa.Estado == "Finalizada")
                    {
                        // Traemos teléfono, notas y hacemos JOIN con Sist_Usuarios para saber el vendedor
                        string sqlGanador = @"
                            SELECT 
                                b.""Numero"", 
                                b.""NombreComprador"", 
                                b.""TelefonoComprador"", 
                                b.""Comentarios"",
                                b.""NombrePromotorExterno"", 
                                u.""NombreCompleto"" as NombreVendedor
                            FROM ""RifasPersonales_Rifas"" r
                            JOIN ""RifasPersonales_Boletos"" b ON r.""IdBoletoGanador"" = b.""IdBoleto""
                            LEFT JOIN ""Sist_Usuarios"" u ON b.""IdUsuarioAsignado"" = u.""Id_Usuario""
                            WHERE r.""IdRifa"" = @id";

                        using (var cmdGanador = new NpgsqlCommand(sqlGanador, conexion))
                        {
                            cmdGanador.Parameters.AddWithValue("@id", idRifa);
                            using (var r = await cmdGanador.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.BoletoGanadorTexto = ((int)r["Numero"]).ToString("000");
                                    modelo.NombreGanador = r["NombreComprador"]?.ToString() ?? "Anónimo";

                                    // Lógica para determinar el nombre exacto del Vendedor
                                    string vendedorInterno = r["NombreVendedor"]?.ToString();
                                    string vendedorExterno = r["NombrePromotorExterno"]?.ToString();

                                    ViewBag.VendedorGanador = !string.IsNullOrEmpty(vendedorInterno)
                                        ? vendedorInterno
                                        : (!string.IsNullOrEmpty(vendedorExterno) ? vendedorExterno + " (Externo)" : "Administrador / Sin Asignar");

                                    // Datos de contacto extra
                                    ViewBag.TelefonoGanador = r["TelefonoComprador"]?.ToString();
                                    ViewBag.ComentariosGanador = r["Comentarios"]?.ToString();
                                }
                            }
                        }
                    }

                    if (acceso.EsCreador)
                    {
                        // 1. Contar contactos totales con WhatsApp
                        var cmdTel = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa""=@id AND ""Estado""='Vendido' AND ""TelefonoComprador"" IS NOT NULL AND ""TelefonoComprador"" <> ''", conexion);
                        cmdTel.Parameters.AddWithValue("@id", idRifa);
                        modelo.ContactosConWhatsapp = Convert.ToInt32(await cmdTel.ExecuteScalarAsync());

                        // 2. Contar a cuántos ya se les envió notificación exitosa
                        var cmdEnviados = new NpgsqlCommand(@"SELECT COUNT(DISTINCT ""IdBoleto"") FROM ""RifasPersonales_Notificaciones"" WHERE ""IdRifa""=@id AND ""EstadoEnvio""='Enviado'", conexion);
                        cmdEnviados.Parameters.AddWithValue("@id", idRifa);
                        int yaEnviados = Convert.ToInt32(await cmdEnviados.ExecuteScalarAsync());

                        // 3. Bandera para ocultar el botón si ya todos fueron notificados
                        ViewBag.TodosNotificados = (modelo.ContactosConWhatsapp > 0 && yaEnviados >= modelo.ContactosConWhatsapp);
                    }

                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Leer, $"Gestor Personal: Visualizó Resultados de Rifa {idRifa}", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudieron cargar los resultados: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        // ========================================================================
        // 10. ENVIAR NOTIFICACIONES MASIVAS (TWILIO) - SOLO CREADOR
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnviarNotificacionesMasivas(string tokenRifa)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para enviar notificaciones.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.EsCreador) throw new Exception("Solo el administrador puede enviar notificaciones masivas.");
                    if (acceso.Estado != "Finalizada") throw new Exception("La rifa debe estar finalizada para poder notificar a los participantes.");

                    if (string.IsNullOrEmpty(_twilioAccountSid)) throw new Exception("El servicio de mensajería (Twilio) no está configurado en el sistema.");

                    // 1. Obtener ID, Nombre del Ganador y la URL de la Imagen en una sola consulta
                    int? idBoletoGanador = null;
                    string nombreGanador = "Anónimo";
                    string urlImagenRifa = ""; // Nueva variable para la foto

                    // Asumo que tu columna de imagen se llama "UrlImagen" en la tabla RifasPersonales_Rifas
                    string sqlGanadorInfo = @"SELECT r.""IdBoletoGanador"", b.""NombreComprador"", r.""ImagenUrl"" 
                                      FROM ""RifasPersonales_Rifas"" r 
                                      LEFT JOIN ""RifasPersonales_Boletos"" b ON r.""IdBoletoGanador"" = b.""IdBoleto"" 
                                      WHERE r.""IdRifa"" = @id";

                    using (var cmd = new NpgsqlCommand(sqlGanadorInfo, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                if (r["IdBoletoGanador"] != DBNull.Value)
                                {
                                    idBoletoGanador = (int)r["IdBoletoGanador"];
                                    nombreGanador = r["NombreComprador"]?.ToString() ?? "Anónimo";
                                }

                                // Capturamos la imagen de Cloudinary guardada en tu BD
                                urlImagenRifa = r["ImagenUrl"]?.ToString() ?? "";
                            }
                        }
                    }

                    var envios = new List<NotificacionPersonalViewModel>();
                    string sqlBols = @"SELECT ""IdBoleto"", ""TelefonoComprador"", ""NombreComprador"" 
                               FROM ""RifasPersonales_Boletos"" 
                               WHERE ""IdRifa""=@id AND ""Estado""='Vendido' AND ""TelefonoComprador"" IS NOT NULL AND ""TelefonoComprador"" <> ''";

                    using (var cmd = new NpgsqlCommand(sqlBols, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                envios.Add(new NotificacionPersonalViewModel
                                {
                                    IdNotificacion = (int)r["IdBoleto"],
                                    TelefonoDestino = r["TelefonoComprador"].ToString(),
                                    NombreCliente = r["NombreComprador"].ToString()
                                });
                            }
                        }
                    }

                    int mensajesEnviados = 0;

                    foreach (var envio in envios)
                    {
                        bool esGanador = idBoletoGanador.HasValue && envio.IdNotificacion == idBoletoGanador.Value;
                        string plantillaAUsar = esGanador ? "tpl_ganador_rifa" : "tpl_agradecimiento";

                        // Le pasamos la urlImagenRifa como último parámetro
                        var resultadoEnvio = await EnviarWhatsAppTwilio(envio.TelefonoDestino, plantillaAUsar, envio.NombreCliente, acceso.Titulo, nombreGanador, urlImagenRifa);

                        string sqlLog = @"INSERT INTO ""RifasPersonales_Notificaciones"" 
                                  (""IdRifa"", ""IdBoleto"", ""TelefonoDestino"", ""TipoMensaje"", ""EstadoEnvio"", ""RespuestaAPI"") 
                                  VALUES (@idr, @idb, @tel, @tipo, @est, @res)";
                        using (var cmdLog = new NpgsqlCommand(sqlLog, conexion))
                        {
                            cmdLog.Parameters.AddWithValue("@idr", idRifa);
                            cmdLog.Parameters.AddWithValue("@idb", envio.IdNotificacion);
                            cmdLog.Parameters.AddWithValue("@tel", envio.TelefonoDestino);
                            cmdLog.Parameters.AddWithValue("@tipo", plantillaAUsar);
                            cmdLog.Parameters.AddWithValue("@est", resultadoEnvio.Exito ? "Enviado" : "Fallido");
                            cmdLog.Parameters.AddWithValue("@res", resultadoEnvio.Respuesta ?? "");
                            await cmdLog.ExecuteNonQueryAsync();
                        }

                        if (resultadoEnvio.Exito) mensajesEnviados++;
                    }

                    MostrarMensaje("Proceso Terminado", $"Se procesaron {envios.Count} números y se enviaron con éxito {mensajesEnviados} mensajes de WhatsApp.", TipoMensaje.Exito);
                    string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                    await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Crear, $"Gestor Personal: Envío masivo de WP en Rifa {idRifa}", ip);
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Envío", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Resultados", new { token = tokenRifa });
        }

        private async Task<(bool Exito, string Respuesta)> EnviarWhatsAppTwilio(string numeroDestino, string plantillaId, string nombreComprador, string tituloRifa, string nombreGanador, string urlImagen)
        {
            try
            {
                // 1. Limpieza y formateo del número
                numeroDestino = numeroDestino.Replace(" ", "").Replace("-", "");
                if (!numeroDestino.StartsWith("+"))
                {
                    numeroDestino = "+" + numeroDestino;
                }

                if (numeroDestino.StartsWith("+52") && numeroDestino.Length == 13)
                {
                    numeroDestino = numeroDestino.Insert(3, "1");
                }

                string fromNumber = _twilioWhatsAppNumber.Replace("whatsapp:", "").Trim();

                var messageOptions = new CreateMessageOptions(new Twilio.Types.PhoneNumber($"whatsapp:{numeroDestino}"))
                {
                    From = new Twilio.Types.PhoneNumber($"whatsapp:{fromNumber}")
                };

                // 2. PROTECCIÓN CONTRA NULLS (Twilio rechaza el JSON si hay valores nulos)
                nombreComprador = string.IsNullOrWhiteSpace(nombreComprador) ? "Participante" : nombreComprador;
                tituloRifa = string.IsNullOrWhiteSpace(tituloRifa) ? "la rifa" : tituloRifa;
                nombreGanador = string.IsNullOrWhiteSpace(nombreGanador) ? "Anónimo" : nombreGanador;

                if (plantillaId == "tpl_ganador_rifa")
                {
                    messageOptions.ContentSid = "HXa4e8fa346f8b6187d14bb8b198790e4c"; // Tu SID de la imagen

                    // La plantilla contiene:
                    //¡Felicidades {{2}}! Has ganado la rifa de {{3}}. Por favor, ponte en contacto directamente con tu vendedor para coordinar la entrega de tu premio. 
                    //Este es un mensaje automático del sistema. No respondas a este chat ya que no es monitoreado.
                    var variables = new Dictionary<string, string>
                    {
                        { "1", nombreComprador },
                        { "2", tituloRifa }
                    };

                    messageOptions.ContentVariables = System.Text.Json.JsonSerializer.Serialize(variables);
                }
                else if (plantillaId == "tpl_agradecimiento")
                {
                    //Hola {{1}}, te informamos que la rifa de {{2}} ha finalizado. El numero ganador pertenece a {{3}}. 
                    //Si tienes alguna duda, por favor ponte en contacto directamente con tu vendedor. ¡Mucha suerte a la proxima!
                    //Este es un mensaje automatico del sistema.No respondas a este chat ya que no es monitoreado.
                    messageOptions.ContentSid = "HXd1f8d6a4cb3fc5f289e5a1b9fa605615";

                    // ASEGÚRATE de que esta plantilla en la consola tenga exactamente 3 llaves {{1}} {{2}} {{3}}
                    // Si solo tiene 2, borra la tercera aquí:
                    var variables = new Dictionary<string, string>
                    {
                        { "1", nombreComprador },
                        { "2", tituloRifa },
                        { "3", nombreGanador }
                    };
                    messageOptions.ContentVariables = System.Text.Json.JsonSerializer.Serialize(variables);
                }

                // 4. Envío del mensaje
                var message = await MessageResource.CreateAsync(messageOptions);

                if (message.ErrorCode != null)
                {
                    return (false, $"Error Twilio {message.ErrorCode}: {message.ErrorMessage}");
                }

                if (message.Status == MessageResource.StatusEnum.Failed ||
                    message.Status == MessageResource.StatusEnum.Undelivered)
                {
                    return (false, $"Mensaje fallido o no entregable. Estado: {message.Status}");
                }

                return (true, message.Sid);
            }
            catch (Twilio.Exceptions.ApiException twilioEx)
            {
                // Esto atrapará exactamente qué variable le está molestando a Twilio
                return (false, $"Excepción API Twilio: {twilioEx.Message}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditarBoletoVendido(string tokenRifa, int IdBoleto, string NombreComprador, string TelefonoComprador, string Comentarios, bool MarcadorPersonal)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Leer))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para acceder al módulo.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(tokenRifa);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);

                    if (!acceso.EsCreador && !acceso.EsVendedor) throw new Exception("No tienes acceso a esta rifa.");
                    if (acceso.Estado != "Activa") throw new Exception("La rifa no está activa, no se pueden editar ventas.");

                    string condicionFiltro = acceso.EsCreador
                        ? @"(""IdUsuarioAsignado"" = @uid OR (""IdUsuarioAsignado"" IS NULL AND ""NombrePromotorExterno"" IS NOT NULL))"
                        : @"(""IdUsuarioAsignado"" = @uid)";


                    string telValidado = null;
                    if (!string.IsNullOrWhiteSpace(TelefonoComprador))
                    {
                        telValidado = LimpiarYValidarTelefono(TelefonoComprador);
                    }

                    string sqlUpd = $@"UPDATE ""RifasPersonales_Boletos"" 
                                       SET ""NombreComprador"" = @nom, 
                                           ""TelefonoComprador"" = @tel, 
                                           ""Comentarios"" = @com,
                                           ""MarcadorPersonal"" = @marc
                                       WHERE ""IdBoleto"" = @idBol 
                                         AND ""Estado"" = 'Vendido' 
                                         AND {condicionFiltro}";

                    using (var cmd = new NpgsqlCommand(sqlUpd, conexion))
                    {
                        cmd.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(NombreComprador) ? "Anónimo" : NombreComprador);
                        cmd.Parameters.AddWithValue("@tel", (object)telValidado ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@com", (object)Comentarios ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@marc", MarcadorPersonal);
                        cmd.Parameters.AddWithValue("@idBol", IdBoleto);
                        cmd.Parameters.AddWithValue("@uid", idUsuario);

                        int afectadas = await cmd.ExecuteNonQueryAsync();
                        if (afectadas > 0)
                        {
                            MostrarMensaje("Éxito", "La información del comprador se ha actualizado correctamente.", TipoMensaje.Exito);
                            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Gestor Personal: Editó datos del boleto #{IdBoleto} en Rifa {idRifa}", ip);
                        }
                        else
                        {
                            MostrarMensaje("Error", "No se pudo actualizar la información. Verifica que el boleto sea tuyo y ya esté vendido.", TipoMensaje.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("PuntoDeVenta", new { token = tokenRifa });
        }

        // ========================================================================
        // 11. SORTEO EN VIVO (GET) - CARGA CON RECUPERACIÓN CRIPTOGRÁFICA
        // ========================================================================
        public async Task<IActionResult> Sorteo(string token)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos de edición en este módulo.", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            int idRifa = Funciones.DesencriptarId(token);
            if (idRifa <= 0)
            {
                MostrarMensaje("Enlace Inválido", "El identificador del sorteo no es válido.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var participantes = new List<BoletoPersonalViewModel>();
            var descartesPrevios = new List<BoletoPersonalViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.EsCreador)
                    {
                        MostrarMensaje("Acceso Denegado", "Solo el administrador de la rifa puede iniciar la interfaz de sorteo.", TipoMensaje.Error);
                        return RedirectToAction("PanelAdministracion", new { token = token });
                    }
                    if (acceso.Estado != "Activa")
                    {
                        MostrarMensaje("Bloqueado", "Esta rifa ya finalizó o fue cancelada, el sorteo no puede realizarse nuevamente.", TipoMensaje.Alerta);
                        return RedirectToAction("PanelAdministracion", new { token = token });
                    }

                    // 1. Validaciones de la rifa y de ganador previo
                    string sqlValidacion = @"
                SELECT 
                    (SELECT ""IdBoletoGanador"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id) as GanadorActual,
                    (SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @id AND ""Estado"" = 'Vendido') as TotalVendidos,
                    (SELECT ""CostoBoleto"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id) as CostoBoleto,
                    (SELECT COALESCE(SUM(""Monto""), 0) FROM ""RifasPersonales_Pagos"" WHERE ""IdRifa"" = @id AND ""Estado"" = 'Aprobado') as TotalPagado";

                    using (var cmdValidacion = new NpgsqlCommand(sqlValidacion, conexion))
                    {
                        cmdValidacion.Parameters.AddWithValue("@id", idRifa);
                        using (var rValidacion = await cmdValidacion.ExecuteReaderAsync())
                        {
                            if (await rValidacion.ReadAsync())
                            {
                                if (rValidacion["GanadorActual"] != DBNull.Value)
                                {
                                    MostrarMensaje("Sorteo Bloqueado", "Esta rifa ya tiene un ganador registrado.", TipoMensaje.Alerta);
                                    return RedirectToAction("Resultados", new { token = token });
                                }

                                decimal deuda = (Convert.ToInt32(rValidacion["TotalVendidos"]) * Convert.ToDecimal(rValidacion["CostoBoleto"])) - Convert.ToDecimal(rValidacion["TotalPagado"]);
                                if (deuda > 0.01m)
                                {
                                    MostrarMensaje("Bloqueado", $"Existe saldo pendiente de ${deuda:N2}. Liquida antes de sortear.", TipoMensaje.Alerta);
                                    return RedirectToAction("Asignaciones", new { token = token });
                                }
                            }
                        }
                    }

                    // 2. RECUPERAR DESCARTES Y VALIDAR ENCRIPTACIÓN
                    var idsDescartados = new List<int>();
                    string sqlDescartes = @"SELECT ""BoletoToken"" FROM ""RifasPersonales_Descartes"" WHERE ""IdRifa"" = @id ORDER BY ""FechaDescarte"" ASC";

                    using (var cmdDesc = new NpgsqlCommand(sqlDescartes, conexion))
                    {
                        cmdDesc.Parameters.AddWithValue("@id", idRifa);
                        using (var rDesc = await cmdDesc.ExecuteReaderAsync())
                        {
                            while (await rDesc.ReadAsync())
                            {
                                string tokenBoleto = rDesc["BoletoToken"].ToString();
                                int idBoletoDesc = Funciones.DesencriptarId(tokenBoleto);

                                // Si devuelve 0, alguien inyectó basura en la tabla directamente
                                if (idBoletoDesc <= 0)
                                {
                                    MostrarMensaje("ALERTA CRÍTICA", "Integridad comprometida. Se detectó un error de datos en el historial de boletos descartados.", TipoMensaje.Error);
                                    return RedirectToAction("PanelAdministracion", new { token = token });
                                }
                                idsDescartados.Add(idBoletoDesc);
                            }
                        }
                    }

                    // 3. CARGAR TODOS LOS BOLETOS Y SEPARARLOS EN MEMORIA
                    string sql = @"SELECT ""IdBoleto"", ""Numero"", ""NombreComprador"" FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @id AND ""Estado"" = 'Vendido' ORDER BY ""Numero"" ASC";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idBol = (int)r["IdBoleto"];
                                var boleto = new BoletoPersonalViewModel
                                {
                                    IdBoleto = idBol,
                                    Numero = (int)r["Numero"],
                                    NombreComprador = r["NombreComprador"]?.ToString() ?? "Anónimo"
                                };

                                if (idsDescartados.Contains(idBol)) descartesPrevios.Add(boleto);
                                else participantes.Add(boleto);
                            }
                        }
                    }

                    if (!participantes.Any() && !descartesPrevios.Any())
                    {
                        MostrarMensaje("Sorteo No Disponible", "Aún no tienes boletos vendidos.", TipoMensaje.Alerta);
                        return RedirectToAction("PanelAdministracion", new { token = token });
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            ViewBag.TokenRifa = token;
            ViewBag.DescartesPrevios = descartesPrevios;
            return View(participantes);
        }

        // ========================================================================
        // 11.5 PROCESAR TIRO (AJAX) - TRIPLE BLINDAJE (Finanzas, Sync, Criptografía)
        // ========================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarTiroSorteo(string tokenRifa, bool esGanador, int intentoActual)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Editar)) return Json(new { exito = false, mensaje = "Sin permisos." });

            try
            {
                int idRifa = Funciones.DesencriptarId(tokenRifa);
                int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var acceso = await ValidarAccesoRifa(idRifa, idUsuario, conexion);
                    if (!acceso.EsCreador || acceso.Estado != "Activa") return Json(new { exito = false, mensaje = "Rifa inactiva o sin permisos." });

                    // 1. Validar Finanzas y Ganador
                    string sqlValidacion = @"
                SELECT 
                    (SELECT ""IdBoletoGanador"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id) as GanadorActual,
                    (SELECT COUNT(*) FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @id AND ""Estado"" = 'Vendido') as TotalVendidos,
                    (SELECT ""CostoBoleto"" FROM ""RifasPersonales_Rifas"" WHERE ""IdRifa"" = @id) as CostoBoleto,
                    (SELECT COALESCE(SUM(""Monto""), 0) FROM ""RifasPersonales_Pagos"" WHERE ""IdRifa"" = @id AND ""Estado"" = 'Aprobado') as TotalPagado";

                    int totalVendidos = 0;
                    using (var cmdVal = new NpgsqlCommand(sqlValidacion, conexion))
                    {
                        cmdVal.Parameters.AddWithValue("@id", idRifa);
                        using (var rVal = await cmdVal.ExecuteReaderAsync())
                        {
                            if (await rVal.ReadAsync())
                            {
                                if (rVal["GanadorActual"] != DBNull.Value) return Json(new { exito = false, mensaje = "Ya hay un ganador registrado." });
                                decimal deuda = (Convert.ToInt32(rVal["TotalVendidos"]) * Convert.ToDecimal(rVal["CostoBoleto"])) - Convert.ToDecimal(rVal["TotalPagado"]);
                                if (deuda > 0.01m) return Json(new { exito = false, mensaje = "Existen deudas pendientes." });
                                totalVendidos = Convert.ToInt32(rVal["TotalVendidos"]);
                            }
                        }
                    }

                    // 2. VALIDAR INTEGRIDAD CRIPTOGRÁFICA DE DESCARTES
                    var idsDescartados = new List<int>();
                    string sqlDesc = @"SELECT ""BoletoToken"" FROM ""RifasPersonales_Descartes"" WHERE ""IdRifa"" = @id";
                    using (var cmdDesc = new NpgsqlCommand(sqlDesc, conexion))
                    {
                        cmdDesc.Parameters.AddWithValue("@id", idRifa);
                        using (var rDesc = await cmdDesc.ExecuteReaderAsync())
                        {
                            while (await rDesc.ReadAsync())
                            {
                                string tokenBol = rDesc["BoletoToken"].ToString();
                                int decId = Funciones.DesencriptarId(tokenBol);
                                if (decId <= 0) return Json(new { exito = false, mensaje = "🔥 ALERTA: Cadena de encriptación rota en BD. Posible inyección manual." });
                                idsDescartados.Add(decId);
                            }
                        }
                    }

                    // 3. VALIDACIÓN DE SECUENCIA (Previene saltos e inyecciones masivas)
                    int descartesEnBD = idsDescartados.Count;
                    if (descartesEnBD != (intentoActual - 1)) return Json(new { exito = false, codigoError = "SYNC", mensaje = "Re-sincronizando..." });
                    if (descartesEnBD >= totalVendidos) return Json(new { exito = false, codigoError = "EMPTY", mensaje = "Boletos agotados." });

                    // 4. SELECCIONAR PARTICIPANTES VÁLIDOS EN MEMORIA
                    var disponibles = new List<BoletoPersonalViewModel>();
                    string sqlDisponibles = @"SELECT ""IdBoleto"", ""Numero"", ""NombreComprador"" FROM ""RifasPersonales_Boletos"" WHERE ""IdRifa"" = @id AND ""Estado"" = 'Vendido'";
                    using (var cmdDisp = new NpgsqlCommand(sqlDisponibles, conexion))
                    {
                        cmdDisp.Parameters.AddWithValue("@id", idRifa);
                        using (var r = await cmdDisp.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                int idB = (int)r["IdBoleto"];
                                if (!idsDescartados.Contains(idB))
                                {
                                    disponibles.Add(new BoletoPersonalViewModel { IdBoleto = idB, Numero = (int)r["Numero"], NombreComprador = r["NombreComprador"]?.ToString() ?? "Anónimo" });
                                }
                            }
                        }
                    }

                    if (!disponibles.Any()) return Json(new { exito = false, mensaje = "No quedan participantes disponibles." });

                    // 5. GENERACIÓN Criptográfica del Resultado
                    int indiceGanador = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, disponibles.Count);
                    var seleccionado = disponibles[indiceGanador];

                    // 6. GUARDADO TRANSACCIONAL
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        if (esGanador)
                        {
                            await new NpgsqlCommand($@"UPDATE ""RifasPersonales_Rifas"" SET ""Estado"" = 'Finalizada', ""IdBoletoGanador"" = {seleccionado.IdBoleto} WHERE ""IdRifa"" = {idRifa}", conexion, trans).ExecuteNonQueryAsync();
                            await new NpgsqlCommand($@"DELETE FROM ""RifasPersonales_Descartes"" WHERE ""IdRifa"" = {idRifa}", conexion, trans).ExecuteNonQueryAsync();
                        }
                        else
                        {
                            // ENCRIPTAMOS EL ID ANTES DE GUARDARLO EN BD
                            string tokenSeguro = Funciones.EncriptarId(seleccionado.IdBoleto);
                            using (var cmdIns = new NpgsqlCommand(@"INSERT INTO ""RifasPersonales_Descartes"" (""IdRifa"", ""BoletoToken"") VALUES (@idR, @tok)", conexion, trans))
                            {
                                cmdIns.Parameters.AddWithValue("@idR", idRifa);
                                cmdIns.Parameters.AddWithValue("@tok", tokenSeguro);
                                await cmdIns.ExecuteNonQueryAsync();
                            }
                        }
                        await trans.CommitAsync();
                    }

                    return Json(new { exito = true, idBoleto = seleccionado.IdBoleto, numero = seleccionado.Numero, comprador = seleccionado.NombreComprador });
                }
            }
            catch (Exception ex) { return Json(new { exito = false, mensaje = ex.Message }); }
        }


        // ========================================================================
        // HELPER: SUBIR IMAGEN CLOUDINARY
        // ========================================================================
        private async Task<string> SubirImagenCloudinary(IFormFile foto, string folderPath)
        {
            using var memoryStream = new MemoryStream();
            using (var image = await SixLabors.ImageSharp.Image.LoadAsync(foto.OpenReadStream()))
            {
                const int MaxWidth = 1200;
                if (image.Width > MaxWidth)
                {
                    int newHeight = (int)((double)image.Height / image.Width * MaxWidth);
                    image.Mutate(x => x.Resize(MaxWidth, newHeight));
                }
                var encoder = new JpegEncoder { Quality = 75 };
                await image.SaveAsync(memoryStream, encoder);
            }

            memoryStream.Position = 0;
            var uploadParams = new ImageUploadParams()
            {
                File = new FileDescription(foto.FileName, memoryStream),
                Folder = folderPath,
                Transformation = new Transformation().FetchFormat("auto"),
                UseFilename = false,
                UniqueFilename = true
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);
            return uploadResult.SecureUrl.ToString();
        }
        private async Task DestruirImagenCloudinary(string urlImagen)
        {
            if (string.IsNullOrEmpty(urlImagen)) return;

            try
            {
                // Un método un poco más robusto para extraer el PublicId asumiendo el formato estándar de Cloudinary
                Uri uri = new Uri(urlImagen);
                string path = uri.AbsolutePath;

                // Normalmente path es: /v1234567/Carpeta/Subcarpeta/archivo.jpg
                // Buscamos el inicio de la ruta real ignorando la versión
                int versionIndex = path.IndexOf("/v");
                int startPos = -1;

                if (versionIndex >= 0)
                {
                    startPos = path.IndexOf('/', versionIndex + 2); // Buscar la barra después de /v123...
                }

                if (startPos == -1) startPos = path.IndexOf('/', 1); // Fallback si no hay versión

                if (startPos > 0)
                {
                    string publicIdConExt = path.Substring(startPos + 1);
                    int lastDot = publicIdConExt.LastIndexOf('.');
                    string publicId = lastDot > 0 ? publicIdConExt.Substring(0, lastDot) : publicIdConExt;

                    // Uri.UnescapeDataString para manejar espacios en nombres de archivo (%20)
                    publicId = Uri.UnescapeDataString(publicId);

                    await _cloudinary.DestroyAsync(new DeletionParams(publicId));
                }
            }
            catch (Exception ex)
            {
                // ¡AQUÍ ESTÁ EL TEMA! No bloqueamos el flujo, pero DEBEMOS registrarlo.
                // En un entorno de QA estricto, esto debería escribirse en un log de base de datos o archivo local.
                Console.WriteLine($"[ADVERTENCIA] Falló el borrado en Cloudinary para la URL: {urlImagen}. Archivo huérfano generado. Razón: {ex.Message}");
            }
        }
    }
}