using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Stripe;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    [Route("Donaciones/[action]")]
    [Route("Aportaciones/[action]")]
    public class DonacionesController : GlobalController
    {
        private readonly IConfiguration _configuration;
        private Parametros.Modulo Modulo = Parametros.Modulos.Caja;
        private static readonly bool _destinoDonacionEsBanco = true;
        private readonly string _stripeSecretKey;

        public DonacionesController(IConfiguration configuration, IWebHostEnvironment env)
        {
            _configuration = configuration;

            // Configuración Stripe
            _stripeSecretKey = _configuration["StripeEventos:SecretKey"];
            StripeConfiguration.ApiKey = _stripeSecretKey;
        }

        [Route("~/Donaciones", Order = 1)]
        [Route("~/Aportaciones", Order = 2)]
        public async Task<IActionResult> Index()
        {
            ViewBag.PrefijoTransfer = sPrefijoTransfer;
            ViewBag.PagoExitoso = TempData["PagoExitoso"];
            ViewBag.ErrorPago = TempData["ErrorPago"];

            int? idUsuarioActual = null;
            if (User.Identity != null && User.Identity.IsAuthenticated)
            {
                if (int.TryParse(User.FindFirst("IdUsuario")?.Value, out int parsedId))
                {
                    idUsuarioActual = parsedId;
                }
            }

            // OBTENER PROYECTOS ACTIVOS PARA LA VISTA
            var proyectosActivos = new List<dynamic>();
            try
            {
                using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await con.OpenAsync();
                    string sqlProy = @"SELECT ""Id_Proyecto"", ""Titulo"", ""Descripcion"" FROM ""Fin_Proyectos"" WHERE ""Activo"" = TRUE ORDER BY ""Fecha_Limite"" ASC";
                    using (var cmd = new NpgsqlCommand(sqlProy, con))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            proyectosActivos.Add(new
                            {
                                Id = (int)r["Id_Proyecto"],
                                Titulo = r["Titulo"].ToString(),
                                Descripcion = r["Descripcion"].ToString()
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("Error al cargar proyectos: " + ex.Message); }

            ViewBag.Proyectos = proyectosActivos;

            await VerificarPagosPendientes(idUsuarioActual);

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IniciarPagoTarjeta(decimal montoAportacion, string comentarioDonante, int? idProyectoAsignado)
        {
            if (montoAportacion < 50)
            {
                TempData["ErrorPago"] = "El monto mínimo para pago con tarjeta es de $50.00 MXN.";
                return RedirectToAction("Index");
            }

            try
            {
                int? idUser = User.Identity!.IsAuthenticated ? int.Parse(User.FindFirst("IdUsuario")!.Value) : null;
                string emailCliente = User.Identity.IsAuthenticated ? User.FindFirst("Email")?.Value : null;

                using (var conexion = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await conexion.OpenAsync();

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlIns = @"INSERT INTO ""Sist_Aportaciones_Transacciones"" 
                (""Id_Usuario"", ""Monto"", ""Estatus_Pago"", ""Fecha_Intento"", ""Comentario"", ""Id_Proyecto"") 
                VALUES (@idUser, @monto, 'pending', NOW(), @comentario, @idProyecto) 
                RETURNING ""Id_Transaccion""";

                            int idTransaccion = 0;
                            using (var cmd = new NpgsqlCommand(sqlIns, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@idUser", idUser ?? (object)DBNull.Value);
                                cmd.Parameters.AddWithValue("@monto", montoAportacion);
                                cmd.Parameters.AddWithValue("@comentario", string.IsNullOrEmpty(comentarioDonante) ? (object)DBNull.Value : comentarioDonante);
                                cmd.Parameters.AddWithValue("@idProyecto", idProyectoAsignado ?? (object)DBNull.Value);

                                idTransaccion = (int)await cmd.ExecuteScalarAsync();
                            }

                            if (idTransaccion == 0)
                            {
                                throw new Exception("Error al instanciar la transacción en la base de datos.");
                            }

                            // MODIFICACIÓN 1: Usamos un GUID para crear una referencia única y opaca.
                            // Ejemplo de salida: APOR_A1B2C3D4E5
                            string folioSeguimiento = Guid.NewGuid().ToString("N").Substring(0, 10).ToUpper();
                            string referenciaUnica = $"APOR_{folioSeguimiento}";

                            string sqlUpd = @"UPDATE ""Sist_Aportaciones_Transacciones"" SET ""External_Reference"" = @ref WHERE ""Id_Transaccion"" = @id";
                            using (var cmdUpd = new NpgsqlCommand(sqlUpd, conexion, transaccion))
                            {
                                cmdUpd.Parameters.AddWithValue("@ref", referenciaUnica);
                                cmdUpd.Parameters.AddWithValue("@id", idTransaccion);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            string urlRetorno = Url.Action("ValidarRetornoStripe", "Donaciones", null, Request.Scheme);
                            var options = new Stripe.Checkout.SessionCreateOptions
                            {
                                PaymentMethodTypes = new List<string> { "card" },
                                LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                        {
                            new Stripe.Checkout.SessionLineItemOptions
                            {
                                PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                                {
                                    UnitAmountDecimal = montoAportacion * 100,
                                    Currency = "mxn",
                                    ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions
                                    {
                                        Name = "Donación Solidaria" + (idProyectoAsignado.HasValue ? " (Causa Directa)" : ""),
                                        // MODIFICACIÓN 2: Mostramos el folio opaco en lugar del ID incremental
                                        Description = $"Folio de donación: {referenciaUnica}"
                                    }
                                },
                                Quantity = 1
                            }
                        },
                                Mode = "payment",
                                SuccessUrl = $"{urlRetorno}?session_id={{CHECKOUT_SESSION_ID}}",
                                CancelUrl = Url.Action("Index", "Donaciones", null, Request.Scheme),
                                ClientReferenceId = referenciaUnica,
                                CustomerEmail = emailCliente,
                                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
                            };

                            var service = new Stripe.Checkout.SessionService();
                            var session = await service.CreateAsync(options);

                            string sqlUpdRef = @"UPDATE ""Sist_Aportaciones_Transacciones"" SET ""Ref_Pasarela"" = @sid WHERE ""Id_Transaccion"" = @id";
                            using (var cmdUpd2 = new NpgsqlCommand(sqlUpdRef, conexion, transaccion))
                            {
                                cmdUpd2.Parameters.AddWithValue("@sid", session.Id);
                                cmdUpd2.Parameters.AddWithValue("@id", idTransaccion);
                                await cmdUpd2.ExecuteNonQueryAsync();
                            }

                            await transaccion.CommitAsync();

                            return Redirect(session.Url);
                        }
                        catch (Exception)
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error Crítico en IniciarPagoTarjeta: " + ex.Message);
                TempData["ErrorPago"] = "Ocurrió un error al preparar tu solicitud de pago seguro.";
                return RedirectToAction("Index");
            }
        }

        [HttpGet]
        public async Task<IActionResult> ValidarRetornoStripe(string session_id)
        {
            if (string.IsNullOrEmpty(session_id)) return RedirectToAction("Index");

            try
            {
                var service = new Stripe.Checkout.SessionService();
                var session = await service.GetAsync(session_id);

                if (session.PaymentStatus == "paid")
                {
                    bool procesado = await ConsolidarPagoStripe(session);

                    if (procesado)
                        TempData["PagoExitoso"] = "Tu Donación con tarjeta se ha procesado correctamente. ¡Muchas gracias por tu generosidad!";
                    else
                        TempData["PagoExitoso"] = "Tu Donación ya había sido registrada previamente. ¡Gracias!";
                }
                else
                {
                    TempData["ErrorPago"] = "El pago no pudo ser procesado o fue cancelado.";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al validar retorno Stripe: " + ex.Message);
                TempData["ErrorPago"] = "Error crítico al validar la respuesta bancaria.";
            }

            return RedirectToAction("Index");
        }

        private async Task<bool> ConsolidarPagoStripe(Stripe.Checkout.Session session)
        {
            using (var conexion = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
            {
                await conexion.OpenAsync();

                using (var transaccion = await conexion.BeginTransactionAsync())
                {
                    try
                    {
                        // 1. LEER LA TRANSACCIÓN (Sin actualizar el estatus todavía)
                        string sqlRead = @"
        SELECT ""Id_Transaccion"", ""Id_Usuario"", ""Monto"", ""Comentario"", ""Id_Proyecto"", ""Estatus_Pago""
        FROM ""Sist_Aportaciones_Transacciones"" 
        WHERE ""External_Reference"" = @ref FOR UPDATE";

                        int idTransaccion = 0;
                        int? idUsuarioOriginal = null;
                        int? idProyectoAsignado = null;
                        decimal montoDonado = 0;
                        string comentario = "";
                        string estatusPago = "";
                        bool transaccionReclamada = false;

                        using (var cmdRead = new NpgsqlCommand(sqlRead, conexion, transaccion))
                        {
                            cmdRead.Parameters.AddWithValue("@ref", session.ClientReferenceId);
                            using (var r = await cmdRead.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    transaccionReclamada = true;
                                    idTransaccion = (int)r["Id_Transaccion"];
                                    idUsuarioOriginal = r["Id_Usuario"] != DBNull.Value ? (int)r["Id_Usuario"] : (int?)null;
                                    idProyectoAsignado = r["Id_Proyecto"] != DBNull.Value ? (int)r["Id_Proyecto"] : (int?)null;
                                    montoDonado = (decimal)r["Monto"];
                                    comentario = r["Comentario"] != DBNull.Value ? r["Comentario"].ToString() : "";
                                    estatusPago = r["Estatus_Pago"].ToString();
                                }
                            }
                        }

                        // Si no existe o ya fue consolidada previamente
                        if (!transaccionReclamada || estatusPago == "paid")
                        {
                            await transaccion.RollbackAsync();
                            return false;
                        }

                        // BUSCAR EL TÍTULO DEL PROYECTO (Si existe)
                        string tituloProyecto = "";
                        if (idProyectoAsignado.HasValue && idProyectoAsignado.Value > 0)
                        {
                            using (var cmdProy = new NpgsqlCommand(@"SELECT ""Titulo"" FROM ""Fin_Proyectos"" WHERE ""Id_Proyecto"" = @id", conexion, transaccion))
                            {
                                cmdProy.Parameters.AddWithValue("@id", idProyectoAsignado.Value);
                                var resProy = await cmdProy.ExecuteScalarAsync();
                                if (resProy != null) tituloProyecto = resProy.ToString();
                            }
                        }

                        // 2. OBTENER EL NETO DE STRIPE
                        decimal montoNeto = 0;
                        try
                        {
                            var piService = new Stripe.PaymentIntentService();
                            var optionsPI = new Stripe.PaymentIntentGetOptions();
                            optionsPI.AddExpand("latest_charge.balance_transaction");

                            if (!string.IsNullOrEmpty(session.PaymentIntentId))
                            {
                                var pi = await piService.GetAsync(session.PaymentIntentId, optionsPI);
                                if (pi.LatestCharge?.BalanceTransaction != null)
                                    montoNeto = pi.LatestCharge.BalanceTransaction.Net / 100.0m;
                            }
                        }
                        catch { montoNeto = 0; }

                        // 3. SI EL NETO ES 0, SE QUEDA EN SALA DE ESPERA
                        if (montoNeto <= 0)
                        {
                            string sqlWait = @"
                    UPDATE ""Sist_Aportaciones_Transacciones"" 
                    SET ""Monto_Neto"" = 0 
                    WHERE ""Id_Transaccion"" = @id";
                            using (var cmdWait = new NpgsqlCommand(sqlWait, conexion, transaccion))
                            {
                                cmdWait.Parameters.AddWithValue("@id", idTransaccion);
                                await cmdWait.ExecuteNonQueryAsync();
                            }
                            await transaccion.CommitAsync();
                            return true;
                        }

                        // 4. SI YA HAY NETO, AHORA SÍ MARCAMOS COMO PAID Y REGISTRAMOS
                        string sqlUpdatePaid = @"
                UPDATE ""Sist_Aportaciones_Transacciones"" 
                SET ""Estatus_Pago"" = 'paid', ""Monto_Neto"" = @neto
                WHERE ""Id_Transaccion"" = @id";
                        using (var cmdUpd = new NpgsqlCommand(sqlUpdatePaid, conexion, transaccion))
                        {
                            cmdUpd.Parameters.AddWithValue("@neto", montoNeto);
                            cmdUpd.Parameters.AddWithValue("@id", idTransaccion);
                            await cmdUpd.ExecuteNonQueryAsync();
                        }

                        string nombreUsuario = "Donante Anónimo / Tarjeta";
                        if (idUsuarioOriginal.HasValue)
                        {
                            var cmdU = new NpgsqlCommand(@"SELECT ""NombreCompleto"" FROM ""Sist_Usuarios"" WHERE ""Id_Usuario"" = @id", conexion, transaccion);
                            cmdU.Parameters.AddWithValue("@id", idUsuarioOriginal.Value);
                            var resNom = await cmdU.ExecuteScalarAsync();
                            if (resNom != null) nombreUsuario = resNom.ToString();
                        }

                        string sqlDonacion = @"INSERT INTO ""Sist_Aportaciones"" 
        (""Id_Usuario"", ""Monto"", ""Fecha_Registro"", ""Estatus"", ""Comentario_Donante"", ""Monto_Validado"", ""Id_Usuario_Revisor"", ""Fecha_Revision"", ""Folio_Bancario"", ""Comprobante_Bytea"", ""Formato_Imagen"", ""Id_Proyecto"") 
        VALUES (@idUser, @montoNeto, NOW(), 'Verificado', @comentario, @montoNeto, 1, NOW(), @folio, @bytesVacios, 'stripe/auto', @idProyecto) 
        RETURNING ""Id_Donacion""";

                        int idDonacionFinal = 0;
                        using (var cmd = new NpgsqlCommand(sqlDonacion, conexion, transaccion))
                        {
                            cmd.Parameters.AddWithValue("@idUser", idUsuarioOriginal.HasValue ? idUsuarioOriginal.Value : (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@montoNeto", montoNeto);
                            cmd.Parameters.AddWithValue("@comentario", string.IsNullOrEmpty(comentario) ? (object)DBNull.Value : comentario);
                            cmd.Parameters.AddWithValue("@folio", session.PaymentIntentId);
                            cmd.Parameters.AddWithValue("@bytesVacios", new byte[0]);
                            cmd.Parameters.AddWithValue("@idProyecto", idProyectoAsignado ?? (object)DBNull.Value);
                            idDonacionFinal = (int)await cmd.ExecuteScalarAsync();
                        }

                        // INYECCIÓN A LA CAJA CORRESPONDIENTE
                        if (idProyectoAsignado.HasValue && idProyectoAsignado.Value > 0)
                        {
                            string sqlProjCaja = @"INSERT INTO ""Fin_Proyectos_Caja"" 
            (""Id_Proyecto"", ""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Folio_Bancario"")
            VALUES (@idp, @concepto, @montoNeto, 'Ingreso', NOW(), 1, @enBanco, @folioBanc)";

                            using (var cmdCaja = new NpgsqlCommand(sqlProjCaja, conexion, transaccion))
                            {
                                cmdCaja.Parameters.AddWithValue("@idp", idProyectoAsignado.Value);
                                cmdCaja.Parameters.AddWithValue("@concepto", $"Donación Stripe - {nombreUsuario}");
                                cmdCaja.Parameters.AddWithValue("@montoNeto", montoNeto);
                                cmdCaja.Parameters.AddWithValue("@enBanco", true);
                                cmdCaja.Parameters.AddWithValue("@folioBanc", session.PaymentIntentId);
                                await cmdCaja.ExecuteNonQueryAsync();
                            }
                        }
                        else
                        {
                            string sqlCaja = @"INSERT INTO ""Fin_Caja"" 
            (""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Folio_Bancario"")
            VALUES (@concepto, @montoNeto, 'Ingreso', NOW(), 1, @enBanco, @folioBanc)";

                            using (var cmdCaja = new NpgsqlCommand(sqlCaja, conexion, transaccion))
                            {
                                cmdCaja.Parameters.AddWithValue("@concepto", $"{sNombreConceptoDonacion} (Stripe) - {nombreUsuario}");
                                cmdCaja.Parameters.AddWithValue("@montoNeto", montoNeto);
                                cmdCaja.Parameters.AddWithValue("@enBanco", true);
                                cmdCaja.Parameters.AddWithValue("@folioBanc", session.PaymentIntentId);
                                await cmdCaja.ExecuteNonQueryAsync();
                            }
                        }

                        await Funciones.RegistrarBitacora(conexion, idUsuarioOriginal ?? 1, Modulo, Parametros.AccionesBitacora.Crear, $"Se consolidó Donación definitiva #{idDonacionFinal} derivada de la transacción Stripe con neto ${montoNeto}", "Sistema", transaccion);

                        // ====================================================================
                        // Pasamos 'montoDonado' (original) y 'tituloProyecto'
                        // ====================================================================
                        await EnviarComentarioAComunidad(conexion, transaccion, comentario, idUsuarioOriginal, montoDonado, tituloProyecto);

                        await transaccion.CommitAsync();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        await transaccion.RollbackAsync();
                        Console.WriteLine($"[CRÍTICO] Falló consolidación. Transacción revertida a pending. Motivo: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        private async Task VerificarPagosPendientes(int? idUser)
        {
            try
            {
                using var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion"));
                await con.OpenAsync();

                var sessionService = new Stripe.Checkout.SessionService();

                // ====================================================================
                // 1. VALIDAR PENDIENTES (Actualiza sesiones pagadas o caducadas)
                // ====================================================================
                // Buscamos transacciones donde el usuario apenas inició el proceso de pago
                string sqlPending = @"
            SELECT ""Id_Transaccion"", ""Ref_Pasarela"", ""External_Reference""
            FROM ""Sist_Aportaciones_Transacciones""
            WHERE (""Id_Usuario"" = @uid OR (@uid IS NULL AND ""Id_Usuario"" IS NULL))
              AND ""Estatus_Pago"" = 'pending'
              AND ""Ref_Pasarela"" LIKE 'cs_%'";

                var pendientes = new List<dynamic>();
                using (var cmdPending = new NpgsqlCommand(sqlPending, con))
                {
                    cmdPending.Parameters.AddWithValue("@uid", idUser ?? (object)DBNull.Value);
                    using var r = await cmdPending.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        pendientes.Add(new
                        {
                            IdTrx = (int)r["Id_Transaccion"],
                            SessionId = r["Ref_Pasarela"].ToString(),
                            RefEsperada = r["External_Reference"].ToString()
                        });
                    }
                }

                foreach (var p in pendientes)
                {
                    try
                    {
                        var session = await sessionService.GetAsync(p.SessionId);

                        if (session.PaymentStatus == "paid" && session.ClientReferenceId == p.RefEsperada)
                        {
                            // Si ya se pagó en Stripe, le pasamos la estafeta a la función consolidadora
                            // para que intente obtener el neto e insertarlo en las cajas.
                            await ConsolidarPagoStripe(session);
                        }
                        else if (session.PaymentStatus == "unpaid" && session.ExpiresAt < DateTime.UtcNow)
                        {
                            // Si ya expiró el tiempo para pagar en la ventana de Stripe, lo marcamos como cancelado
                            using var cmdExp = new NpgsqlCommand(@"UPDATE ""Sist_Aportaciones_Transacciones"" SET ""Estatus_Pago"" = 'expired' WHERE ""Id_Transaccion"" = @id", con);
                            cmdExp.Parameters.AddWithValue("@id", p.IdTrx);
                            await cmdExp.ExecuteNonQueryAsync();
                        }
                    }
                    catch { }
                }

                // ====================================================================
                // 2. VALIDAR NETOS EN 0 (Distribuir las líneas ya validadas a las cajas)
                // ====================================================================
                // Buscamos transacciones que YA sabemos que están pagadas, pero cuyo balance neto
                // aún no se registraba (probablemente porque Stripe tardó en liberar la comisión)
                string sqlNetosCero = @"
            SELECT ""Ref_Pasarela"", ""External_Reference""
            FROM ""Sist_Aportaciones_Transacciones""
            WHERE (""Id_Usuario"" = @uid OR (@uid IS NULL AND ""Id_Usuario"" IS NULL))
              AND ""Estatus_Pago"" = 'paid'
              AND ""Monto_Neto"" = 0
              AND ""Ref_Pasarela"" LIKE 'cs_%'";

                var netosCero = new List<dynamic>();
                using (var cmdNeto = new NpgsqlCommand(sqlNetosCero, con))
                {
                    cmdNeto.Parameters.AddWithValue("@uid", idUser ?? (object)DBNull.Value);
                    using var r2 = await cmdNeto.ExecuteReaderAsync();
                    while (await r2.ReadAsync())
                    {
                        netosCero.Add(new
                        {
                            SessionId = r2["Ref_Pasarela"].ToString(),
                            RefEsperada = r2["External_Reference"].ToString()
                        });
                    }
                }

                foreach (var n in netosCero)
                {
                    try
                    {
                        var session = await sessionService.GetAsync(n.SessionId);

                        if (session.PaymentStatus == "paid" && session.ClientReferenceId == n.RefEsperada)
                        {
                            // Al volver a enviarlo, ConsolidarPagoStripe consultará de nuevo el PaymentIntent.
                            // Como ahora el neto ya será mayor a 0, actualizará el Monto_Neto en la tabla 
                            // de transacciones y registrará la línea final en Sist_Aportaciones y en Cajas.
                            await ConsolidarPagoStripe(session);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error en VerificarPagosPendientes Aportaciones: {ex.Message}");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubirComprobante(IFormFile archivoComprobante, decimal? montoDonado, string comentarioDonante, int? idProyectoAsignado)
        {
            if (archivoComprobante == null || archivoComprobante.Length == 0)
            {
                MostrarMensaje("Atención", "Por favor selecciona una imagen de tu comprobante.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            const long limitePeso = 10 * 1024 * 1024;
            if (archivoComprobante.Length > limitePeso)
            {
                MostrarMensaje("Archivo muy pesado", "El comprobante no debe superar los 10 MB.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (!string.IsNullOrEmpty(comentarioDonante) && comentarioDonante.Length > 500)
            {
                MostrarMensaje("Mensaje muy largo", "El comentario es demasiado largo. Por favor, resúmelo a un máximo de 500 caracteres.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var extensionesPermitidas = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var extension = Path.GetExtension(archivoComprobante.FileName).ToLowerInvariant();

            if (string.IsNullOrEmpty(extension) || !extensionesPermitidas.Contains(extension))
            {
                MostrarMensaje("Formato no permitido", "Solo aceptamos imágenes JPG, PNG o WEBP.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                int? idUsuario = null;
                string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

                if (User.Identity!.IsAuthenticated)
                {
                    if (int.TryParse(User.FindFirst("IdUsuario")?.Value, out int parsedId))
                        idUsuario = parsedId;
                }

                byte[] imagenComprimida;
                using (var stream = archivoComprobante.OpenReadStream())
                using (var image = await Image.LoadAsync(stream))
                {
                    if (image.Width > 1200) image.Mutate(x => x.Resize(1200, 0));
                    using (var ms = new MemoryStream())
                    {
                        await image.SaveAsync(ms, new JpegEncoder { Quality = 75 });
                        imagenComprimida = ms.ToArray();
                    }
                }

                using (var conexion = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sql = @"INSERT INTO ""Sist_Aportaciones"" 
                                 (""Id_Usuario"", ""Monto"", ""Comprobante_Bytea"", ""Formato_Imagen"", ""Fecha_Registro"", ""Estatus"", ""Comentario_Donante"", ""Id_Proyecto"") 
                                 VALUES (@idUser, @monto, @archivo, 'image/jpeg', NOW(), 'Pendiente', @comentario, @idProyecto)
                                 RETURNING ""Id_Donacion""";

                            int idDonacion = 0;
                            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@idUser", idUsuario ?? (object)DBNull.Value);
                                cmd.Parameters.AddWithValue("@monto", montoDonado ?? (object)DBNull.Value);
                                cmd.Parameters.AddWithValue("@archivo", imagenComprimida);
                                cmd.Parameters.AddWithValue("@comentario", string.IsNullOrEmpty(comentarioDonante) ? (object)DBNull.Value : comentarioDonante);
                                cmd.Parameters.AddWithValue("@idProyecto", idProyectoAsignado ?? (object)DBNull.Value);

                                idDonacion = (int)await cmd.ExecuteScalarAsync();
                            }

                            if (idUsuario.HasValue)
                            {
                                await Funciones.RegistrarBitacora(conexion, idUsuario.Value, Modulo, Parametros.AccionesBitacora.Crear, $"Envió comprobante de donación #{idDonacion}", ipUsuario, transaccion);
                            }
                            else
                            {
                                await Funciones.RegistrarBitacora(conexion, 0, Modulo, Parametros.AccionesBitacora.Crear, $"Envió comprobante de donación #{idDonacion}", ipUsuario, transaccion);
                            }

                            await transaccion.CommitAsync();

                            string urlAdmin = Url.Action("Autorizaciones", "Donaciones", null, Request.Scheme);
                            string montoTexto = montoDonado.HasValue ? montoDonado.Value.ToString("C2") : "No especificado";

                            string htmlComentario = string.IsNullOrEmpty(comentarioDonante) ? "" : $@"
                        <div style='background-color: #e9ecef; border-left: 4px solid #6c757d; padding: 12px; margin-top: 15px; border-radius: 4px;'>
                            <p style='margin: 0; font-size: 14px; font-style: italic; color: #495057;'>
                                <strong>Comentario del donante:</strong><br/>
                                ""{comentarioDonante}""
                            </p>
                        </div>";

                            string htmlSoporte = $@"
                    <div style='font-family: Arial, Helvetica, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
                        <div style='background-color: #0d6efd; padding: 20px; text-align: center; color: #ffffff;'>
                            <h2 style='margin: 0; font-size: 24px; font-weight: 600;'>🔔 Nueva Ofrenda Pendiente</h2>
                        </div>
                        <div style='padding: 30px; background-color: #ffffff; color: #333333;'>
                            <p style='font-size: 16px; line-height: 1.6; margin-top: 0;'>Hola, <strong>Equipo de Soporte</strong>:</p>
                            <p style='font-size: 16px; line-height: 1.6;'>El sistema ha registrado la recepción de un nuevo comprobante de donación.</p>
                            <div style='background-color: #f8f9fa; border-left: 5px solid #ffc107; padding: 18px; margin: 25px 0; border-radius: 4px;'>
                                <ul style='margin: 0; padding-left: 20px; line-height: 1.8; font-size: 16px;'>
                                    <li><strong>Folio de Rastreo:</strong> #{idDonacion}</li>
                                    <li><strong>Monto Reportado:</strong> <span style='color: #198754; font-weight: bold;'>{montoTexto}</span></li>
                                    <li><strong>Fecha de Registro:</strong> {DateTime.Now.ToString("dd/MM/yyyy HH:mm")}</li>
                                </ul>
                                {htmlComentario}
                            </div>
                            <div style='text-align: center; margin-top: 35px; margin-bottom: 20px;'>
                                <a href='{urlAdmin}' style='background-color: #ffc107; color: #212529; padding: 14px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; font-size: 16px; display: inline-block;'>
                                    🔍 Ir al Panel de Autorizaciones
                                </a>
                            </div>
                        </div>
                    </div>";

                            await Funciones.EnviarAlertaPorBaseDatos(_configuration, "NUEVA_DONACION", $"Nueva Ofrenda Pendiente #{idDonacion}", htmlSoporte);
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }

                MostrarMensaje("¡Muchas Gracias!", "Tu comprobante fue enviado con éxito.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al guardar donación: " + ex.Message);
                MostrarMensaje("Error del Sistema", "Hubo un problema al procesar el comprobante.", TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }


        [Authorize]
        public async Task<IActionResult> VerComprobantePendiente(int id)
        {
            int idUser = int.Parse(User.FindFirst("IdUsuario")!.Value);
            bool esAdmin = User.TienePermiso(Modulo, Parametros.Permisos.Crear);

            using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
            {
                await con.OpenAsync();

                var cmd = new NpgsqlCommand(@"SELECT ""Comprobante_Bytea"", ""Formato_Imagen"", ""Id_Usuario"" 
                                      FROM ""Sist_Aportaciones"" 
                                      WHERE ""Id_Donacion"" = @id", con);
                cmd.Parameters.AddWithValue("@id", id);

                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        int? idDueno = r["Id_Usuario"] as int?;

                        if (!esAdmin && idDueno != idUser)
                        {
                            return Unauthorized("No tienes permisos para ver este comprobante.");
                        }

                        if (r["Comprobante_Bytea"] != DBNull.Value)
                        {
                            byte[] bytes = (byte[])r["Comprobante_Bytea"];
                            if (bytes.Length > 0)
                                return File(bytes, r["Formato_Imagen"].ToString());
                        }
                    }
                }
            }
            return NotFound();
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Dictaminar(int idDonacion, decimal montoReportado, decimal montoValidado, bool esAprobado, string comentarios, string folioBancario)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para realizar esta acción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            if (esAprobado && montoValidado <= 0)
            {
                MostrarMensaje("Error", "El monto validado debe ser mayor a cero.", TipoMensaje.Error);
                return RedirectToAction("Autorizaciones");
            }

            if (esAprobado && string.IsNullOrWhiteSpace(folioBancario))
            {
                MostrarMensaje("Datos Faltantes", "El Folio del Banco es obligatorio para aprobar la donación.", TipoMensaje.Error);
                return RedirectToAction("Autorizaciones");
            }

            int idAdmin = int.Parse(User.FindFirst("IdUsuario")!.Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
            bool huboCambios = (montoReportado != montoValidado) && montoReportado != 0;
            string folioLimpio = folioBancario?.Trim() ?? "";

            try
            {
                using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await con.OpenAsync();
                    using (var transaccion = await con.BeginTransactionAsync())
                    {
                        try
                        {
                            string estatusActual = "";
                            using (var cmdVal = new NpgsqlCommand(@"SELECT ""Estatus"" FROM ""Sist_Aportaciones"" WHERE ""Id_Donacion"" = @id FOR UPDATE", con, transaccion))
                            {
                                cmdVal.Parameters.AddWithValue("@id", idDonacion);
                                var resEstatus = await cmdVal.ExecuteScalarAsync();
                                if (resEstatus == null) throw new Exception("La donación no existe.");
                                estatusActual = resEstatus.ToString();
                            }

                            if (estatusActual != "Pendiente")
                                throw new InvalidOperationException("Esta donación ya fue procesada o está en otro estado.");

                            if (esAprobado && !huboCambios)
                            {
                                await ProcesarTraspasoACaja(con, transaccion, idDonacion, montoValidado, idAdmin, comentarios, true, ipUsuario, folioLimpio);

                                await transaccion.CommitAsync();
                                MostrarMensaje("Aprobado", "La donación pasó directamente a Tesorería.", TipoMensaje.Exito);
                            }
                            else
                            {
                                string sql = @"UPDATE ""Sist_Aportaciones"" 
                                       SET ""Id_Usuario_Pre_Revisor"" = @admin, 
                                           ""Fecha_Pre_Revision"" = NOW(), 
                                           ""Monto_Validado"" = @monto, 
                                           ""Notas_Admin"" = @notas,
                                           ""Folio_Bancario"" = @folio
                                       WHERE ""Id_Donacion"" = @id";

                                using (var cmd = new NpgsqlCommand(sql, con, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@admin", idAdmin);
                                    cmd.Parameters.AddWithValue("@monto", esAprobado ? montoValidado : -1m);
                                    cmd.Parameters.AddWithValue("@notas", (esAprobado ? "[MODIFICADO] " : "[RECHAZADO] ") + comentarios);
                                    cmd.Parameters.AddWithValue("@folio", esAprobado ? folioLimpio : (object)DBNull.Value);
                                    cmd.Parameters.AddWithValue("@id", idDonacion);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                string accionTxt = esAprobado ? "Modificó y Pre-Aprobó" : "Pre-Rechazó";
                                await Funciones.RegistrarBitacora(con, idAdmin, Modulo, Parametros.AccionesBitacora.Editar, $"{accionTxt} donación #{idDonacion} (Pendiente de 2da Validación). Folio propuesto: {folioLimpio}", ipUsuario, transaccion);

                                await transaccion.CommitAsync();

                                // =======================================================
                                // BLOQUE DE CORREO RESTAURADO
                                // =======================================================
                                string accionHeader = esAprobado ? "⚠️ Donación Modificada" : "🛑 Donación Pre-Rechazada";
                                string colorHeader = esAprobado ? "#fd7e14" : "#dc3545";
                                string textoAccion = esAprobado ? "modificada" : "rechazada";
                                string montoValidadoTxt = esAprobado ? montoValidado.ToString("C2") : "N/A (Rechazado)";
                                string folioHtml = esAprobado ? $"<li><strong>Folio Propuesto:</strong> {folioLimpio}</li>" : "";
                                string urlAdmin = Url.Action("Autorizaciones", "Donaciones", null, Request.Scheme);

                                string htmlSegundaValidacion = $@"
                                <div style='font-family: Arial, Helvetica, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
                                    <div style='background-color: {colorHeader}; padding: 20px; text-align: center; color: #ffffff;'>
                                        <h2 style='margin: 0; font-size: 22px; font-weight: 600;'>{accionHeader}</h2>
                                    </div>
                                    <div style='padding: 30px; background-color: #ffffff; color: #333333;'>
                                        <p style='font-size: 16px; margin-top: 0;'>Hola, <strong>Equipo Revisor</strong>:</p>
                                        <p style='font-size: 16px; line-height: 1.6;'>Una donación ha sido <strong>{textoAccion}</strong> durante su primera revisión y ha sido enviada a la fila de Segunda Validación.</p>
                                        <div style='background-color: #f8f9fa; border-left: 5px solid {colorHeader}; padding: 18px; margin: 25px 0; border-radius: 4px;'>
                                            <p style='margin: 0 0 10px 0; font-size: 14px; color: #555;'><strong>Detalles del Dictamen:</strong></p>
                                            <ul style='margin: 0; padding-left: 20px; line-height: 1.8; font-size: 15px;'>
                                                <li><strong>ID Donación:</strong> #{idDonacion}</li>
                                                {folioHtml}
                                                <li><strong>Monto Original:</strong> {montoReportado:C2}</li>
                                                <li><strong>Monto Validado:</strong> <strong>{montoValidadoTxt}</strong></li>
                                                <li><strong>Motivo / Notas:</strong> <em style='color:#555;'>""{comentarios}""</em></li>
                                            </ul>
                                        </div>
                                        <p style='font-size: 15px; color: #555555; line-height: 1.5;'>
                                            Se requiere que un administrador distinto ingrese al panel para evaluar los comentarios y confirmar o descartar este movimiento.
                                        </p>
                                        <div style='text-align: center; margin-top: 30px; margin-bottom: 10px;'>
                                            <a href='{urlAdmin}' style='background-color: #343a40; color: #ffffff; padding: 14px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; font-size: 15px; display: inline-block;'>
                                                ⚖️ Ir a Segunda Validación
                                            </a>
                                        </div>
                                    </div>
                                    <div style='background-color: #f1f1f1; padding: 12px; text-align: center; font-size: 12px; color: #777777; border-top: 1px solid #e0e0e0;'>
                                        <p style='margin: 0;'>Mensaje automático del Sistema de Aportaciones AJP.</p>
                                    </div>
                                </div>";

                                await Funciones.EnviarAlertaPorBaseDatos(_configuration, "DONACION_2DA_VALIDACION", $"{accionHeader} #{idDonacion}", htmlSegundaValidacion);

                                MostrarMensaje("En Revisión", "Al haber modificaciones/rechazo, se ha enviado a Segunda Validación.", TipoMensaje.Alerta);
                            }
                        }
                        catch (InvalidOperationException ioe)
                        {
                            await transaccion.RollbackAsync();
                            MostrarMensaje("Aviso", ioe.Message, TipoMensaje.Alerta);
                            return RedirectToAction("Autorizaciones");
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
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Autorizaciones");
        }

        [Authorize]
        public async Task<IActionResult> Autorizaciones()
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para acceder a esta sección.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<RevisionDonacionViewModel>();

            using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
            {
                await con.OpenAsync();
                string sql = @"
            SELECT d.*, 
                   u.""NombreCompleto"" AS ""Donante"", u.""Email"",
                   u2.""NombreCompleto"" AS ""PreRevisor""
            FROM ""Sist_Aportaciones"" d
            LEFT JOIN ""Sist_Usuarios"" u ON d.""Id_Usuario"" = u.""Id_Usuario""
            LEFT JOIN ""Sist_Usuarios"" u2 ON d.""Id_Usuario_Pre_Revisor"" = u2.""Id_Usuario""
            WHERE d.""Estatus"" = 'Pendiente' 
            ORDER BY d.""Fecha_Registro"" ASC";

                using (var cmd = new NpgsqlCommand(sql, con))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    while (await r.ReadAsync())
                    {
                        lista.Add(new RevisionDonacionViewModel
                        {
                            IdDonacion = (int)r["Id_Donacion"],
                            Fecha = (DateTime)r["Fecha_Registro"],
                            Donante = r["Donante"] == DBNull.Value ? "Donante Anónimo" : r["Donante"].ToString(),
                            Email = r["Email"] == DBNull.Value ? "" : r["Email"].ToString(),
                            MontoOriginal = r["Monto"] == DBNull.Value ? 0m : (decimal)r["Monto"],
                            MontoValidado = r["Monto_Validado"] == DBNull.Value ? (decimal?)null : (decimal)r["Monto_Validado"],
                            Requiere2daValidacion = r["Id_Usuario_Pre_Revisor"] != DBNull.Value,
                            PreRevisor = r["PreRevisor"] == DBNull.Value ? "" : r["PreRevisor"].ToString(),
                            IdUsuarioPreRevisor = r["Id_Usuario_Pre_Revisor"] != DBNull.Value ? (int)r["Id_Usuario_Pre_Revisor"] : (int?)null,
                            Notas = r["Notas_Admin"].ToString(),
                            ComentarioDonante = r["Comentario_Donante"] == DBNull.Value ? "" : r["Comentario_Donante"].ToString(),
                            FolioBancario = r["Folio_Bancario"] != DBNull.Value ? r["Folio_Bancario"].ToString() : ""
                        });
                    }
                }
            }
            return View(lista);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SegundaValidacion(int idDonacion, bool confirmar)
        {
            if (!User.TienePermiso(Modulo, Parametros.Permisos.Crear))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para realizar esta acción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            int idAdminRevisor = int.Parse(User.FindFirst("IdUsuario")!.Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var con = new NpgsqlConnection(_configuration.GetConnectionString("MiConexion")))
                {
                    await con.OpenAsync();
                    using (var trans = await con.BeginTransactionAsync())
                    {
                        try
                        {
                            decimal montoValidado = 0;
                            string emailDonante = "";
                            string nombreDonante = "";
                            string notasAdmin = "";
                            string folioBancario = "";
                            bool eraRechazo = false;
                            int idPreRevisor = 0;
                            string estatusActual = "";

                            using (var cmdGet = new NpgsqlCommand(@"SELECT d.""Monto_Validado"", d.""Notas_Admin"", d.""Id_Usuario_Pre_Revisor"", d.""Estatus"", d.""Folio_Bancario"", u.""Email"", u.""NombreCompleto"" 
                                        FROM ""Sist_Aportaciones"" d 
                                        LEFT JOIN ""Sist_Usuarios"" u ON d.""Id_Usuario"" = u.""Id_Usuario"" 
                                        WHERE d.""Id_Donacion"" = @id FOR UPDATE OF d", con, trans))
                            {
                                cmdGet.Parameters.AddWithValue("@id", idDonacion);
                                using (var r = await cmdGet.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        estatusActual = r["Estatus"].ToString();
                                        montoValidado = r["Monto_Validado"] != DBNull.Value ? (decimal)r["Monto_Validado"] : 0;
                                        notasAdmin = r["Notas_Admin"]?.ToString() ?? "";
                                        folioBancario = r["Folio_Bancario"]?.ToString() ?? "";
                                        emailDonante = r["Email"] == DBNull.Value ? "" : r["Email"].ToString();
                                        nombreDonante = r["NombreCompleto"] == DBNull.Value ? "Anónimo" : r["NombreCompleto"].ToString();
                                        eraRechazo = montoValidado < 0;
                                        idPreRevisor = r["Id_Usuario_Pre_Revisor"] != DBNull.Value ? (int)r["Id_Usuario_Pre_Revisor"] : 0;
                                    }
                                    else throw new Exception("Donación no encontrada.");
                                }
                            }

                            if (estatusActual != "Pendiente")
                                throw new InvalidOperationException("Esta donación ya no está pendiente de validación.");

                            if (idAdminRevisor == idPreRevisor && confirmar)
                            {
                                MostrarMensaje("Acceso Denegado", "Por políticas de seguridad, no puedes confirmar una evaluación que tú mismo realizaste.", TipoMensaje.Error);
                                return RedirectToAction("Autorizaciones");
                            }

                            if (!confirmar)
                            {
                                using (var cmdRev = new NpgsqlCommand(@"UPDATE ""Sist_Aportaciones"" SET ""Id_Usuario_Pre_Revisor"" = NULL, ""Monto_Validado"" = NULL, ""Notas_Admin"" = NULL, ""Folio_Bancario"" = NULL WHERE ""Id_Donacion"" = @id", con, trans))
                                {
                                    cmdRev.Parameters.AddWithValue("@id", idDonacion);
                                    await cmdRev.ExecuteNonQueryAsync();
                                }

                                await Funciones.RegistrarBitacora(con, idAdminRevisor, Modulo, Parametros.AccionesBitacora.Editar, $"Descartó la pre-revisión de la donación #{idDonacion}", ipUsuario, trans);

                                await trans.CommitAsync();
                                MostrarMensaje("Revertido", "La evaluación previa fue cancelada. Regresó a la fila normal.", TipoMensaje.Alerta);
                                return RedirectToAction("Autorizaciones");
                            }

                            if (eraRechazo)
                            {
                                using (var cmdRechazo = new NpgsqlCommand(@"UPDATE ""Sist_Aportaciones"" SET ""Estatus"" = 'Rechazado', ""Id_Usuario_Revisor"" = @admin, ""Fecha_Revision"" = NOW() WHERE ""Id_Donacion"" = @id", con, trans))
                                {
                                    cmdRechazo.Parameters.AddWithValue("@admin", idAdminRevisor);
                                    cmdRechazo.Parameters.AddWithValue("@id", idDonacion);
                                    await cmdRechazo.ExecuteNonQueryAsync();
                                }

                                await Funciones.RegistrarBitacora(con, idAdminRevisor, Modulo, Parametros.AccionesBitacora.Editar, $"Confirmó RECHAZO definitivo de donación #{idDonacion}", ipUsuario, trans);
                                await trans.CommitAsync();

                                // =======================================================
                                // BLOQUE DE CORREO RESTAURADO (RECHAZO AL DONANTE)
                                // =======================================================
                                if (!string.IsNullOrEmpty(emailDonante))
                                {
                                    string htmlRechazo = $@"
                                <h2 style='color:#dc3545;'>Aviso sobre tu Comprobante</h2>
                                <p>Hola <strong>{nombreDonante}</strong>,</p>
                                <p>Hemos revisado el comprobante de donación que nos enviaste recientemente, pero hemos encontrado un inconveniente y no pudimos procesarlo.</p>
                                <p><strong>Motivo de la administración:</strong></p>
                                <blockquote style='border-left: 4px solid #dc3545; padding-left: 15px; font-style: italic; color: #555;'>{notasAdmin}</blockquote>
                                <p>Por favor, acércate físicamente a la directiva para solucionar cualquier duda.</p>";

                                    await Funciones.EnviarCorreo(_configuration, emailDonante, "Aviso sobre tu donación", htmlRechazo);
                                }
                                // =======================================================

                                MostrarMensaje("Dictamen Final", "Donación RECHAZADA definitivamente.", TipoMensaje.Exito);
                            }
                            else
                            {
                                await ProcesarTraspasoACaja(con, trans, idDonacion, montoValidado, idAdminRevisor, notasAdmin, false, ipUsuario, folioBancario);

                                await trans.CommitAsync();
                                MostrarMensaje("Dictamen Final", "Donación APROBADA y registrada en Caja.", TipoMensaje.Exito);
                            }
                        }
                        catch (InvalidOperationException ioe)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Aviso", ioe.Message, TipoMensaje.Alerta);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw new Exception(ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Autorizaciones");
        }

        private async Task ProcesarTraspasoACaja(NpgsqlConnection con, NpgsqlTransaction transaccion, int idDonacion, decimal montoReal, int idAdminFinal, string notasAdicionales, bool esDirecto, string ipUsuario, string folioBancario)
        {
            byte[] archivoBytes = null;
            string formato = "";
            string donanteNombre = "Anónimo";
            string emailDonante = "";
            int? idProyectoAsignado = null;

            // VARIABLES ADICIONALES
            string comentarioDonante = "";
            int? idUsuarioDonante = null;
            decimal montoOriginal = 0;
            string tituloProyecto = "";

            string sqlExtract = @"SELECT d.""Comprobante_Bytea"", d.""Formato_Imagen"", d.""Id_Proyecto"", u.""NombreCompleto"", u.""Email"", d.""Comentario_Donante"", d.""Id_Usuario"", d.""Monto"", p.""Titulo"" 
                  FROM ""Sist_Aportaciones"" d 
                  LEFT JOIN ""Sist_Usuarios"" u ON d.""Id_Usuario"" = u.""Id_Usuario"" 
                  LEFT JOIN ""Fin_Proyectos"" p ON d.""Id_Proyecto"" = p.""Id_Proyecto""
                  WHERE d.""Id_Donacion"" = @id";

            using (var cmdEx = new NpgsqlCommand(sqlExtract, con, transaccion))
            {
                cmdEx.Parameters.AddWithValue("@id", idDonacion);
                using (var r = await cmdEx.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        archivoBytes = r["Comprobante_Bytea"] != DBNull.Value ? (byte[])r["Comprobante_Bytea"] : null;
                        formato = r["Formato_Imagen"].ToString();
                        idProyectoAsignado = r["Id_Proyecto"] != DBNull.Value ? (int)r["Id_Proyecto"] : (int?)null;
                        if (r["NombreCompleto"] != DBNull.Value) donanteNombre = r["NombreCompleto"].ToString();
                        if (r["Email"] != DBNull.Value) emailDonante = r["Email"].ToString();

                        if (r["Comentario_Donante"] != DBNull.Value) comentarioDonante = r["Comentario_Donante"].ToString();
                        if (r["Id_Usuario"] != DBNull.Value) idUsuarioDonante = (int)r["Id_Usuario"];
                        if (r["Monto"] != DBNull.Value) montoOriginal = (decimal)r["Monto"];
                        if (r["Titulo"] != DBNull.Value) tituloProyecto = r["Titulo"].ToString();
                    }
                }
            }

            int idNuevoArchivo = 0;
            if (archivoBytes != null && archivoBytes.Length > 0)
            {
                string extension = ".jpg";
                if (formato.Contains("png")) extension = ".png";
                else if (formato.Contains("pdf")) extension = ".pdf";
                else if (formato.Contains("webp")) extension = ".webp";

                string nombreArchivo = $"Comprobante_Donacion_{idDonacion}{extension}";

                string sqlFile = @"INSERT INTO ""Rec_Archivos"" 
                  (""Titulo"", ""Descripcion"", ""Tipo"", ""Contenido_Binario"", ""Descargas"", ""Origen"", ""Fecha_Creacion"", ""Id_Usuario_Carga"")
                  VALUES (@titulo, @desc, @tipo, @bytes, 0, 'Caja', NOW(), @uid)
                  RETURNING ""Id_Archivo""";

                using (var cmdFile = new NpgsqlCommand(sqlFile, con, transaccion))
                {
                    cmdFile.Parameters.AddWithValue("@titulo", nombreArchivo);
                    cmdFile.Parameters.AddWithValue("@desc", $"Donación #{idDonacion} de {donanteNombre}");
                    cmdFile.Parameters.AddWithValue("@tipo", formato);
                    cmdFile.Parameters.AddWithValue("@bytes", archivoBytes);
                    cmdFile.Parameters.AddWithValue("@uid", idAdminFinal);
                    idNuevoArchivo = (int)await cmdFile.ExecuteScalarAsync();
                }
            }

            int idMovimientoFinal = 0;

            if (idProyectoAsignado.HasValue && idProyectoAsignado.Value > 0)
            {
                string sqlProjCaja = @"INSERT INTO ""Fin_Proyectos_Caja"" 
                (""Id_Proyecto"", ""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Movimiento_En_Banco"", ""Folio_Bancario"")
                VALUES (@idp, @concepto, @monto, 'Ingreso', NOW(), @uid, @enBanco, @folioBanc)
                RETURNING ""Id_Movimiento_Proyecto""";

                using (var cmdCaja = new NpgsqlCommand(sqlProjCaja, con, transaccion))
                {
                    cmdCaja.Parameters.AddWithValue("@idp", idProyectoAsignado.Value);
                    cmdCaja.Parameters.AddWithValue("@concepto", $"Donación Verificada - {donanteNombre}");
                    cmdCaja.Parameters.AddWithValue("@monto", montoReal);
                    cmdCaja.Parameters.AddWithValue("@uid", idAdminFinal);
                    cmdCaja.Parameters.AddWithValue("@enBanco", _destinoDonacionEsBanco);
                    cmdCaja.Parameters.AddWithValue("@folioBanc", string.IsNullOrWhiteSpace(folioBancario) ? (object)DBNull.Value : folioBancario);
                    idMovimientoFinal = (int)await cmdCaja.ExecuteScalarAsync();
                }
            }
            else
            {
                string sqlCaja = @"INSERT INTO ""Fin_Caja"" 
                 (""Concepto"", ""Monto"", ""Tipo"", ""Fecha"", ""Id_Usuario"", ""Id_Archivo"", ""Movimiento_En_Banco"", ""Folio_Bancario"")
                 VALUES (@concepto, @monto, 'Ingreso', NOW(), @uid, @idArchivo, @enBanco, @folioBanc)
                 RETURNING ""Id_Movimiento""";

                using (var cmdCaja = new NpgsqlCommand(sqlCaja, con, transaccion))
                {
                    cmdCaja.Parameters.AddWithValue("@concepto", $"{sNombreConceptoDonacion} - {donanteNombre}");
                    cmdCaja.Parameters.AddWithValue("@monto", montoReal);
                    cmdCaja.Parameters.AddWithValue("@uid", idAdminFinal);
                    cmdCaja.Parameters.AddWithValue("@idArchivo", idNuevoArchivo > 0 ? idNuevoArchivo : (object)DBNull.Value);
                    cmdCaja.Parameters.AddWithValue("@enBanco", _destinoDonacionEsBanco);
                    cmdCaja.Parameters.AddWithValue("@folioBanc", string.IsNullOrWhiteSpace(folioBancario) ? (object)DBNull.Value : folioBancario);
                    idMovimientoFinal = (int)await cmdCaja.ExecuteScalarAsync();
                }
            }

            string sqlUpdate = @"UPDATE ""Sist_Aportaciones"" 
               SET ""Estatus"" = 'Verificado', 
                   ""Id_Usuario_Revisor"" = @admin, 
                   ""Fecha_Revision"" = NOW(), 
                   ""Monto_Validado"" = @monto,
                   ""Id_Archivo_Rec"" = @idArchivo,
                   ""Comprobante_Bytea"" = @bytesVacios,
                   ""Folio_Bancario"" = @folio
               WHERE ""Id_Donacion"" = @id";

            using (var cmdUpd = new NpgsqlCommand(sqlUpdate, con, transaccion))
            {
                cmdUpd.Parameters.AddWithValue("@admin", idAdminFinal);
                cmdUpd.Parameters.AddWithValue("@monto", montoReal);
                cmdUpd.Parameters.AddWithValue("@idArchivo", idNuevoArchivo > 0 ? idNuevoArchivo : (object)DBNull.Value);
                cmdUpd.Parameters.AddWithValue("@bytesVacios", new byte[0]);
                cmdUpd.Parameters.AddWithValue("@folio", string.IsNullOrWhiteSpace(folioBancario) ? (object)DBNull.Value : folioBancario);
                cmdUpd.Parameters.AddWithValue("@id", idDonacion);
                await cmdUpd.ExecuteNonQueryAsync();
            }

            string targetLog = idProyectoAsignado.HasValue ? "Libro Proyecto" : "Libro General";
            await Funciones.RegistrarBitacora(con, idAdminFinal, Modulo, Parametros.AccionesBitacora.Crear, $"Aprobó donación #{idDonacion}. Generó Ingreso #{idMovimientoFinal} en {targetLog}. Folio: {folioBancario}", ipUsuario, transaccion);

            if (!string.IsNullOrEmpty(emailDonante) && !esDirecto)
            {
                string asunto = "Aviso sobre tu donación";
                string htmlAprobacion = $@"
    <h2 style='color:#198754;'>Donación Aprobada con Modificación</h2>
    <p>Hola <strong>{donanteNombre}</strong>,</p>
    <p>Hemos procesado tu comprobante de donación. Tras una revisión, hemos registrado oficialmente tu Donación por la cantidad de <strong>${montoReal:N2}</strong>.</p>
    <p><strong>Nota de administración:</strong> <em>{notasAdicionales}</em></p>
    <p>Si tienes alguna duda sobre este ajuste, por favor acércate a la directiva. ¡Muchas gracias por tu apoyo!</p>";

                await Funciones.EnviarCorreo(_configuration, emailDonante, asunto, htmlAprobacion);
            }

            // ====================================================================
            // UBICACIÓN SEGURA: Pasamos 'montoOriginal' y 'tituloProyecto'
            // ====================================================================
            await EnviarComentarioAComunidad(con, transaccion, comentarioDonante, idUsuarioDonante, montoReal, tituloProyecto);
        }
        private async Task EnviarComentarioAComunidad(NpgsqlConnection con, NpgsqlTransaction transaccion, string comentario, int? idUsuario, decimal monto, string tituloProyecto)
        {
            // Si el donante no escribió ningún mensaje/comentario, salimos sin hacer nada
            if (string.IsNullOrWhiteSpace(comentario)) return;

            // Si hay un proyecto asignado, lo añadimos al texto
            string textoProyecto = string.IsNullOrWhiteSpace(tituloProyecto) ? "" : $" para *{tituloProyecto.Trim()}*";

            // Formateamos el contenido agregando el MONTO DONADO y el PROYECTO (si aplica)
            string contenidoMensaje = $"🙏 *Mensaje adjunto a un donativo de {monto:C2}{textoProyecto}:* \n\n\"{comentario.Trim()}\"";

            string sqlMsg = @"INSERT INTO ""Sist_Comunidad_Mensajes"" 
        (""Id_Usuario_Autor"", ""Tipo_Mensaje"", ""Destinatario"", ""Contenido"", ""Fecha_Creacion"", ""Fecha_Expiracion"", ""Estado"", ""Es_Anonimo"", ""Es_Institucional"") 
        VALUES (@idU, 'General', 'Directiva', @cont, NOW(), NOW() + INTERVAL '2 months', 'APR', TRUE, FALSE)";

            using (var cmdMsg = new NpgsqlCommand(sqlMsg, con, transaccion))
            {
                // Enviamos NULL real a PostgreSQL si la donación fue anónima (sin sesión)
                cmdMsg.Parameters.AddWithValue("@idU", idUsuario.HasValue ? (object)idUsuario.Value : DBNull.Value);
                cmdMsg.Parameters.AddWithValue("@cont", contenidoMensaje);

                await cmdMsg.ExecuteNonQueryAsync();
            }
        }
    }
}