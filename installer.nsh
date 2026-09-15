; During uninstall, give the user an explicit choice to remove the local library.
; The folder contains the SQLite catalog, collection memberships, settings, and cached thumbnails.
!macro customUnInstall
  MessageBox MB_YESNO|MB_ICONQUESTION "Remove all local Knowledge Vault data, including collections, catalog records, settings, and thumbnails? This cannot be undone." IDNO kv_keep_library_data
  RMDir /r "$APPDATA\PDFLibraryManager"
kv_keep_library_data:
!macroend
