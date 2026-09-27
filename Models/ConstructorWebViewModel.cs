using System;
using System.Collections.Generic;

namespace RedAJP.Models
{
    public class ConstructorWebViewModel : IglesiaWebPublicaViewModel
    {
        public int IdIglesia { get; set; }
        public string sIdIglesia { get; set; }

        // Cadenas JSON para el motor visual
        public string EstilosGlobalesJson { get; set; }
        public string LayoutDataJson { get; set; }

        // Bandera para saber qué estamos viendo
        public bool UsandoDatosPrueba { get; set; }
    }
}