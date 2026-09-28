# Reglas Locales del Proyecto: Ministerio Corbán

## 1. Restricción de Compilación
* **PROHIBIDO COMPILAR:** No ejecutar comandos de compilación (como `dotnet build`, `dotnet publish`, `dotnet run`, `msbuild`, etc.).
* El usuario se encargará de compilar, probar y ejecutar el proyecto manualmente.

## 2. Restricción de Git
* **PROHIBIDO CONTACTAR O INTERACTUAR CON GIT:** No ejecutar comandos de Git (como `git status`, `git add`, `git commit`, `git push`, `git pull`, `git checkout`, etc.).
* La gestión del repositorio y control de versiones está reservada exclusivamente al usuario.

## 3. Alcance de Trabajo
* La labor del asistente es **únicamente de programador / desarrollador de software**:
  - Analizar código y arquitectura.
  - Escribir, modificar y refactorizar código (C#, HTML, Razor, SCSS, CSS, JS).
  - Diseñar e implementar soluciones de UI/UX y estilos.

## 4. Codificación Obligatoria de Archivos (Encoding)
* **UNICODE (UTF-8 CON FIRMA / BOM) - PÁGINA DE CÓDIGO 65001:**
  - Es de **SUMA IMPORTANCIA** que todo archivo nuevo o modificado (C#, Razor `.cshtml`, SCSS, CSS, JS, JSON, Markdown, etc.) se codifique y guarde siempre en **Unicode (UTF-8 con firma) - página de código 65001** (BOM: bytes iniciales `0xEF, 0xBB, 0xBF`).
  - No guardar archivos en UTF-8 sin firma ni en codificaciones incompatibles con Visual Studio.
