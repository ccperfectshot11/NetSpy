# NetSpy Designer

Program separat pentru a proiecta aspectul NetSpy: culori, fonturi, colțuri rotunjite (WinUI),
stilul taburilor, bara de titlu, densitatea arborelui. În stânga e o previzualizare vie a ferestrei NetSpy.

- Rulare: `dotnet run -c Release` (din acest folder).
- Designul se salvează automat în `%LocalAppData%\NetSpy\Designer\autosave.netspy-design.json`;
  „Salvează ca…” scrie un fișier `*.netspy-design.json`.
- Verificare fără interfață: `NetSpy.Designer.exe --snapshot poza.png [indexTemă] [Cheie=valoare ...]`.

Iconițele din `Icons\` sunt din ILSpy (MIT, vezi `Icons\LICENSE-ILSpy.txt`), la rândul lor din
Visual Studio 2017 Image Library. Sunt randate cu SharpVectors (BSD-3-Clause).
