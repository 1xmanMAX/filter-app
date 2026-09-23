# Filter App — Diseño

Fecha: 2026-09-23

## Objetivo

App de escritorio para Windows que actúa como **intermediario** entre el origen de unos archivos
(Telegram, Explorador, navegador, etc.) y una carpeta destino. El usuario reparte cada archivo sobre
una **tarjeta con nombre**; al soltarlo, el archivo se copia al destino renombrado con el nombre de
la tarjeta. Prioridades: simplicidad y rendimiento excepcional (arranque < 1 s, UI nunca bloqueada).

## Decisiones tomadas

| Tema | Decisión |
|---|---|
| Plataforma | Solo Windows. C# / WPF sobre .NET 10 (runtime 10.0.10 ya instalado; falta instalar el SDK). |
| Punto de intercepción | La ventana de la app (no se engancha al pegado del Explorador). |
| Origen de nombres de tarjeta | Escritos a mano: diálogo "+ Nombres", un nombre por línea. |
| Varios archivos a la vez | Van a la bandeja **Pendientes**; desde ahí se reparten uno a uno. |
| Un solo archivo arrastrado | Puede soltarse directo en una tarjeta o en Pendientes. |
| Copiar vs mover | Siempre **copiar**. El original nunca se toca. |
| Nombre final | `<nombre tarjeta><extensión original>`. Ej.: tarjeta "Factura Enero" + `foto.jpg` → `Factura Enero.jpg`. |
| Conflicto de nombre | No sobrescribir: `Nombre (2).ext`, `Nombre (3).ext`, … |
| Tarjeta llena | Marcada (verde ✔), rechaza nuevos archivos. Botón ✕ deshace: borra la copia y libera la tarjeta. |
| Destino | **Automático**: la última ventana del Explorador que recibe el foco define el destino (pestaña activa en Win11). |
| Candado 🔒 | Congela el destino actual; el detector se ignora mientras esté activo. |
| Persistencia | Tarjetas, estado, destino y candado se guardan y restauran entre sesiones. |

## Interfaz (una sola ventana)

```
┌──────────────────────────────────────────────────────────┐
│ Destino: F:\...\Clientes  🔒                Pendientes: 3 │
├──────────────┬───────────────────────────────────────────┤
│ PENDIENTES   │  TARJETAS                     [+ Nombres] │
│ 📄 foto1.jpg │  [Factura Enero]  [✔ Contrato Juan ✕]  ... │
│ 📄 doc.pdf   │   (libre)          doc.pdf                 │
│ (arrastra o  │                                           │
│  Ctrl+V)     │                                           │
└──────────────┴───────────────────────────────────────────┘
```

- **Barra superior:** ruta destino (grande, legible), toggle candado, contador de pendientes.
  Sin destino válido, las tarjetas no aceptan archivos y la barra lo indica.
- **Pendientes:** acepta drop y Ctrl+V (Ctrl+V funciona con foco en cualquier parte de la ventana).
  Cada elemento es arrastrable hacia una tarjeta. Botón para quitar un pendiente.
- **Tarjetas:** grilla (WrapPanel) con plantillas ligeras; cientos de tarjetas sin problema.
  - Libre: gris; resalta al pasar un archivo encima (DragOver).
  - Llena: verde, ✔, nombre del archivo original, botón ✕. DragOver muestra "no permitido".
  - Botón para eliminar una tarjeta (solo si está libre). Botón "Limpiar llenas" para empezar otra tanda.
- **+ Nombres:** diálogo con caja de texto multilínea; cada línea no vacía (recortada) crea una tarjeta.
  Nombres duplicados se permiten (el conflicto se resuelve con `(2)` al copiar).

## Componentes

1. **FolderWatcher** (detector de carpeta)
   - `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` → callback solo al cambiar la ventana activa; cero polling.
   - Si la ventana es `CabinetWClass` (Explorador), la busca en `Shell.Application.Windows()` por HWND.
     En Win11 con pestañas varias comparten HWND: se elige la pestaña cuyo `ShellTabWindowClass` es visible.
   - Obtiene la ruta vía `Document.Folder.Self.Path`. Ignora rutas virtuales (Este equipo, Papelera, etc.).
   - Al pasar el foco a otra ventana (Telegram, la propia app…), relee la última ventana del Explorador
     usada, para captar la navegación hecha dentro de ella. No hace nada si el candado está activo.
   - Expone evento `DestinationChanged(string path)`.
2. **FileIntake** (entrada)
   - Drop / portapapeles con `CF_HDROP` (`FileDrop`) → rutas reales, sin copiar nada todavía.
   - Formatos virtuales (`FileGroupDescriptorW` + `FileContents`, p. ej. adjuntos de Outlook) y
     bitmaps del portapapeles → se materializan en `%TEMP%\FilterApp\<guid>\` y se tratan como rutas reales.
   - Carpetas soltadas: se rechazan con aviso (fuera de alcance).
   - Resultado: lista de `PendingFile { SourcePath, DisplayName, IsTemp }` añadida a Pendientes.
3. **NameResolver** (lógica pura, sin E/S salvo `exists`)
   - Sanea el nombre de tarjeta: reemplaza `\ / : * ? " < > |` por `_`, recorta espacios y puntos finales,
     evita nombres reservados (`CON`, `PRN`, `NUL`, `COM1`…).
   - Añade la extensión original tal cual; sin extensión si el original no tiene.
   - Resuelve conflicto con sufijo ` (n)` usando un predicado `exists(path)` inyectado (testeable).
4. **Copier**
   - Copia asíncrona (`FileStream` con buffer grande, `FileOptions.Asynchronous | SequentialScan`)
     para que la UI no se bloquee con archivos grandes. Durante la copia la tarjeta queda en estado "copiando".
   - Éxito → tarjeta llena, pendiente eliminado, temporal borrado si `IsTemp`.
   - Error → tarjeta libre, archivo vuelve a Pendientes, aviso tipo toast no bloqueante.
   - Deshacer (✕) → borra el archivo destino (si sigue existiendo) y libera la tarjeta.
     El original no vuelve a Pendientes (se puede volver a arrastrar desde el origen).
5. **StateStore**
   - JSON en `%APPDATA%\FilterApp\state.json`: tarjetas (nombre, estado, archivo destino, nombre original),
     destino, candado. Guardado con debounce tras cada cambio y al cerrar (escritura atómica: tmp + rename).
   - Pendientes no se persisten (pueden ser temporales).

## Flujo de datos

```
Explorador (foco) ──► FolderWatcher ──► Destino (UI)
Telegram/otro ──drop/Ctrl+V──► FileIntake ──► Pendientes
Pendiente ──drop sobre tarjeta──► NameResolver ──► Copier ──► Carpeta destino
                                                     └──► Tarjeta llena ──► StateStore
```

## Rendimiento

- WPF puro; sin librerías externas.
- Detector basado en eventos del sistema, no temporizadores.
- Copias en segundo plano; la UI solo actualiza estado.
- Publicación `win-x64`, framework-dependent (usa el runtime 10 ya instalado), ReadyToRun para arranque rápido.

## Pruebas

- Automáticas (xUnit): NameResolver (extensiones, sin extensión, duplicados `(2)/(3)`, caracteres
  inválidos, nombres reservados), Copier (copiar, conflicto, error, deshacer) contra carpetas temporales,
  StateStore (ida y vuelta del JSON).
- Manuales: arrastrar desde Telegram (uno y varios), Ctrl+V desde el Explorador, clic en Explorador
  con y sin candado, pestañas de Win11, archivo grande (>1 GB) sin congelar la UI.

## Fuera de alcance

- Engancharse al pegado nativo del Explorador.
- Mac / Linux.
- Mover (en vez de copiar), carpetas completas, cargar nombres desde Excel/CSV.
