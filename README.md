# Rotula

[![Build](https://github.com/glenntosco/Rotula/actions/workflows/build.yml/badge.svg)](https://github.com/glenntosco/Rotula/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Language: [English](#english) | [Espanol](#espanol)

<!-- TODO(screenshots): add a screenshot of the designer here, saved as docs/images/designer.png. It must be captured
     from a clean desktop so that no unrelated windows show through; none is included until then. -->

## English

Rotula is a free, open source, **offline-first label designer for Windows**. Draw a label, preview it, merge data from a
spreadsheet and print ZPL-compatible output to your label printer. It makes **no network calls and sends no telemetry**.

Rotula is built by P4 Software, MIT licensed, on .NET 10 and WPF. The interface is available in English and Spanish and
follows your Windows language (you can switch it from the View menu).

### Features

- Visual designer with rulers, grid, snapping, guides, align and distribute, layers, undo and redo.
- Elements: text (including wrapped text blocks), data fields, barcodes, QR codes, boxes, lines, circles and images.
- Barcodes: Code 128, Code 39, Code 93, EAN-13, UPC-A, Interleaved 2 of 5, QR Code, Data Matrix and PDF417, with
  validation that tells you what is wrong with the data in plain language.
- Variables written as `{{name}}`, merged from CSV or Excel (`.xlsx`) files, with serial-number counters and batch output.
- Live preview and a read-only ZPL panel you can copy from. Export as ZPL, PNG or PDF.
- ZPL import: open existing `.zpl` files, with a warning for everything that could not be interpreted exactly.
- Printing to any Windows printer queue (USB, shared, network) using raw data, or straight to a network printer on
  TCP port 9100, with printer profiles. See [docs/printing.md](docs/printing.md).
- Your own `.rotula` files: plain JSON, versioned, saved safely (a failed save never destroys the previous file).
- Light theme, Spanish and English, no installation required (portable build available).

The commands Rotula reads and writes are listed in [docs/zpl-support.md](docs/zpl-support.md).

### Install

Download the latest release from the [Releases page](https://github.com/glenntosco/Rotula/releases):

- **Portable**: `Rotula-<version>-win-x64-portable.zip`. Unzip anywhere and double-click `Rotula.App.exe`. No .NET needed.
- **Installer**: `Rotula-<version>-win-x64-setup.exe`. Installs for your user only; no administrator rights needed.
- **winget**: once the package is accepted, `winget install P4Software.Rotula`.

Requires Windows 10 or 11, 64-bit.

#### Windows SmartScreen warning

Rotula releases are **not code-signed** (signing certificates are not free and this is a volunteer project). The first time
you run the download, Windows SmartScreen may say "Windows protected your PC". Choose **More info**, then **Run anyway**.
You can verify the download against the `SHA256SUMS.txt` file attached to each release, or build it yourself from source
(below). The portable build also unpacks its native graphics library to a temporary folder the first time it starts, so the
first launch can take a moment longer.

### Build from source

You need the .NET 10 SDK on Windows.

```
dotnet build Rotula.sln -c Release
dotnet test  Rotula.sln -c Release
dotnet run --project src/Rotula.App
```

Layout: `Rotula.Core` (model, ZPL generator and parser, data merge; no UI), `Rotula.Rendering` (preview and export),
`Rotula.Printing` (spooler and TCP printing), `Rotula.App` (WPF) and `tests/Rotula.Tests`. CI requires at least 80% line
coverage on `Rotula.Core`. See [CONTRIBUTING.md](CONTRIBUTING.md).

### Known limitations

Please read these before relying on Rotula for production labels. **Always print a test label first.**

- **The preview is approximate.** Text is drawn with a bundled stand-in typeface, not the printer's own fonts, so letter
  shapes and line breaks can differ slightly from the printed label. The app shows a notice about this.
- **Bitmap fonts and `FontWidth`**: the printer's built-in bitmap fonts are not reproduced; width scaling is only
  approximated in the preview.
- **`^PO` (print orientation) is not previewed.** It is written to the ZPL but the preview always shows the normal orientation.
- **Variables do not survive a ZPL round trip.** ZPL cannot express `{{name}}`; ZPL imported into Rotula is plain text, and
  variables exist only in `.rotula` files.
- Commands outside [the support matrix](docs/zpl-support.md) are kept as raw ZPL where possible but are not interpreted or
  previewed.
- Rotula is tested against decoders and golden files, not against every printer model.
- ZPL output only. Windows only.

### Roadmap

Not supported today, and planned or open for discussion:

- EPL, TSPL and CPCL output
- RFID encoding
- Database connections (CSV and Excel only for now)
- A label template marketplace
- Cloud sync
- macOS and Linux
- Multi-label sheet layouts

### Contributing, security and conduct

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Changes are
tracked in [CHANGELOG.md](CHANGELOG.md); third-party licenses are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

### License

MIT. See [LICENSE](LICENSE). Copyright (c) 2026 P4 Software.

---

## Espanol

Rotula es un disenador de etiquetas gratuito, de codigo abierto y **que funciona sin conexion, para Windows**. Disena una
etiqueta, previsualizala, combina datos desde una hoja de calculo e imprime salida compatible con ZPL en tu impresora de
etiquetas. **No hace llamadas de red ni envia telemetria.**

Rotula lo desarrolla P4 Software con licencia MIT, sobre .NET 10 y WPF. La interfaz esta en espanol e ingles y sigue el idioma
de Windows (se puede cambiar desde el menu Ver).

### Funciones

- Disenador visual con reglas, cuadricula, ajuste, guias, alineacion y distribucion, capas, deshacer y rehacer.
- Elementos: texto (incluido texto en bloque), campos de datos, codigos de barras, codigos QR, cajas, lineas, circulos e imagenes.
- Codigos de barras: Code 128, Code 39, Code 93, EAN-13, UPC-A, Intercalado 2 de 5, QR, Data Matrix y PDF417, con validacion
  que explica en lenguaje sencillo que esta mal en los datos.
- Variables escritas como `{{nombre}}`, combinadas desde archivos CSV o Excel (`.xlsx`), con contadores de numeros de serie
  y salida por lotes.
- Vista previa en vivo y panel ZPL de solo lectura para copiar. Exporta a ZPL, PNG o PDF.
- Importacion de ZPL: abre archivos `.zpl` existentes, con un aviso por todo lo que no se pudo interpretar con exactitud.
- Impresion en cualquier cola de Windows (USB, compartida, de red) con datos sin procesar, o directo a una impresora de red por
  el puerto TCP 9100, con perfiles de impresora. Ver [docs/printing.md](docs/printing.md).
- Tus propios archivos `.rotula`: JSON simple, con version, guardados de forma segura.
- Tema claro, espanol e ingles, sin instalacion obligatoria (hay version portatil).

Los comandos que Rotula lee y escribe estan en [docs/zpl-support.md](docs/zpl-support.md).

### Instalacion

Descarga la ultima version desde la [pagina de Releases](https://github.com/glenntosco/Rotula/releases):

- **Portatil**: `Rotula-<version>-win-x64-portable.zip`. Descomprime en cualquier carpeta y haz doble clic en `Rotula.App.exe`.
  No necesita .NET.
- **Instalador**: `Rotula-<version>-win-x64-setup.exe`. Se instala solo para tu usuario, sin permisos de administrador.
- **winget**: cuando el paquete sea aceptado, `winget install P4Software.Rotula`.

Requiere Windows 10 u 11 de 64 bits.

#### Aviso de Windows SmartScreen

Las versiones de Rotula **no estan firmadas digitalmente** (los certificados de firma no son gratuitos y este es un proyecto
voluntario). La primera vez que ejecutes la descarga, SmartScreen puede mostrar "Windows protegio el equipo". Elige **Mas
informacion** y luego **Ejecutar de todas formas**. Puedes comprobar la descarga con el archivo `SHA256SUMS.txt` de cada release
o compilarla tu mismo desde el codigo fuente. La version portatil tambien descomprime su biblioteca grafica nativa en una
carpeta temporal la primera vez, asi que el primer inicio puede tardar un poco mas.

### Compilar desde el codigo fuente

Necesitas el SDK de .NET 10 en Windows.

```
dotnet build Rotula.sln -c Release
dotnet test  Rotula.sln -c Release
dotnet run --project src/Rotula.App
```

### Limitaciones conocidas

Lee esto antes de usar Rotula en etiquetas de produccion. **Imprime siempre una etiqueta de prueba primero.**

- **La vista previa es aproximada.** El texto se dibuja con una tipografia sustituta incluida, no con las fuentes de la
  impresora, asi que la forma de las letras y los saltos de linea pueden diferir un poco de la etiqueta impresa.
- **Fuentes de mapa de bits y `FontWidth`**: las fuentes internas de la impresora no se reproducen; el ancho solo se aproxima.
- **`^PO` (orientacion de impresion) no se previsualiza.** Se escribe en el ZPL, pero la vista previa siempre muestra la
  orientacion normal.
- **Las variables no sobreviven a un viaje de ida y vuelta por ZPL.** ZPL no puede expresar `{{nombre}}`; el ZPL importado es
  texto fijo y las variables solo existen en archivos `.rotula`.
- Los comandos fuera de [la tabla de compatibilidad](docs/zpl-support.md) se conservan como ZPL sin procesar cuando es posible,
  pero no se interpretan ni se previsualizan.
- Solo salida ZPL. Solo Windows.

### Hoja de ruta

Hoy no soportado; previsto o abierto a discusion:

- Salida EPL, TSPL y CPCL
- Codificacion RFID
- Conexiones a bases de datos (por ahora solo CSV y Excel)
- Mercado de plantillas de etiquetas
- Sincronizacion en la nube
- macOS y Linux
- Disenos de hojas con varias etiquetas

### Contribuir, seguridad y conducta

Ver [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md) y [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

### Licencia

MIT. Ver [LICENSE](LICENSE). Copyright (c) 2026 P4 Software.
