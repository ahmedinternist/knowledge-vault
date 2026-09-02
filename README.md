# Knowledge Vault

A self-contained HTML prototype for an offline-first medical document library, inspired by the supplied Stitch project.

## Run it locally

Requirements: Node.js 20+ and the .NET 8 SDK. Install dependencies with `npm ci`, then run `npm start`. The PDF indexer is rebuilt automatically from `pdf-indexer/Program.cs` before Electron starts.

## Included

- Dark teal and gold Digital Sanctuary design system
- Responsive library dashboard with grid and list modes
- Search, specialty collections, favourites, read-state and recent filters
- Native Windows file/folder library: stores only source paths, never copies documents
- Automatic folder scan for PDFs, EPUBs, MOBI, FB2, TXT, DOC, and DOCX files
- Remove missing source paths and open actual files in their default Windows applications
- Focused in-app reader preview
- Keyboard shortcut: `Ctrl/Cmd + K` focuses search

## Build a Windows installer

Run `npm run build:installer`. It rebuilds the self-contained PDF indexer and then creates an NSIS Windows setup program. The installer is written to `dist/` by default.

Generated directories (`backend/pdf-indexer`, `node_modules`, `.nuget`, and `dist*`) are intentionally excluded from Git. They are recreated by the build commands.
