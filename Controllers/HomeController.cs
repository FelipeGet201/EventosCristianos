using Microsoft.AspNetCore.Mvc;

namespace RedAJP.Controladores
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            //Abrir la vista tienda
            return RedirectToAction("Index", "Tienda");
        }
        public IActionResult Login()
        {
            return View();
        }
        public IActionResult Registro()
        {
            return View();
        }
        public IActionResult Donaciones()
        {
            return View();
        }
        public IActionResult Nosotros()
        {
            return View();
        }
        public IActionResult Reuniones()
        {
            return View();
        }
        public IActionResult Eventos()
        {
            return View();
        }
        public IActionResult Materiales()
        {
            return View();
        }
        public IActionResult Formatos()
        {
            return View();
        }
        public IActionResult Caja()
        {
            return View();
        }
        public IActionResult Rifas()
        {
            return View();
        }
        public IActionResult Usuarios()
        {
            return View();
        }
        public IActionResult Perfiles()
        {
            return View();
        }
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View();
        }
    }
}
