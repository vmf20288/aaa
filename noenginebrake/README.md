# No Engine Brake

Proyecto MVP creado con Next.js (App Router), TypeScript, ESLint y Tailwind CSS.

## Requisitos
- Node.js 18+

## Uso
1. Instala dependencias:
   ```bash
   npm install
   ```
2. Inicia el entorno de desarrollo:
   ```bash
   npm run dev
   ```
3. Abre `http://localhost:3000` en tu navegador.

## Secciones incluidas
- Inicio con accesos rápidos.
- Aprende, Recetas, Comunidad y Patrocinio listos como páginas base.

El contenido es placeholder y está listo para iterar.

## Instalación como PWA

La app ahora puede instalarse como aplicación en el dispositivo:

- **Android (Chrome/Edge/Firefox)**
  1. Abre `http://localhost:3000` o el dominio desplegado.
  2. Espera a que cargue y el service worker se registre.
  3. Toca el menú de opciones (⋮) y selecciona **Instalar aplicación** o **Añadir a pantalla de inicio**.
  4. Confirma el nombre y añade el icono. La PWA quedará accesible como una app independiente.

- **iOS (Safari)**
  1. Abre el sitio en Safari y deja que termine de cargar.
  2. Pulsa el botón **Compartir** y elige **Agregar a pantalla de inicio**.
  3. Confirma el nombre y toca **Agregar**. El icono aparecerá en el Springboard y abrirá la PWA en modo standalone.

Consejo: si actualizas la app, fuerza la recarga abriendo la PWA y realizando un “pull to refresh” para que el service worker obtenga la última versión.
