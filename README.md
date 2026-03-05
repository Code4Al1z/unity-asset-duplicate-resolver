# Asset Duplicate Resolver
**Unity Editor tool by [AL!Z (Aliz Pasztor)](https://alizpasztor.com)**

Scans one or two asset folders and categorises everything into duplicates, unique-used assets, and unused assets — then lets you migrate references and clean up safely, with full undo support.

Built because the standard Unity duplicate workflow involves a lot of manual GUID hunting. This automates the scan, shows you exactly what's safe to touch, and gives you a dry-run preview before committing any changes.

---

## Features

**Scanning**
- Content-based duplicate detection via MD5 hash — catches same-content, different-name files
- GUID collision detection — finds corrupted `.meta` files sharing a GUID
- Orphaned `.meta` file detection — finds `.meta` files with no matching asset
- Non-blocking chunked scan with live progress bar
- Write-time-invalidating dependency and hash cache — only rescans what changed
- Size threshold filter — hide assets below a configurable file size
- Single-folder mode — audit one folder for used vs. unused without a second folder

**Analysis**
- Asset diff window — side-by-side metadata, MD5 identity, pixel similarity %, waveform
- Dependency graph with configurable depth (1–10 levels)
- Scene cross-reference with click-through to GameObjects in any scene (opens additively)
- Hover asset preview panel

**Actions**
- Property-level reference swapping with full selective undo
- Auto-swap dry-run preview — shows exact file and property count before committing
- Export scan report to CSV
- Ignore list (folder-based, persistent)
- VCS checkout support (Perforce / Plastic SCM)
- Persistent settings via EditorPrefs

---

## Requirements
Developed and tested on Unity 2022+. Likely compatible with earlier versions — no cutting-edge APIs are used. 
No external packages required.

---

## Installation
1. Drop `AssetDuplicateResolver.cs` into any `Editor/` folder in your project
2. Open via **Tools → Asset Duplicate Resolver**

---

## License
Free to use in personal and commercial projects. Please do not redistribute or resell.

---

*I build tools, plugins, and audio systems that other engineers find too complex or too weird.*  
*Game audio · DAW plugins · Live visualization · [alizpasztor.com](https://alizpasztor.com)*
